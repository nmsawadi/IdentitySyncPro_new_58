# ═══════════════════════════════════════════════════════════
# IdentitySyncPro — Production Deployment Script v2.0
# ═══════════════════════════════════════════════════════════
# Usage:
#   .\scripts\deploy.ps1 [-OutputDir "C:\inetpub\IdentitySyncPro"] [-Configuration Release]
#
# What it does:
#   1. Validates prerequisites (.NET SDK, project files, config)
#   2. Restores NuGet packages
#   3. Builds in Release mode
#   4. Runs tests (if available)
#   5. Publishes to output directory
#   6. Validates output (DLLs, appsettings, web.config)
#   7. Creates Logs directory & sets permissions
#   8. Creates first-run marker file
#   9. Shows deployment summary & next steps
# ═══════════════════════════════════════════════════════════

param(
    [string]$OutputDir = "C:\inetpub\IdentitySyncPro",
    [string]$Configuration = "Release",
    [string]$ProjectRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [switch]$SkipTests,
    [switch]$SkipBackup,
    [switch]$CreateWindowsService
)

$ErrorActionPreference = "Stop"

# ═══════════════════════════════════════════
# Helper Functions
# ═══════════════════════════════════════════
function Write-Step($msg)    { Write-Host "`n▶ $msg" -ForegroundColor Cyan }
function Write-Success($msg) { Write-Host "  ✅ $msg" -ForegroundColor Green }
function Write-Warn($msg)    { Write-Host "  ⚠️  $msg" -ForegroundColor Yellow }
function Write-Fail($msg)    { Write-Host "  ❌ $msg" -ForegroundColor Red }
function Write-Info($msg)    { Write-Host "  ℹ️  $msg" -ForegroundColor DarkGray }

Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host "  IdentitySyncPro — Production Deployment Script v2.0    " -ForegroundColor White
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host ""
Write-Host "  📁 Project:  $ProjectRoot"
Write-Host "  📦 Output:   $OutputDir"
Write-Host "  ⚙️  Config:   $Configuration"
Write-Host "  🧪 Tests:    $(if ($SkipTests) { 'Skipped' } else { 'Enabled' })"
Write-Host "  🔄 Backup:   $(if ($SkipBackup) { 'Skipped' } else { 'Enabled' })"
Write-Host ""

# ═══════════════════════════════════════════
# Step 1: Validate Prerequisites
# ═══════════════════════════════════════════
Write-Step "Checking prerequisites..."

# Check .NET SDK
$dotnetVersion = dotnet --version 2>$null
if (-not $dotnetVersion) {
    Write-Fail ".NET SDK not found. Install from https://dotnet.microsoft.com"
    exit 1
}
Write-Success ".NET SDK: $dotnetVersion"

# Check minimum .NET version (8.0+)
$majorVersion = [int]($dotnetVersion.Split('.')[0])
if ($majorVersion -lt 8) {
    Write-Fail ".NET SDK 8.0+ required (found: $dotnetVersion)"
    exit 1
}
Write-Success ".NET 8+ requirement met"

# Check project exists
$webProject = Join-Path $ProjectRoot "src\IdentitySyncPro.Web\IdentitySyncPro.Web.csproj"
if (-not (Test-Path $webProject)) {
    Write-Fail "Web project not found: $webProject"
    exit 1
}
Write-Success "Web project found"

# Check if SQL Server is accessible (optional)
try {
    $sqlcmd = Get-Command "sqlcmd" -ErrorAction SilentlyContinue
    if ($sqlcmd) {
        Write-Success "SQL Server tools available (sqlcmd found)"
    } else {
        Write-Info "sqlcmd not in PATH — SQL validation skipped"
    }
} catch {
    Write-Info "SQL Server tools check skipped"
}

# Check production settings
$prodSettings = Join-Path $ProjectRoot "src\IdentitySyncPro.Web\appsettings.Production.json"
$appSettings = Join-Path $ProjectRoot "src\IdentitySyncPro.Web\appsettings.json"

