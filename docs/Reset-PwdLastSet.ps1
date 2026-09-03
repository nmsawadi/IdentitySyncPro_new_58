<#
.SYNOPSIS
    Restarts the password-age clock (pwdLastSet = now) for accounts whose passwords expired after
    the DONT_EXPIRE_PASSWORD flag was removed.

.DESCRIPTION
    Clearing "password never expires" makes the domain's maximum password age apply immediately.
    An account whose password was last set years ago is therefore already past that age the moment
    the flag is cleared, and the user is locked out of a password they never had reason to change.

    This script restarts that clock WITHOUT changing anyone's password: the user keeps their
    current password and gets a full maxPwdAge period before it expires.

    HOW IT WORKS -- and why it is done in two steps:
    Active Directory does not accept an arbitrary date in pwdLastSet. Only two values are legal:
        0   -> "user must change password at next logon"
        -1  -> "the password was set right now"
    Writing 0 and then -1 is the documented way to reset the age. That sequence has a real hazard:
    between the two writes the account is flagged "must change password at next logon". If the
    second write fails, the account is LEFT in that state -- so every failure of the second write
    is reported individually and listed again at the end, because those are the accounts a person
    has to go fix.

    SECURITY NOTE, stated once and then left to your judgement: this makes an old password valid
    for another full period. It restores service without proving the password is still trustworthy.
    Consider following up with a planned password change for the affected users.

    Report-only by default. It writes nothing to AD unless -Apply is passed.

.PARAMETER SearchBase
    OU to scan. Required unless -FromCsv or -Accounts is used -- there is deliberately no
    domain-wide default, because this writes to accounts.

.PARAMETER FromCsv
    CSV listing the accounts to fix. Uses the column named by -CsvAccountColumn.

.PARAMETER FromExcel
    The .xlsx exported from the service's results screen — the exact accounts the service changed.
    This is the most precise option: it touches those accounts and nothing else.
    Read directly, with no Excel installation and no extra PowerShell module.

.PARAMETER ExcelColumn
    Header of the column holding the account name. Empty = detected automatically
    ("المفتاح", "Key", "sAMAccountName", ...). Summary and notification rows are filtered out.

.PARAMETER ExcelSheet
    Worksheet name. Empty = the first sheet.

.PARAMETER Accounts
    Explicit sAMAccountNames, for a handful of accounts.

.PARAMETER OnlyExpired
    Restrict to accounts whose password is already past the domain's maximum age. On by default:
    accounts that are not expired do not need touching, and every write carries the hazard above.
    Pass -OnlyExpired:$false to reset every account in scope.

.PARAMETER ExcludeDisabled
    Skip disabled accounts. On by default -- a disabled account is not locked out of anything.

.PARAMETER ExclusionGroup
    Members of this group (including nested) are never touched. A group that cannot be resolved
    aborts the run rather than silently protecting nobody.

.PARAMETER Apply
    Perform the writes. Without it the script only reports.

.PARAMETER LogPath
    CSV written with the outcome for every account examined.

.EXAMPLE
    .\Reset-PwdLastSet.ps1 -SearchBase "OU=test1,DC=nu,DC=edu,DC=sa"
    Reports which accounts would be reset. Run this first and read the output.

.EXAMPLE
    .\Reset-PwdLastSet.ps1 -SearchBase "OU=test1,DC=nu,DC=edu,DC=sa" -Apply
    Restarts the password-age clock for the expired accounts in that OU.

.EXAMPLE
    .\Reset-PwdLastSet.ps1 -FromExcel .\ServiceAudit-PwdAudit-20260727-1253.xlsx
    Reports what would be reset, using the service's own Excel export as the account list.

.EXAMPLE
    .\Reset-PwdLastSet.ps1 -FromExcel .\ServiceAudit-PwdAudit-20260727-1253.xlsx -Apply
    Fixes exactly the accounts the service changed.

.EXAMPLE
    .\Reset-PwdLastSet.ps1 -FromCsv .\accounts.csv -CsvAccountColumn "Key" -Apply
    Same, from a CSV.

