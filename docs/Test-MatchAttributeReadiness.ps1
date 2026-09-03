<#
.SYNOPSIS
    Checks that a tenant's AD match attribute is ready before the first sync, and optionally
    stamps it onto existing accounts.

.DESCRIPTION
    IdentitySyncPro finds an existing account by an immutable value (ADMatchAttribute, e.g.
    extensionAttribute2 = employee number) rather than by account name, so a person whose name
    changes is still matched to their own account.

    That only works if the value is already ON the existing accounts. An account missing it is
    invisible to the match, so the sync treats the person as new and CREATES A SECOND ACCOUNT --
    and repeats that every run. Stamping the existing population is therefore not optional
    housekeeping; it is the precondition for the first sync.

    Four things are verified, each of which silently breaks matching if wrong:

      1. Schema     -- the attribute exists, is single-valued, and is writable.
      2. Index      -- searchFlags has the index bit. Without it every match is a full scan of
                       the directory, per identity. Correct but slow enough to matter at scale.
      3. Coverage   -- how many in-scope accounts carry a value, and how many do not.
      4. Duplicates -- two accounts sharing one value. The engine deliberately REFUSES an
                       ambiguous match and skips the record, so these people never sync at all
                       until the duplicate is resolved.

    Report-only by default: it writes nothing to AD unless -Apply is passed.

.PARAMETER MatchAttribute
    The AD attribute holding the key. Must equal the tenant's ADMatchAttribute setting.

.PARAMETER SearchBase
    OU to examine. Required -- deliberately no domain-wide default, since -Apply writes.

.PARAMETER FromAttribute
    Stamp MatchAttribute by copying an attribute the accounts already carry (e.g. employeeID).
    Use when the key is already somewhere in AD.

.PARAMETER FromCsv
    Stamp from a CSV exported from the source system. Needs the columns named by
    -CsvAccountColumn and -CsvKeyColumn.

.PARAMETER Overwrite
    Replace values that are already present. Off by default: an existing value may have been set
    by another system, and overwriting it silently re-points that account's identity.

.PARAMETER Apply
    Perform the writes. Without it the script only reports.

.PARAMETER LogPath
    CSV written with the outcome for every account examined.

.EXAMPLE
    .\Test-MatchAttributeReadiness.ps1 -MatchAttribute extensionAttribute2 -SearchBase "OU=Employees,DC=corp,DC=local"
    Audits only. Run this first and read the output before anything else.

.EXAMPLE
    .\Test-MatchAttributeReadiness.ps1 -MatchAttribute extensionAttribute2 -SearchBase "OU=Employees,DC=corp,DC=local" -FromAttribute employeeID
    Shows which accounts WOULD be stamped from employeeID. Still writes nothing.

.EXAMPLE
    .\Test-MatchAttributeReadiness.ps1 -MatchAttribute extensionAttribute2 -SearchBase "OU=Employees,DC=corp,DC=local" -FromCsv .\employees.csv -Apply
    Stamps the value from the CSV.

.NOTES
    Requires the RSAT ActiveDirectory module and write access to the attribute when using -Apply.
    Indexing an attribute is a forest-wide schema change and is NOT performed by this script --
    it only reports whether the index is present.
#>

[CmdletBinding(DefaultParameterSetName = 'Audit')]
param(
    [Parameter(Mandatory)]
    [string]$MatchAttribute,

    [Parameter(Mandatory)]
    [string]$SearchBase,

    [Parameter(ParameterSetName = 'FromAttribute', Mandatory)]
    [string]$FromAttribute,

    [Parameter(ParameterSetName = 'FromCsv', Mandatory)]
    [string]$FromCsv,

    [Parameter(ParameterSetName = 'FromCsv')]
    [string]$CsvAccountColumn = 'SamAccountName',

    [Parameter(ParameterSetName = 'FromCsv')]
    [string]$CsvKeyColumn = 'EmployeeNumber',

    [switch]$Overwrite,
    [switch]$Apply,

    [string]$LogPath = ".\MatchAttributeReadiness-$(Get-Date -Format yyyyMMdd-HHmmss).csv"
)

$ErrorActionPreference = 'Stop'
Import-Module ActiveDirectory

function Write-Section([string]$Title) {
    Write-Host ""
    Write-Host "=== $Title ===" -ForegroundColor Cyan
}

# A run that writes nothing must never look like a run that did.
if (-not $Apply) {
    Write-Host "REPORT ONLY - nothing will be written to AD. Add -Apply to make changes." -ForegroundColor Yellow
}

# ─────────────────────────────────────────────────────────────────────────────
# 1. Scope
# ─────────────────────────────────────────────────────────────────────────────
# A typo'd OU returns zero accounts, which reads exactly like "everything is already
# stamped". Fail on it instead.
try {
    $null = Get-ADObject -Identity $SearchBase -ErrorAction Stop
}
catch {
    throw "SearchBase '$SearchBase' does not exist. Nothing was examined."
}

# ─────────────────────────────────────────────────────────────────────────────
# 2. Schema
# ─────────────────────────────────────────────────────────────────────────────
Write-Section "Schema: $MatchAttribute"

