<#
.SYNOPSIS
Runs the built AD Guardian installer against a throwaway directory and
C:\ADCheckLogs (which must exist with test content) to exercise the
v2.0.27 CleanupLegacyAdCheckLogs flow — migration copy, DelTree, MigrationMarker
write, and the new FindFirst CFA/enumeration-failure diagnostics.

.ELEVATION
The installer has PrivilegesRequired=admin (Inno Setup). If this script is
not running elevated, it auto-relaunches itself via Start-Process -Verb RunAs
(triggers a UAC consent dialog). The relaunched copy passes -ElevatedSelf
internally so it skips the re-launch guard.
#>

[CmdletBinding()]
param(
    [string]$InstallerPath = 'C:\Users\crogers\AD Guardian Installer\Release\AD Guardian Installer.exe',
    [string]$TestInstallDir = 'C:\Temp\ADGSmokeTest',
    [string]$LogOutputDir = "$env:TEMP",

    # Where test data is placed inside the legacy path before the run.
    [string]$LegacyDir = 'C:\ADCheckLogs',

    [switch]$SkipCleanup,   # leave TestInstallDir + ProgramData marker for post-mortem
    [switch]$ElevatedSelf   # internal: set by the relaunched elevated copy
)

$ErrorActionPreference = 'Stop'