.NOTES
    Requires the RSAT ActiveDirectory module and permission to write pwdLastSet.
    Does NOT change any password, and does NOT re-add "password never expires".
#>

[CmdletBinding(DefaultParameterSetName = 'Ou')]
param(
    [Parameter(ParameterSetName = 'Ou', Mandatory)]
    [string]$SearchBase,

    [Parameter(ParameterSetName = 'Csv', Mandatory)]
    [string]$FromCsv,

    [Parameter(ParameterSetName = 'Csv')]
    [string]$CsvAccountColumn = 'KeyValue',

    [Parameter(ParameterSetName = 'Excel', Mandatory)]
    [string]$FromExcel,

    # Empty = look for a likely account column by header name, in both languages.
    [Parameter(ParameterSetName = 'Excel')]
    [string]$ExcelColumn,

    [Parameter(ParameterSetName = 'Excel')]
    [string]$ExcelSheet,

    # Force "no header row": every row, including the first, is an account name.
    [Parameter(ParameterSetName = 'Excel')]
    [switch]$NoHeader,

    [Parameter(ParameterSetName = 'List', Mandatory)]
    [string[]]$Accounts,

    [bool]$OnlyExpired = $true,
    [bool]$ExcludeDisabled = $true,
    [string]$ExclusionGroup,
    [switch]$Apply,

    [string]$LogPath = ".\Reset-PwdLastSet-$(Get-Date -Format yyyyMMdd-HHmmss).csv"
)

$ErrorActionPreference = 'Stop'
Import-Module ActiveDirectory

function Write-Section([string]$Title) {
    Write-Host ""
    Write-Host "=== $Title ===" -ForegroundColor Cyan
}

<#
    Reads an .xlsx without Excel and without any extra module.

    An xlsx is a zip of XML parts, so it can be read with what ships in the box. Requiring the
    ImportExcel module or a local Excel installation would make this script unusable on exactly
    the machine it is most likely to be run on — a domain controller or an admin jump box.

    Returns an array of PSCustomObjects keyed by the header row, like Import-Csv does.
#>
function Read-XlsxRows {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$SheetName,
        # Returns the raw grid (array of string arrays) instead of header-keyed objects, so a
        # sheet with no header row can be read without losing its first line.
        [switch]$Raw
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $Path))
    try {
        function Get-Xml($entryName) {
            $e = $zip.Entries | Where-Object { $_.FullName -eq $entryName }
            if (-not $e) { return $null }
            $sr = New-Object System.IO.StreamReader($e.Open())
            try { [xml]$sr.ReadToEnd() } finally { $sr.Dispose() }
        }

        # Shared strings: most cell text lives here, referenced by index.
        $shared = @()
        $ssXml = Get-Xml 'xl/sharedStrings.xml'
        if ($ssXml) {
            foreach ($si in $ssXml.sst.si) {
                # Rich text splits one string across several <t> runs; join them back.
                $shared += (($si.SelectNodes('.//*[local-name()="t"]') | ForEach-Object { $_.InnerText }) -join '')
            }
        }

        # Which sheet? Match by name via workbook.xml, else the first one.
        $target = 'xl/worksheets/sheet1.xml'
        if ($SheetName) {
            $wb = Get-Xml 'xl/workbook.xml'
            $idx = 0; $found = $false
            foreach ($sh in $wb.workbook.sheets.sheet) {
                $idx++
                if ($sh.name -eq $SheetName) { $target = "xl/worksheets/sheet$idx.xml"; $found = $true; break }
            }
            if (-not $found) {
                $names = ($wb.workbook.sheets.sheet | ForEach-Object { $_.name }) -join ', '
                throw "Sheet '$SheetName' not found. Sheets present: $names"
            }
        }

        $sheetXml = Get-Xml $target
        if (-not $sheetXml) { throw "Could not read worksheet part '$target' from '$Path'." }

        # Column reference "BC12" -> zero-based column index.
        function Convert-ColRef([string]$ref) {
            $letters = ($ref -replace '\d', '')
            $n = 0
            foreach ($ch in $letters.ToUpperInvariant().ToCharArray()) {
                $n = $n * 26 + ([int][char]$ch - 64)
            }
            $n - 1
        }

        $matrix = @()
        foreach ($row in $sheetXml.worksheet.sheetData.row) {
            $cells = @{}
            $max = -1
            foreach ($c in $row.c) {
                $ci = Convert-ColRef $c.r
                $val = switch ($c.t) {
                    's'         { if ($c.v -ne $null) { $shared[[int]$c.v] } else { '' } }
                    'inlineStr' { ($c.SelectNodes('.//*[local-name()="t"]') | ForEach-Object { $_.InnerText }) -join '' }
                    default     { if ($c.v -ne $null) { [string]$c.v } else { '' } }
                }
                $cells[$ci] = $val
                if ($ci -gt $max) { $max = $ci }
            }
            $line = @()
            for ($i = 0; $i -le $max; $i++) { $line += $(if ($cells.ContainsKey($i)) { $cells[$i] } else { '' }) }
            $matrix += ,$line
        }

        if ($Raw) { return ,$matrix }

        if ($matrix.Count -lt 2) { return @() }

        # The export writes a title/header row first; treat row 1 as the header.
        $headers = $matrix[0]
        $out = @()
        foreach ($line in $matrix[1..($matrix.Count - 1)]) {
            $o = [ordered]@{}
            for ($i = 0; $i -lt $headers.Count; $i++) {
                $h = "$($headers[$i])".Trim()
                if (-not $h) { $h = "Column$($i+1)" }
                $o[$h] = $(if ($i -lt $line.Count) { $line[$i] } else { '' })
            }
            $out += [pscustomobject]$o
        }
        return $out
    }
    finally { $zip.Dispose() }
}