$schemaNC = (Get-ADRootDSE).schemaNamingContext
$attrDef = Get-ADObject -SearchBase $schemaNC `
    -LDAPFilter "(lDAPDisplayName=$MatchAttribute)" `
    -Properties lDAPDisplayName, searchFlags, isSingleValued, systemOnly, attributeSyntax |
    Select-Object -First 1

if (-not $attrDef) {
    throw "Attribute '$MatchAttribute' does not exist in the schema. Check the spelling against the tenant's ADMatchAttribute setting."
}

$isIndexed = ($attrDef.searchFlags -band 1) -eq 1
$schemaOk = $true

Write-Host ("  exists         : yes")
Write-Host ("  single-valued  : {0}" -f $(if ($attrDef.isSingleValued) { 'yes' } else { 'NO' }))
Write-Host ("  writable       : {0}" -f $(if ($attrDef.systemOnly) { 'NO (systemOnly)' } else { 'yes' }))
Write-Host ("  indexed        : {0}" -f $(if ($isIndexed) { 'yes' } else { 'NO' }))

if (-not $attrDef.isSingleValued) {
    Write-Host "  -> Multi-valued attributes are not supported for matching." -ForegroundColor Red
    $schemaOk = $false
}
if ($attrDef.systemOnly) {
    Write-Host "  -> This attribute cannot be written. Choose another." -ForegroundColor Red
    $schemaOk = $false
}
if (-not $isIndexed) {
    # Not fatal: matching is correct either way. It is a throughput problem, and one that
    # looks like "the sync is slow" rather than like a configuration choice.
    Write-Host "  -> Not indexed. Each match becomes a full directory scan; on a large" -ForegroundColor Yellow
    Write-Host "     population this dominates sync time. Ask your schema admin to set" -ForegroundColor Yellow
    Write-Host "     searchFlags bit 1 on this attribute (forest-wide, online)." -ForegroundColor Yellow
}

if (-not $schemaOk) {
    throw "Schema check failed for '$MatchAttribute'. No accounts were examined."
}

# ─────────────────────────────────────────────────────────────────────────────
# 3. Population
# ─────────────────────────────────────────────────────────────────────────────
Write-Section "Accounts under $SearchBase"

$props = @('sAMAccountName', 'DistinguishedName', 'Enabled', $MatchAttribute)
if ($PSCmdlet.ParameterSetName -eq 'FromAttribute') { $props += $FromAttribute }

$accounts = Get-ADUser -Filter * -SearchBase $SearchBase -Properties $props -ResultPageSize 500

$total = @($accounts).Count
if ($total -eq 0) {
    throw "No user accounts found under '$SearchBase'. Verify the OU before treating this as 'nothing to do'."
}

$withValue = @($accounts | Where-Object { $_.$MatchAttribute })
$without = @($accounts | Where-Object { -not $_.$MatchAttribute })

Write-Host ("  total accounts     : {0}" -f $total)
Write-Host ("  carry a value      : {0}" -f $withValue.Count)
Write-Host ("  MISSING a value    : {0}" -f $without.Count) -ForegroundColor $(if ($without.Count) { 'Yellow' } else { 'Green' })

if ($without.Count -gt 0) {
    Write-Host "  -> Each of these would be treated as a NEW identity on the next sync," -ForegroundColor Yellow
    Write-Host "     creating a duplicate account, unless stamped first." -ForegroundColor Yellow
}

# ─────────────────────────────────────────────────────────────────────────────
# 4. Duplicates
# ─────────────────────────────────────────────────────────────────────────────
Write-Section "Duplicate values"

$dupes = $withValue | Group-Object -Property $MatchAttribute | Where-Object { $_.Count -gt 1 }

if ($dupes) {
    Write-Host ("  {0} value(s) appear on more than one account:" -f @($dupes).Count) -ForegroundColor Red
    foreach ($d in $dupes) {
        Write-Host ("   * '{0}' -> {1}" -f $d.Name, ($d.Group.sAMAccountName -join ', ')) -ForegroundColor Red
    }
    Write-Host "  -> The engine refuses an ambiguous match and SKIPS these records, so these" -ForegroundColor Red
    Write-Host "     people will not sync at all until one account is corrected." -ForegroundColor Red
}
else {
    Write-Host "  none" -ForegroundColor Green
}

# ─────────────────────────────────────────────────────────────────────────────
# 5. Stamping
# ─────────────────────────────────────────────────────────────────────────────
$results = New-Object System.Collections.Generic.List[object]

function Add-Result($Sam, $Dn, $Action, $Value, $Detail) {
    $results.Add([pscustomobject]@{
            Timestamp      = (Get-Date).ToString('s')
            SamAccountName = $Sam
            DistinguishedName = $Dn
            Action         = $Action
            Value          = $Value
            Detail         = $Detail
        })
}