if (Test-Path $prodSettings) {
    $content = Get-Content $prodSettings -Raw
    $placeholders = @("YOUR_SQL_SERVER", "YOUR_ORACLE_HOST", "YOUR_AD_SERVER", "YOUR_ORACLE_USER", "YOUR_ORACLE_PASSWORD", "YOUR_AD_SERVICE_ACCOUNT", "YOUR_AD_SERVICE_PASSWORD", "CHANGE_THIS_DEFAULT_PASSWORD", "GENERATE-A-STRONG-API-KEY")
    $foundPlaceholders = @()
    foreach ($p in $placeholders) {
        if ($content -match [regex]::Escape($p)) {
            $foundPlaceholders += $p
        }
    }
    if ($foundPlaceholders.Count -gt 0) {
        Write-Warn "appsettings.Production.json contains $($foundPlaceholders.Count) placeholder values:"
        foreach ($fp in $foundPlaceholders) {
            Write-Warn "  - $fp"
        }
        Write-Warn ""
        Write-Warn "⚠️  You MUST update these before first run!"
        Write-Warn "   Edit: $prodSettings"
    } else {
        Write-Success "Production settings found and configured"
    }
} elseif (Test-Path $appSettings) {
    Write-Warn "appsettings.Production.json not found"
    Write-Warn "Using default appsettings.json — update before running in production!"
} else {
    Write-Fail "No appsettings file found! Create appsettings.json or appsettings.Production.json"
    exit 1
}

# ═══════════════════════════════════════════
# Step 2: Restore & Build
# ═══════════════════════════════════════════
Write-Step "Restoring NuGet packages..."
dotnet restore $webProject --verbosity minimal
if ($LASTEXITCODE -ne 0) { Write-Fail "Restore failed"; exit 1 }
Write-Success "Packages restored"

Write-Step "Building project ($Configuration)..."
dotnet build $webProject -c $Configuration --no-restore --verbosity minimal
if ($LASTEXITCODE -ne 0) { Write-Fail "Build failed"; exit 1 }
Write-Success "Build succeeded"

# ═══════════════════════════════════════════
# Step 3: Run Tests (if available)
# ═══════════════════════════════════════════
if (-not $SkipTests) {
    $testProject = Join-Path $ProjectRoot "src\IdentitySyncPro.Tests\IdentitySyncPro.Tests.csproj"
    if (Test-Path $testProject) {
        Write-Step "Running tests..."
        dotnet test $testProject -c $Configuration --no-build --verbosity minimal
        if ($LASTEXITCODE -ne 0) {
            Write-Warn "Some tests failed — review before deploying to production"
        } else {
            Write-Success "All tests passed"
        }
    } else {
        Write-Info "No test project found — skipping tests"
    }
} else {
    Write-Info "Tests skipped (--SkipTests flag)"
}

# ═══════════════════════════════════════════
# Step 4: Backup Existing Deployment
# ═══════════════════════════════════════════
if (-not $SkipBackup) {
    if ((Get-ChildItem $OutputDir -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0) {
        $backupDir = "${OutputDir}_backup_$(Get-Date -Format 'yyyyMMdd_HHmmss')"
        Write-Step "Backing up existing deployment..."
        Write-Warn "Existing deployment found — backing up to $backupDir"
        Copy-Item $OutputDir $backupDir -Recurse -Force
        Write-Success "Backup created: $backupDir"
    }
}

# ═══════════════════════════════════════════
# Step 5: Publish
# ═══════════════════════════════════════════
Write-Step "Publishing to $OutputDir..."

# Create output directory if needed
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    Write-Success "Created output directory"
}

dotnet publish $webProject -c $Configuration -o $OutputDir --no-build
if ($LASTEXITCODE -ne 0) { Write-Fail "Publish failed"; exit 1 }
Write-Success "Published successfully"

# ═══════════════════════════════════════════
# Step 6: Validate Output
# ═══════════════════════════════════════════
Write-Step "Validating deployment..."

$requiredFiles = @(
    "IdentitySyncPro.Web.dll",
    "IdentitySyncPro.Core.dll",
    "IdentitySyncPro.Infrastructure.dll",
    "appsettings.json"
)

$allFound = $true
foreach ($file in $requiredFiles) {
    $filePath = Join-Path $OutputDir $file
    if (Test-Path $filePath) {
        $size = (Get-Item $filePath).Length
        Write-Success "$file ($([math]::Round($size/1KB, 1)) KB)"
    } else {
        Write-Fail "$file MISSING!"
        $allFound = $false
    }
}

# Check for web.config (IIS)
$webConfig = Join-Path $OutputDir "web.config"
if (Test-Path $webConfig) {
    Write-Success "web.config found (IIS ready)"
} else {
    Write-Info "web.config not found — Kestrel-only deployment"
}

# ═══════════════════════════════════════════
# Step 7: Create Required Directories
# ═══════════════════════════════════════════
Write-Step "Setting up directories..."

$logsDir = Join-Path $OutputDir "Logs"
if (-not (Test-Path $logsDir)) {
    New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
    Write-Success "Created Logs directory"
} else {
    Write-Success "Logs directory exists"
}

$wwwrootDir = Join-Path $OutputDir "wwwroot"
if (Test-Path $wwwrootDir) {
    Write-Success "wwwroot directory verified"
}