if (-not $Apply) {
    Write-Host "REPORT ONLY - nothing will be written to AD. Add -Apply to make changes." -ForegroundColor Yellow
}

# ─────────────────────────────────────────────────────────────────────────────
# 1. Domain password policy — needed to decide what "expired" means
# ─────────────────────────────────────────────────────────────────────────────
Write-Section "Domain policy"

$maxPwdAge = (Get-ADDefaultDomainPasswordPolicy).MaxPasswordAge
if ($maxPwdAge -eq $null -or $maxPwdAge.TotalSeconds -le 0) {
    Write-Host "  Maximum password age: not set (passwords do not expire by policy)" -ForegroundColor Yellow
    if ($OnlyExpired) {
        throw "The domain policy has no maximum password age, so nothing can be 'expired'. Re-run with -OnlyExpired:`$false if you still want to reset the clock."
    }
    $cutoff = [datetime]::MaxValue
}
else {
    $cutoff = (Get-Date).Add(-$maxPwdAge)
    Write-Host ("  Maximum password age : {0} days" -f [math]::Round($maxPwdAge.TotalDays))
    Write-Host ("  Expired if set before: {0:yyyy-MM-dd}" -f $cutoff)
}

# Fine-grained password policies override the default for the accounts they apply to. This script
# judges against the default only, so say so rather than let the number look authoritative.
$fgppCount = @(Get-ADFineGrainedPasswordPolicy -Filter * -ErrorAction SilentlyContinue).Count
if ($fgppCount -gt 0) {
    Write-Host "  NOTE: $fgppCount fine-grained password polic(ies) exist. Accounts governed by one" -ForegroundColor Yellow
    Write-Host "        may expire on a different schedule than shown here." -ForegroundColor Yellow
}

# ─────────────────────────────────────────────────────────────────────────────
# 2. Collect the accounts
# ─────────────────────────────────────────────────────────────────────────────
Write-Section "Scope"

$props = @('sAMAccountName', 'DistinguishedName', 'Enabled', 'pwdLastSet', 'displayName', 'userAccountControl')
$targets = @()