if ($PSCmdlet.ParameterSetName -eq 'Audit') {
    foreach ($a in $accounts) {
        Add-Result $a.sAMAccountName $a.DistinguishedName `
            $(if ($a.$MatchAttribute) { 'HasValue' } else { 'Missing' }) $a.$MatchAttribute ''
    }
}
else {
    Write-Section "Stamping plan"

    # Build sam -> key
    $desired = @{}

    if ($PSCmdlet.ParameterSetName -eq 'FromCsv') {
        if (-not (Test-Path $FromCsv)) { throw "CSV not found: $FromCsv" }
        $rows = Import-Csv -Path $FromCsv

        foreach ($col in @($CsvAccountColumn, $CsvKeyColumn)) {
            if ($rows.Count -and -not ($rows[0].PSObject.Properties.Name -contains $col)) {
                throw "CSV has no column '$col'. Columns present: $($rows[0].PSObject.Properties.Name -join ', ')"
            }
        }

        foreach ($r in $rows) {
            $sam = $r.$CsvAccountColumn
            $key = $r.$CsvKeyColumn
            if ([string]::IsNullOrWhiteSpace($sam) -or [string]::IsNullOrWhiteSpace($key)) { continue }
            $desired[$sam.Trim()] = $key.Trim()
        }
    }
    else {
        foreach ($a in $accounts) {
            $v = $a.$FromAttribute
            if (-not [string]::IsNullOrWhiteSpace($v)) { $desired[$a.sAMAccountName] = "$v".Trim() }
        }
    }

    if ($desired.Count -eq 0) { throw "No source values found. Nothing to stamp." }

    # Refuse to CREATE a duplicate. Stamping the same key onto two accounts produces exactly
    # the ambiguity the engine then refuses to resolve -- better caught here, before writing.
    $planned = $desired.Values | Group-Object | Where-Object { $_.Count -gt 1 }
    if ($planned) {
        foreach ($p in $planned) {
            $who = ($desired.GetEnumerator() | Where-Object { $_.Value -eq $p.Name } | ForEach-Object { $_.Key }) -join ', '
            Write-Host ("  '{0}' would be stamped on: {1}" -f $p.Name, $who) -ForegroundColor Red
        }
        throw "The source assigns one key to multiple accounts. Resolve this before stamping."
    }

    $byName = @{}
    foreach ($a in $accounts) { $byName[$a.sAMAccountName] = $a }

    $toStamp = 0; $skipHas = 0; $notFound = 0

    foreach ($sam in $desired.Keys) {
        $key = $desired[$sam]

        if (-not $byName.ContainsKey($sam)) {
            $notFound++
            Add-Result $sam '' 'NotFoundInScope' $key 'No such account under SearchBase'
            continue
        }

        $acct = $byName[$sam]
        $current = $acct.$MatchAttribute

        if ($current -and -not $Overwrite) {
            if ("$current".Trim() -ne $key) {
                # A different existing value is a genuine conflict, not a no-op.
                Add-Result $sam $acct.DistinguishedName 'ConflictKept' $current "Source says '$key'; use -Overwrite to replace"
                Write-Host ("  CONFLICT {0}: has '{1}', source says '{2}'" -f $sam, $current, $key) -ForegroundColor Yellow
            }
            else {
                Add-Result $sam $acct.DistinguishedName 'AlreadyCorrect' $current ''
            }
            $skipHas++
            continue
        }

        $toStamp++

        if ($Apply) {
            try {
                Set-ADUser -Identity $acct.DistinguishedName -Replace @{ $MatchAttribute = $key } -ErrorAction Stop
                Add-Result $sam $acct.DistinguishedName 'Stamped' $key ''
            }
            catch {
                Add-Result $sam $acct.DistinguishedName 'Failed' $key $_.Exception.Message
                Write-Host ("  FAILED {0}: {1}" -f $sam, $_.Exception.Message) -ForegroundColor Red
            }
        }
        else {
            Add-Result $sam $acct.DistinguishedName 'WouldStamp' $key ''
        }
    }

    Write-Host ("  to stamp           : {0}" -f $toStamp)
    Write-Host ("  already had value  : {0}" -f $skipHas)
    Write-Host ("  not found in scope : {0}" -f $notFound) -ForegroundColor $(if ($notFound) { 'Yellow' } else { 'Gray' })
}

# ─────────────────────────────────────────────────────────────────────────────
# 6. Verdict
# ─────────────────────────────────────────────────────────────────────────────
$results | Export-Csv -Path $LogPath -NoTypeInformation -Encoding UTF8
Write-Section "Result"
Write-Host "  log: $LogPath"

$blocking = @()
if ($without.Count -gt 0 -and -not $Apply) { $blocking += "$($without.Count) account(s) missing the value" }
if ($dupes) { $blocking += "$(@($dupes).Count) duplicate value(s)" }

if ($blocking.Count -gt 0) {
    Write-Host ""
    Write-Host ("NOT READY: {0}." -f ($blocking -join '; ')) -ForegroundColor Red
    Write-Host "Running a sync now would create duplicate accounts for the unmatched identities." -ForegroundColor Red
}
elseif (-not $Apply -and $PSCmdlet.ParameterSetName -ne 'Audit') {
    Write-Host ""
    Write-Host "Plan looks clean. Re-run with -Apply to write the values." -ForegroundColor Yellow
}
else {
    Write-Host ""
    Write-Host "READY: every account in scope carries a unique value." -ForegroundColor Green
}