# ═══════════════════════════════════════════
# Step 8: Create First-Run Marker (optional)
# ═══════════════════════════════════════════
$markerFile = Join-Path $OutputDir ".first-run-complete"
if (-not (Test-Path $markerFile)) {
    Write-Info "First deployment detected — app will auto-create database on first run"
}

# ═══════════════════════════════════════════
# Step 9: Create Windows Service (optional)
# ═══════════════════════════════════════════
if ($CreateWindowsService) {
    Write-Step "Creating Windows Service..."
    
    $serviceName = "IdentitySyncPro"
    $serviceDisplayName = "IdentitySyncPro - Identity Management Platform"
    $exePath = Join-Path $OutputDir "IdentitySyncPro.Web.exe"
    
    $existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($existingService) {
        Write-Warn "Service '$serviceName' already exists — stopping..."
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        sc.exe delete $serviceName | Out-Null
        Start-Sleep -Seconds 2
    }
    
    sc.exe create $serviceName binPath= "`"$exePath`"" start= auto DisplayName= "`"$serviceDisplayName`""
    sc.exe description $serviceName "IdentitySyncPro IAM Platform - Syncs Oracle student data to Active Directory"
    sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/120000/restart/300000
    
    Write-Success "Windows Service created: $serviceName"
    Write-Info "Start with: Start-Service $serviceName"
    Write-Info "Failure recovery: restart after 1min/2min/5min"
}

# ═══════════════════════════════════════════
# Step 10: Summary
# ═══════════════════════════════════════════
Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Magenta
if ($allFound) {
    Write-Host "  ✅ Deployment completed successfully!" -ForegroundColor Green
} else {
    Write-Host "  ⚠️  Deployment completed with warnings" -ForegroundColor Yellow
}
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host ""
Write-Host "  📁 Deployment path: $OutputDir" -ForegroundColor White
Write-Host ""
Write-Host "  ┌──────────────────────────────────────────────┐" -ForegroundColor DarkCyan
Write-Host "  │           FIRST RUN CHECKLIST                │" -ForegroundColor DarkCyan
Write-Host "  └──────────────────────────────────────────────┘" -ForegroundColor DarkCyan
Write-Host ""
Write-Host "  1️⃣  Update appsettings.Production.json:" -ForegroundColor Cyan
Write-Host "     • SQL Server connection string" -ForegroundColor White
Write-Host "     • Oracle connection (Host, Port, ServiceName, UserId, Password)" -ForegroundColor White
Write-Host "     • AD connection (Server, Port, BaseDN, Username, Password)" -ForegroundColor White
Write-Host "     • Default password for new accounts" -ForegroundColor White
Write-Host "     • API security keys" -ForegroundColor White
Write-Host ""
Write-Host "  2️⃣  Set environment variable:" -ForegroundColor Cyan
Write-Host "     `$env:ASPNETCORE_ENVIRONMENT=`"Production`"" -ForegroundColor Yellow
Write-Host ""
Write-Host "  3️⃣  Run the application:" -ForegroundColor Cyan
Write-Host "     cd $OutputDir" -ForegroundColor Yellow
Write-Host "     dotnet IdentitySyncPro.Web.dll" -ForegroundColor Yellow
Write-Host ""
Write-Host "     Or as Windows Service:" -ForegroundColor Cyan
Write-Host "     .\scripts\deploy.ps1 -CreateWindowsService" -ForegroundColor Yellow
Write-Host ""
Write-Host "  4️⃣  Open browser:" -ForegroundColor Cyan
Write-Host "     https://localhost:5001" -ForegroundColor Yellow
Write-Host ""
Write-Host "  5️⃣  First-time setup wizard:" -ForegroundColor Cyan
Write-Host "     a. Database auto-created (SQL Server + Hangfire + Services + SmsProviders)" -ForegroundColor White
Write-Host "     b. Go to /Settings → Create Tenant (Oracle + AD + SMS)" -ForegroundColor White
Write-Host "     c. Go to /Settings/Mapping/{tenantId} → Load Default Mapping (34 fields)" -ForegroundColor White
Write-Host "     d. Go to /Connector → Test Oracle & AD connections" -ForegroundColor White
Write-Host "     e. Go to /SmsCenter → Add SMS Provider" -ForegroundColor White
Write-Host "     f. Go to /Sync → Run Dry Run first" -ForegroundColor White
Write-Host "     g. Review results, then run Full Sync" -ForegroundColor White
Write-Host ""
Write-Host "  📖 Full documentation: docs\USER_GUIDE.md" -ForegroundColor DarkGray
Write-Host ""