switch ($PSCmdlet.ParameterSetName) {
    'Ou' {
        try { $null = Get-ADObject -Identity $SearchBase -ErrorAction Stop }
        catch { throw "SearchBase '$SearchBase' does not exist. Nothing was examined." }

        $targets = Get-ADUser -Filter * -SearchBase $SearchBase -Properties $props -ResultPageSize 500
        Write-Host "  Source: OU $SearchBase"
    }
    'Csv' {
        if (-not (Test-Path $FromCsv)) { throw "CSV not found: $FromCsv" }
        $rows = Import-Csv -Path $FromCsv
        if ($rows.Count -and -not ($rows[0].PSObject.Properties.Name -contains $CsvAccountColumn)) {
            throw "CSV has no column '$CsvAccountColumn'. Columns present: $($rows[0].PSObject.Properties.Name -join ', ')"
        }
        $names = $rows.$CsvAccountColumn | Where-Object { $_ -and $_.Trim() } | ForEach-Object { $_.Trim() } | Select-Object -Unique
        Write-Host "  Source: CSV $FromCsv ($($names.Count) name(s))"
        foreach ($n in $names) {
            try { $targets += Get-ADUser -Identity $n -Properties $props }
            catch { Write-Host "  NOT FOUND: $n" -ForegroundColor Yellow }
        }
    }
    'Excel' {
        if (-not (Test-Path $FromExcel)) { throw "Excel file not found: $FromExcel" }

        $candidates = @('المفتاح', 'Key', 'KeyValue', 'sAMAccountName', 'SamAccountName', 'الحساب', 'Account')
        $rawValues = $null

        if ($NoHeader) {
            $rawValues = @(Read-XlsxRows -Path $FromExcel -SheetName $ExcelSheet -Raw | ForEach-Object { $_[0] })
            Write-Host "  Reading as headerless (-NoHeader): every row is an account name."
        }
        else {
            $rows = Read-XlsxRows -Path $FromExcel -SheetName $ExcelSheet
            $grid = Read-XlsxRows -Path $FromExcel -SheetName $ExcelSheet -Raw
            $firstCell = if ($grid.Count -gt 0 -and $grid[0].Count -gt 0) { "$($grid[0][0])".Trim() } else { '' }

            # A hand-prepared single-column list often has no header, in which case row 1 is a real
            # account. Treating it as a header would drop that account silently — the one failure
            # mode nobody double-checks, because the count still looks plausible.
            $looksHeaderless = ($grid.Count -gt 0) -and
                               (@($grid[0]).Count -eq 1) -and
                               ($candidates -notcontains $firstCell)

            if ($looksHeaderless) {
                $rawValues = @($grid | ForEach-Object { $_[0] })
                Write-Host "  Single column and the first cell ('$firstCell') is not a known header —" -ForegroundColor Yellow
                Write-Host "  reading EVERY row as an account name, including the first." -ForegroundColor Yellow
                Write-Host "  (Pass -ExcelColumn if the first row really is a header.)" -ForegroundColor Yellow
            }
            else {
                if ($rows.Count -eq 0) { throw "'$FromExcel' has no data rows." }
                $available = $rows[0].PSObject.Properties.Name
                $col = $ExcelColumn

                if (-not $col) {
                    $col = $candidates | Where-Object { $available -contains $_ } | Select-Object -First 1
                    if (-not $col) {
                        throw "Could not find an account column automatically. Columns present: $($available -join ', '). Re-run with -ExcelColumn '<name>' or -NoHeader."
                    }
                    Write-Host "  Account column detected: '$col'"
                }
                elseif ($available -notcontains $col) {
                    throw "Column '$col' not found. Columns present: $($available -join ', ')"
                }

                $rawValues = @($rows.$col)
            }
        }

        # A full export also contains run summaries and notification rows; those keys are not
        # account names and would each be reported as "not found" on every run.
        $noise = @('(report)', '(summary)')
        $names = $rawValues |
            Where-Object { $_ -and "$_".Trim() } |
            ForEach-Object { "$_".Trim() } |
            Where-Object { $_ -notmatch '^run #\d+$' -and $noise -notcontains $_ -and $candidates -notcontains $_ } |
            Select-Object -Unique

        if ($names.Count -eq 0) { throw "No account names found in '$FromExcel'." }

        Write-Host "  Source: Excel $FromExcel — $($names.Count) unique account name(s)"
        foreach ($n in $names) {
            try { $targets += Get-ADUser -Identity $n -Properties $props }
            catch { Write-Host "  NOT FOUND: $n" -ForegroundColor Yellow }
        }
    }
    'List' {
        Write-Host "  Source: explicit list ($($Accounts.Count) name(s))"
        foreach ($n in $Accounts) {
            try { $targets += Get-ADUser -Identity $n -Properties $props }
            catch { Write-Host "  NOT FOUND: $n" -ForegroundColor Yellow }
        }
    }
}

