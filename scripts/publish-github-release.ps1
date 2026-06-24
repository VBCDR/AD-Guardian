[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [string]$Title,
    [string]$Notes = "",
    [string]$InstallerPath,
    [switch]$Draft,
    [switch]$Latest,
    [ValidateRange(1, 50)]
    [int]$MaxBulletCount = 8
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$uploadAssetPath = Join-Path $env:TEMP "AD.Guardian.Installer.exe"

# Resolve the installer path: use explicit param, or check both candidate locations
if ($InstallerPath -and (Test-Path $InstallerPath)) {
    $resolvedInstaller = $InstallerPath
} else {
    $candidates = @(
        (Join-Path (Split-Path -Parent $repoRoot) "AD Guardian Installer\Release\AD Guardian Installer.exe"),
        (Join-Path $repoRoot "installer\Release\AD Guardian Installer.exe")
    )
    $resolvedInstaller = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $resolvedInstaller) {
    throw "Installer not found. Build it first with build-distributions.ps1, or pass -InstallerPath."
}

Copy-Item -LiteralPath $resolvedInstaller -Destination $uploadAssetPath -Force

$ghCandidates = @(
    "gh",
    "$Env:ProgramFiles\GitHub CLI\gh.exe",
    "$Env:LOCALAPPDATA\Programs\GitHub CLI\gh.exe",
    "C:\Program Files\GitHub CLI\gh.exe",
    "C:\Users\$Env:USERNAME\AppData\Local\Programs\GitHub CLI\gh.exe"
)

$gh = $ghCandidates | Where-Object { $_ -eq "gh" -or (Test-Path $_) } | Select-Object -First 1
if (-not $gh) {
    throw "GitHub CLI (gh) was not found."
}

if (-not $Title) {
    $Title = $Tag
}

$repo = "VBCDR/AD-Guardian"
$releaseExists = $false
try {
    $null = & $gh release view $Tag --repo $repo --json tagName 2>$null
    if ($LASTEXITCODE -eq 0) {
        $releaseExists = $true
    }
} catch {
    $releaseExists = $false
}

# ── Natural-tone notes generator ──────────────────────────────────────────
# Imported from scripts/ReleaseNotes.psm1 so the same generator can be unit
# tested by the xUnit suite (Domain Guardian.Tests) via a subprocess pwsh
# harness. See ReleaseNotes.psm1 for the full generator logic and the
# optional -ForcedPreviousTag / -ForcedCommits / -ForceNoPreviousTag
# parameters used by tests.
Import-Module -Name (Join-Path $PSScriptRoot 'ReleaseNotes.psm1') -Force

if (-not $Notes -and -not $releaseExists) {
    $generatedNotes = Build-NaturalReleaseNotes -CurrentTag $Tag -MaxBullets $MaxBulletCount
    if ($generatedNotes) {
        $Notes = $generatedNotes
        Write-Host "  → Generated natural-tone release notes from commit log:" -ForegroundColor Gray
        foreach ($noteLine in ($Notes -split "`n")) {
            Write-Host "    $noteLine" -ForegroundColor DarkGray
        }
    } else {
        Write-Host "  → Could not read commit history; falling back to --generate-notes." -ForegroundColor Yellow
    }
}

if (-not $releaseExists) {
    $createArgs = @("release", "create", $Tag, $uploadAssetPath, "--repo", $repo, "--title", $Title)
    if ($Notes) {
        $createArgs += @("--notes", $Notes)
    } else {
        $createArgs += "--generate-notes"
    }
    if ($Draft) { $createArgs += "--draft" }
    if ($Latest) { $createArgs += "--latest" }

    & $gh @createArgs
    exit $LASTEXITCODE
}

& $gh release upload $Tag $uploadAssetPath --repo $repo --clobber
exit $LASTEXITCODE
