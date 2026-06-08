[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [string]$Title,
    [string]$Notes = "",
    [string]$InstallerPath,
    [switch]$Draft,
    [switch]$Latest
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

Copy-Item -LiteralPath $installerPath -Destination $uploadAssetPath -Force

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
$releaseView = & $gh release view $Tag --repo $repo 2>$null

if ($LASTEXITCODE -ne 0) {
    $createArgs = @("release", "create", $Tag, $uploadAssetPath, "--repo", $repo, "--title", $Title)
    if ($Notes) {
        $createArgs += @("--notes", $Notes)
    } else {
        $createArgs += @("--generate-notes")
    }
    if ($Draft) { $createArgs += "--draft" }
    if ($Latest) { $createArgs += "--latest" }

    & $gh @createArgs
    exit $LASTEXITCODE
}

& $gh release upload $Tag $uploadAssetPath --repo $repo --clobber
exit $LASTEXITCODE