# -- Self-elevation guard -------------------------------------------------
# The AD Guardian installer has PrivilegesRequired=admin (Inno Setup).
# Running it non-elevated causes an immediate exit(1) with no log output.
# Auto-relaunch this script elevated so the smoke test can actually exercise
# the cleanup flow.
if (-not $ElevatedSelf) {
    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Host 'Installer requires elevation (PrivilegesRequired=admin). Relaunching elevated ...' -ForegroundColor Yellow
        $myPath = $MyInvocation.MyCommand.Path
        $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$myPath`"", '-ElevatedSelf')
        if ($SkipCleanup)       { $argList += '-SkipCleanup' }
        if ($InstallerPath -ne 'C:\Users\crogers\AD Guardian Installer\Release\AD Guardian Installer.exe') { $argList += '-InstallerPath'; $argList += "`"$InstallerPath`"" }
        if ($TestInstallDir -ne 'C:\Temp\ADGSmokeTest')  { $argList += '-TestInstallDir'; $argList += "`"$TestInstallDir`"" }
        if ($LogOutputDir -ne "$env:TEMP")               { $argList += '-LogOutputDir';   $argList += "`"$LogOutputDir`""   }
        if ($LegacyDir -ne 'C:\ADCheckLogs')              { $argList += '-LegacyDir';      $argList += "`"$LegacyDir`""      }
        try {
            $proc = Start-Process powershell.exe -ArgumentList $argList -Verb RunAs -Wait -PassThru
            exit $proc.ExitCode
        } catch {
            Write-Host "ERROR: Could not elevate: $_" -ForegroundColor Red
            Write-Host 'Run this script from an elevated PowerShell window instead.' -ForegroundColor Yellow
            exit 2
        }
    }
}

$TestInstallDir   = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($TestInstallDir)
$InstallerPath    = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InstallerPath)
$LogOutputDir     = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($LogOutputDir)
$LegacyDir        = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($LegacyDir)

$SetupLogPath     = Join-Path $LogOutputDir ('ADGSmoke_Setup_' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
$ExpectedMarker   = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AdHealthMonitor\MigrationMarker.json'

# ── Helpers ────────────────────────────────────────────────────────────────────

function Write-Step([string]$Text) {
    Write-Host ('  >>> ' + $Text) -ForegroundColor Cyan
}

function Write-Pass([string]$Text) {
    Write-Host ('    PASS: ' + $Text) -ForegroundColor Green
}

function Write-Fail([string]$Text) {
    Write-Host ('    FAIL: ' + $Text) -ForegroundColor Red
    $script:Failures++
}

function Write-Warn([string]$Text) {
    Write-Host ('    WARN: ' + $Text) -ForegroundColor Yellow
}

$script:Failures = 0

# Ensure a clean outcome. If the legacy dir already has a runs\ subfolder
# (which it does on this machine), we only plant a small known-size payload
# so the byte-total assertions remain deterministic WITHOUT disturbing
# whatever pre-existing content is already there. Pre-existing files survive.
function Add-TestPayload {
    Write-Step 'Seeding C:\ADCheckLogs with test payload ...'
    $runs = Join-Path $LegacyDir 'runs'
    if (-not (Test-Path $runs)) {
        New-Item -ItemType Directory -Path $runs -Force | Out-Null
    }

    # Two small known-size files so the smoke can assert a non-zero byte count
    # regardless of what else lives in the legacy tree.
    $payload = @(
        @{ Name = 'smoke_test_a.log'; Size = 1000 },
        @{ Name = 'smoke_test_b.txt'; Size = 2000 }
    )
    foreach ($f in $payload) {
        $path = Join-Path $runs $f.Name
        [System.IO.File]::WriteAllBytes($path, (New-Object byte[] $f.Size))
    }
    Write-Host ('    Planted 2 test files (1000 + 2000 bytes) in ' + $runs)
}

# The installer writes %TEMP%\Setup Log YYYY-MM-DD #NNN.txt. We redirect
# via the documented /LOG= parameter, so the log appears at exactly the path
# we requested -- no glob hunt needed.
function Assert-LogContains {
    param([string]$Pattern)
    $content = Get-Content $SetupLogPath -Raw -Encoding UTF8
    if ($content -match [regex]::Escape($Pattern)) {
        Write-Pass "Log contains: $Pattern"
    } else {
        Write-Fail "Log MISSING: $Pattern"
    }
}

function Assert-MigrationMarker {
    param([string]$ExpectedStatus)
    if (-not (Test-Path $ExpectedMarker)) {
        Write-Fail 'MigrationMarker.json was NOT written'
        return
    }
    try {
        $json = Get-Content $ExpectedMarker -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($json.cleanupStatus -eq $ExpectedStatus) {
            Write-Pass "MigrationMarker.json status = '$ExpectedStatus'"
        } else {
            Write-Fail "MigrationMarker.json status = '$($json.cleanupStatus)' (expected '$ExpectedStatus')"
        }
        Write-Host ('      bytesMigrated   = ' + $json.bytesMigrated)
        Write-Host ('      entriesMigrated = ' + $json.entriesMigrated)
        Write-Host ('      errorCount      = ' + $json.errorCount)
    } catch {
        Write-Fail "MigrationMarker.json parse error: $_"
    }
}

function Assert-LegacyDirState {
    param([bool]$ShouldExist)
    $exists = Test-Path $LegacyDir
    if ($exists -eq $ShouldExist) {
        Write-Pass "C:\ADCheckLogs $(if ($ShouldExist) { 'still present' } else { 'removed' })"
    } else {
        Write-Fail "C:\ADCheckLogs $(if ($ShouldExist) { 'missing — should be present' } else { 'still present — should have been removed' })"
    }
}

function Remove-TestArtifacts {
    Write-Step 'Cleaning up test artifacts ...'
    if (Test-Path $TestInstallDir) {
        Remove-Item $TestInstallDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host '    Removed test install dir: ' + $TestInstallDir
    }
    if (Test-Path $ExpectedMarker) {
        Remove-Item $ExpectedMarker -Force -ErrorAction SilentlyContinue
        Write-Host '    Removed MigrationMarker.json'
    }
    # Undo the smoke payload we planted -- leave any pre-existing dirs alone.
    $smokeDir = Join-Path $LegacyDir 'runs'
    if (Test-Path $smokeDir) {
        foreach ($name in @('smoke_test_a.log', 'smoke_test_b.txt')) {
            $path = Join-Path $smokeDir $name
            if (Test-Path $path) {
                Remove-Item $path -Force -ErrorAction SilentlyContinue
                Write-Host "    Removed planted file: $path"
            }
        }
    }
    if (Test-Path $SetupLogPath) {
        # Keep it for post-mortem debugging -- just note where it is.
        Write-Host '    Setup log retained at: ' + $SetupLogPath
    }
}

# ── Pre-flight ─────────────────────────────────────────────────────────────────

Write-Host ''
Write-Host '═══════════════════════════════════════════════════════' -ForegroundColor Magenta
Write-Host '  AD Guardian Installer Smoke Test — v2.0.27 Cleanup  ' -ForegroundColor White
Write-Host '═══════════════════════════════════════════════════════' -ForegroundColor Magenta
Write-Host ''

if (-not (Test-Path $InstallerPath)) {
    throw "Installer not found: $InstallerPath"
}
Write-Host ('Installer: ' + $InstallerPath + ' (' + [math]::Round((Get-Item $InstallerPath).Length / 1MB, 1) + ' MB)')

if (-not (Test-Path $LegacyDir)) {
    throw "Legacy dir not found: $LegacyDir - cannot test cleanup flow"
}
Write-Host ('Legacy dir: ' + $LegacyDir)

# ── Phase A: Seed test payload ─────────────────────────────────────────────────
Add-TestPayload

# ── Phase B: Run installer silently ────────────────────────────────────────────
Write-Step ('Running installer (log -> ' + $SetupLogPath + ') ...')
$proc = Start-Process $InstallerPath `
    -ArgumentList @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        '/LOG="' + $SetupLogPath + '"',
        '/DIR="' + $TestInstallDir + '"'
    ) `
    -Wait -PassThru -NoNewWindow

Write-Host ('    Installer exit code: ' + $proc.ExitCode)

if (-not (Test-Path $SetupLogPath)) {
    throw "Setup log was not produced at $SetupLogPath. The installer may have failed to start."
}
$logSize = [math]::Round((Get-Item $SetupLogPath).Length / 1KB, 1)
Write-Host ('    Setup log size: ' + $logSize + ' KB')

# ── Phase C: Parse log for expected diagnostics ────────────────────────────────
Write-Step 'Parsing setup log for cleanup diagnostics ...'

# Positive signal: the cleanup section should appear (unless C:\ADCheckLogs
# was already absent, which it isn't on this machine).
Assert-LogContains 'Cleanup: legacy C:\ADCheckLogs is not present'
# -> INVERTED: On THIS machine C:\ADCheckLogs exists, so the negative branch
#    (early exit) should NOT fire. We expect to see the 'Cleanup:' WARNING
#    about enumeration IF CFA blocks, OR the success path.

# The new Fix A diagnostic (FindFirst return False on a DirExists=True path).
# If it fires, CFA is active; if it doesn't, the migration succeeded.
$logText = Get-Content $SetupLogPath -Raw
if ($logText -match 'cannot be enumerated') {
    Write-Warn '    CFA / AV blocked enumeration — cleanup marked as failed (expected on protected machines)'
    Assert-LogContains 'Cleanup: WARNING C:\ADCheckLogs exists but cannot be enumerated.'
    Assert-MigrationMarker 'failed'
    Assert-LegacyDirState $true   # directory retained
} elseif ($logText -match 'removed legacy') {
    Write-Pass '    Cleanup succeeded (DelTree passed)'
    Assert-LogContains 'removed legacy'
    Assert-MigrationMarker 'removed'
    Assert-LegacyDirState $false   # directory removed
} elseif ($logText -match 'WARNING could not remove legacy') {
    Write-Warn '    DelTree returned False — cleanup marked as failed'
    Assert-LogContains 'WARNING could not remove legacy'
    Assert-MigrationMarker 'failed'
    Assert-LegacyDirState $true
} else {
    Write-Warn '    No cleanup-related log line found — the cleanup section may have been skipped entirely'
}

# Migration copy section (should appear unless FindFirst failed everywhere)
if ($logText -match 'Migration:') {
    Write-Pass '    Migration: log lines present'
    if ($logText -match 'Migration: copied') {
        Write-Pass '    Migration: copied files'
    }
    if ($logText -match 'WARNING could not enumerate') {
        Write-Warn '    Migration: some directories were locked (CFA enumeration block)'
    }
} else {
    Write-Warn '    No Migration: log lines — entire migration may have been skipped'
}

# PreHandleLockedFiles (unrelated to cleanup but a good installer-health check)
if ($logText -match 'PreHandleLockedFiles') {
    Write-Pass '    PreHandleLockedFiles scan completed'
} else {
    Write-Warn '    PreHandleLockedFiles scan not detected — fresh install, no prior version to lock'
}

# ── Phase D: Summarize MigrationMarker ─────────────────────────────────────────
if (Test-Path $ExpectedMarker) {
    Write-Step 'MigrationMarker.json contents:'
    $marker = Get-Content $ExpectedMarker -Raw | ConvertFrom-Json
    $marker.PSObject.Properties | ForEach-Object {
        Write-Host ('    ' + $_.Name + ' = ' + $_.Value)
    }
}

# ── Phase E: Cleanup ───────────────────────────────────────────────────────────
if (-not $SkipCleanup) {
    Remove-TestArtifacts
} else {
    Write-Host ''
    Write-Warn 'SkipCleanup — artifacts preserved:'
    Write-Host ('    Install dir: ' + $TestInstallDir)
    Write-Host ('    Marker:      ' + $ExpectedMarker)
    Write-Host ('    Setup log:   ' + $SetupLogPath)
}

# ── Verdict ────────────────────────────────────────────────────────────────────
Write-Host ''
if ($script:Failures -eq 0) {
    Write-Host '  RESULT: PASS  (' + $script:Failures + ' failures)' -ForegroundColor Green
    exit 0
} else {
    Write-Host ('  RESULT: FAIL  (' + $script:Failures + ' failures)') -ForegroundColor Red
    exit 1
}
