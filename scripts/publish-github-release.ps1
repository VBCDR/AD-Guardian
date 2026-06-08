[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [string]$Title,
    [string]$Notes = "",
    [switch]$Draft,
    [switch]$Latest
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$installerReleaseDir = Join-Path (Split-Path -Parent $repoRoot) "AD Guardian Installer\Release"
$installerPath = Join-Path $installerReleaseDir "AD Guardian Installer.exe"
$uploadAssetPath = Join-Path $env:TEMP "AD.Guardian.Installer.exe"

if (-not (Test-Path $installerPath)) {
    throw "Installer not found at '$installerPath'. Build it first."
}

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