$targets = @($targets)
if ($targets.Count -eq 0) { throw "No accounts found. Nothing to do." }
Write-Host "  Accounts found: $($targets.Count)"

# ─────────────────────────────────────────────────────────────────────────────
# 3. Exclusion group (fail closed)
# ─────────────────────────────────────────────────────────────────────────────
$exempt = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
if ($ExclusionGroup) {
    try {
        $grp = if ($ExclusionGroup -match '=') { Get-ADGroup -Identity $ExclusionGroup }
               else { Get-ADGroup -Filter "sAMAccountName -eq '$ExclusionGroup'" }
        if (-not $grp) { throw "not found" }
        Get-ADUser -LDAPFilter "(memberOf:1.2.840.113556.1.4.1941:=$($grp.DistinguishedName))" |
            ForEach-Object { $null = $exempt.Add($_.DistinguishedName) }
        Write-Host "  Exclusion group '$ExclusionGroup': $($exempt.Count) member(s) protected"
    }
    catch {
        throw "Could not resolve exclusion group '$ExclusionGroup' — aborting for safety: $($_.Exception.Message)"
    }
}

# ─────────────────────────────────────────────────────────────────────────────
# 4. Decide and act
# ─────────────────────────────────────────────────────────────────────────────
Write-Section $(if ($Apply) { "Resetting" } else { "Plan" })

$results = New-Object System.Collections.Generic.List[object]
$stuck   = New-Object System.Collections.Generic.List[string]
$reset = 0; $skipped = 0; $failed = 0

foreach ($u in $targets) {
    # pwdLastSet arrives as a FILETIME integer; 0 means "must change at next logon".
    $raw = [int64]($u.pwdLastSet)
    $lastSet = if ($raw -gt 0) { [datetime]::FromFileTime($raw) } else { $null }

    $reason = $null
    if ($ExcludeDisabled -and -not $u.Enabled)              { $reason = 'disabled' }
    elseif ($exempt.Contains($u.DistinguishedName))          { $reason = 'in exclusion group' }
    elseif ($raw -eq 0)                                      { $reason = 'already flagged must-change (pwdLastSet=0)' }
    elseif ($OnlyExpired -and $lastSet -and $lastSet -ge $cutoff) {
        $reason = "not expired (set $($lastSet.ToString('yyyy-MM-dd')))"
    }

    if ($reason) {
        $skipped++
        $results.Add([pscustomobject]@{
            Timestamp = (Get-Date).ToString('s'); SamAccountName = $u.sAMAccountName
            DistinguishedName = $u.DistinguishedName; Action = 'Skipped'
            PwdLastSetBefore = $(if ($lastSet) { $lastSet.ToString('yyyy-MM-dd HH:mm') } else { '(0)' })
            PwdLastSetAfter = ''; Detail = $reason })
        continue
    }

    $before = $(if ($lastSet) { $lastSet.ToString('yyyy-MM-dd HH:mm') } else { '(0)' })

    if (-not $Apply) {
        $results.Add([pscustomobject]@{
            Timestamp = (Get-Date).ToString('s'); SamAccountName = $u.sAMAccountName
            DistinguishedName = $u.DistinguishedName; Action = 'WouldReset'
            PwdLastSetBefore = $before; PwdLastSetAfter = '(now)'; Detail = '' })
        $reset++
        continue
    }

    # Step 1 of 2 — this is the window in which the account reads as "must change password".
    try {
        Set-ADUser -Identity $u.DistinguishedName -Replace @{pwdLastSet = 0} -ErrorAction Stop
    }
    catch {
        $failed++
        Write-Host ("  FAILED (step 1, account untouched) {0}: {1}" -f $u.sAMAccountName, $_.Exception.Message) -ForegroundColor Red
        $results.Add([pscustomobject]@{
            Timestamp = (Get-Date).ToString('s'); SamAccountName = $u.sAMAccountName
            DistinguishedName = $u.DistinguishedName; Action = 'Failed'
            PwdLastSetBefore = $before; PwdLastSetAfter = ''; Detail = "step 1: $($_.Exception.Message)" })
        continue
    }

    # Step 2 of 2 — sets pwdLastSet to now. A failure here leaves the account flagged
    # "must change password at next logon", which is worse than where it started.
    try {
        Set-ADUser -Identity $u.DistinguishedName -Replace @{pwdLastSet = -1} -ErrorAction Stop
    }
    catch {
        $failed++
        $stuck.Add($u.sAMAccountName)
        Write-Host ("  FAILED (step 2) {0}: LEFT AS 'must change password at next logon' — {1}" -f $u.sAMAccountName, $_.Exception.Message) -ForegroundColor Red
        $results.Add([pscustomobject]@{
            Timestamp = (Get-Date).ToString('s'); SamAccountName = $u.sAMAccountName
            DistinguishedName = $u.DistinguishedName; Action = 'StuckMustChange'
            PwdLastSetBefore = $before; PwdLastSetAfter = '(0)'
            Detail = "step 2 failed: $($_.Exception.Message)" })
        continue
    }

    # Read back rather than trust the write: the whole point is that the clock actually moved.
    $after = (Get-ADUser -Identity $u.DistinguishedName -Properties pwdLastSet).pwdLastSet
    $afterDate = if ([int64]$after -gt 0) { [datetime]::FromFileTime([int64]$after) } else { $null }

    if (-not $afterDate) {
        $failed++
        $stuck.Add($u.sAMAccountName)
        Write-Host ("  VERIFY FAILED {0}: pwdLastSet is still 0" -f $u.sAMAccountName) -ForegroundColor Red
        $results.Add([pscustomobject]@{
            Timestamp = (Get-Date).ToString('s'); SamAccountName = $u.sAMAccountName
            DistinguishedName = $u.DistinguishedName; Action = 'StuckMustChange'
            PwdLastSetBefore = $before; PwdLastSetAfter = '(0)'; Detail = 'verification read showed 0' })
        continue
    }

    $reset++
    $results.Add([pscustomobject]@{
        Timestamp = (Get-Date).ToString('s'); SamAccountName = $u.sAMAccountName
        DistinguishedName = $u.DistinguishedName; Action = 'Reset'
        PwdLastSetBefore = $before; PwdLastSetAfter = $afterDate.ToString('yyyy-MM-dd HH:mm'); Detail = '' })
}

# ─────────────────────────────────────────────────────────────────────────────
# 5. Result
# ─────────────────────────────────────────────────────────────────────────────
$results | Export-Csv -Path $LogPath -NoTypeInformation -Encoding UTF8

Write-Section "Result"
Write-Host ("  {0} : {1}" -f $(if ($Apply) { "reset " } else { "would reset" }), $reset)
Write-Host ("  skipped     : {0}" -f $skipped)
Write-Host ("  failed      : {0}" -f $failed) -ForegroundColor $(if ($failed) { 'Red' } else { 'Gray' })
Write-Host "  log         : $LogPath"

if ($Apply -and $maxPwdAge -and $maxPwdAge.TotalSeconds -gt 0 -and $reset -gt 0) {
    Write-Host ("  Passwords reset now expire on or about {0:yyyy-MM-dd}." -f (Get-Date).Add($maxPwdAge)) -ForegroundColor Green
}

if ($stuck.Count -gt 0) {
    Write-Host ""
    Write-Host "ACTION REQUIRED — these accounts are flagged 'must change password at next logon'" -ForegroundColor Red
    Write-Host "because the second write did not complete. Re-run the script for them, or clear the" -ForegroundColor Red
    Write-Host "flag manually:" -ForegroundColor Red
    $stuck | ForEach-Object { Write-Host "   * $_" -ForegroundColor Red }
}
elseif (-not $Apply) {
    Write-Host ""
    Write-Host "Plan looks clean. Re-run with -Apply to perform the reset." -ForegroundColor Yellow
}
