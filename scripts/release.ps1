[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$SkipPublish,
    [switch]$AutoPush,
    [ValidateSet("1", "2", "3")]
    [string]$VersionChoice,
    [ValidateSet("1", "2")]
    [string]$ReleaseChoice,
    [string]$ReleaseTitle
)

$ErrorActionPreference = "Stop"

# ── Paths ──────────────────────────────────────────────────────────────────────
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "Domain Guardian.csproj"
$testProjectPath = Join-Path $repoRoot "Domain Guardian.Tests\Domain Guardian.Tests.csproj"
$assemblyInfoPath = Join-Path $repoRoot "AssemblyInfo.cs"
$dotnetPath = "C:\Program Files\dotnet\dotnet.exe"
if (-not (Test-Path $dotnetPath)) {
    $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
}

$buildDistScript = Join-Path $PSScriptRoot "build-distributions.ps1"
$publishReleaseScript = Join-Path $PSScriptRoot "publish-github-release.ps1"

# ── Helpers ────────────────────────────────────────────────────────────────────
function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "  $Message" -ForegroundColor White
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host ""
}

function Write-Status([string]$Message, [string]$Color = "Gray") {
    Write-Host "  → $Message" -ForegroundColor $Color
}

function Get-CurrentVersion {
    if (Test-Path $assemblyInfoPath) {
        $match = Select-String -Path $assemblyInfoPath -Pattern 'AssemblyInformationalVersion\("([^"]+)"\)' | Select-Object -First 1
        if ($match -and $match.Matches.Count -gt 0) {
            return $match.Matches[0].Groups[1].Value
        }
    }
    return "0.0.0"
}

function Set-Version([string]$NewVersion) {
    $content = Get-Content $assemblyInfoPath -Raw

    # Parse the new version into components
    $parts = $NewVersion.Split('.')
    $major = $parts[0]
    $minor = if ($parts.Length -gt 1) { $parts[1] } else { "0" }
    $build = if ($parts.Length -gt 2) { $parts[2] } else { "0" }
    $assemblyVersion = "$major.$minor.$build.0"

    $content = $content -replace 'AssemblyVersion\("[^"]+"\)', "AssemblyVersion(`"$assemblyVersion`")"
    $content = $content -replace 'AssemblyFileVersion\("[^"]+"\)', "AssemblyFileVersion(`"$assemblyVersion`")"
    $content = $content -replace 'AssemblyInformationalVersion\("[^"]+"\)', "AssemblyInformationalVersion(`"$NewVersion`")"

    Set-Content -Path $assemblyInfoPath -Value $content -NoNewline
    Write-Status "Version updated to $NewVersion (assembly $assemblyVersion)" "Green"
}

function Get-NextPatchVersion([string]$Current) {
    $parts = $Current.Split('.')
    $major = [int]$parts[0]
    $minor = [int]$parts[1]
    $build = if ($parts.Length -gt 2) { [int]$parts[2] } else { 0 }
    return "$major.$minor.$($build + 1)"
}

function Confirm-Action([string]$Prompt) {
    $response = Read-Host "$Prompt [Y/n]"
    return [string]::IsNullOrWhiteSpace($response) -or $response -match '^[Yy]'
}

function Find-InstallerExe {
    # Check both candidate locations that build-distributions.ps1 uses:
    #   1. Sibling dir: <repoRoot>\..\AD Guardian Installer\Release\
    #   2. Repo-local:  <repoRoot>\installer\Release\
    $candidates = @(
        (Join-Path (Split-Path -Parent $repoRoot) "AD Guardian Installer\Release\AD Guardian Installer.exe"),
        (Join-Path $repoRoot "installer\Release\AD Guardian Installer.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }
    return $null
}

# ── Step 1: Version decision (BEFORE build so artifacts get the new version) ───
Write-Step "Step 1/7 — Version"

$currentVersion = Get-CurrentVersion
Write-Status "Current version: $currentVersion" "White"
Write-Host ""
Write-Host "  What would you like to do with the version?" -ForegroundColor Yellow
Write-Host ""
Write-Host "    [1] Keep current version ($currentVersion) — replace existing release" -ForegroundColor White
Write-Host "    [2] Bump patch version ($(Get-NextPatchVersion $currentVersion)) — new release" -ForegroundColor White
Write-Host "    [3] Enter custom version" -ForegroundColor White
Write-Host ""

if (-not $VersionChoice) {
    $versionChoice = Read-Host "  Choose (1/2/3)"
} else {
    $versionChoice = $VersionChoice
    Write-Status "Using preset version choice: $versionChoice" "Yellow"
}

$newVersion = $currentVersion
$versionChanged = $false

switch ($versionChoice) {
    "1" {
        Write-Status "Keeping version $currentVersion (will replace existing release)" "Yellow"
    }
    "2" {
        $newVersion = Get-NextPatchVersion $currentVersion
        Set-Version $newVersion
        $versionChanged = $true
    }
    "3" {
        $customVersion = Read-Host "  Enter new version (e.g. 2.1.0)"
        if ([string]::IsNullOrWhiteSpace($customVersion)) {
            Write-Status "No version entered — keeping $currentVersion" "Yellow"
        } else {
            $newVersion = $customVersion.Trim()
            Set-Version $newVersion
            $versionChanged = $true
        }
    }
    default {
        Write-Status "Invalid choice — keeping version $currentVersion" "Yellow"
    }
}

$releaseTag = "v$newVersion"
Write-Status "Release tag will be: $releaseTag" "White"

# ── Step 2: Build ──────────────────────────────────────────────────────────────
Write-Step "Step 2/7 — Build ($Configuration)"

Write-Status "Killing any lingering VBCSCompiler processes..."
Get-Process VBCSCompiler -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Write-Status "Building $projectPath..."
$buildOutput = & $dotnetPath build $projectPath --configuration $Configuration 2>&1
$buildSucceeded = $LASTEXITCODE -eq 0

$buildOutput | Where-Object { $_ -match 'error CS|warning CS|Build succeeded|Error\(s\)|Warning\(s\)' } | ForEach-Object { Write-Host "  $_" }

if (-not $buildSucceeded) {
    Write-Host ""
    Write-Host "  ✗ BUILD FAILED — aborting release pipeline." -ForegroundColor Red
    exit 1
}
Write-Status "Build succeeded." "Green"

# ── Step 3: Tests ──────────────────────────────────────────────────────────────
if (-not $SkipTests) {
    Write-Step "Step 3/7 — Tests"

    Write-Status "Killing any lingering testhost processes..."
    Get-Process testhost -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1

    Write-Status "Running tests..."
    $testOutput = & $dotnetPath test $testProjectPath --configuration $Configuration 2>&1
    $testPassed = $LASTEXITCODE -eq 0

    $testOutput | Where-Object { $_ -match 'Passed|Failed|Total|error CS' } | ForEach-Object { Write-Host "  $_" }

    if (-not $testPassed) {
        Write-Host ""
        Write-Host "  ✗ TESTS FAILED — aborting release pipeline." -ForegroundColor Red
        exit 1
    }
    Write-Status "All tests passed." "Green"
} else {
    Write-Step "Step 3/7 — Tests (skipped)"
}

# ── Step 4: Publish ────────────────────────────────────────────────────────────
$installerExePath = $null
$portableExePath = $null

if (-not $SkipPublish) {
    Write-Step "Step 4/7 — Publish distributions"

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $buildDistScript -Configuration $Configuration

    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "  ✗ PUBLISH FAILED — aborting release pipeline." -ForegroundColor Red
        exit 1
    }

    # Find the built artifacts
    $distRoot = Join-Path $repoRoot "artifacts\distributions"
    $portableExePath = Join-Path $distRoot "portable\win-x64\app\Domain Guardian.exe"
    $installerExePath = Find-InstallerExe

    Write-Host ""
    if (Test-Path $portableExePath) {
        $portableSize = [math]::Round((Get-Item $portableExePath).Length / 1MB, 1)
        Write-Status "Portable EXE: $portableExePath ($portableSize MB)" "Green"
    } else {
        Write-Status "Portable EXE: not found at expected path" "Yellow"
    }

    if ($installerExePath) {
        $installerSize = [math]::Round((Get-Item $installerExePath).Length / 1MB, 1)
        Write-Status "Installer EXE: $installerExePath ($installerSize MB)" "Green"
    } else {
        Write-Status "Installer EXE: not found at expected paths" "Yellow"
    }
} else {
    Write-Step "Step 4/7 — Publish (skipped)"
}

# ── Step 5: Commit and push ───────────────────────────────────────────────────
Write-Step "Step 5/7 — Commit and push"

Write-Status "Staging all changes..."
& git -C $repoRoot add -A

$status = & git -C $repoRoot status --short 2>&1
if ([string]::IsNullOrWhiteSpace($status)) {
    Write-Status "No changes to commit." "Yellow"
} else {
    Write-Host "  Staged changes:" -ForegroundColor Gray
    $status | ForEach-Object { Write-Host "    $_" -ForegroundColor Gray }

    if ($versionChanged) {
        $commitMsg = "Release v$newVersion — bump version and publish distributions"
    } else {
        $commitMsg = "Release v$currentVersion — rebuild and publish distributions"
    }

    Write-Status "Committing: $commitMsg"
    & git -C $repoRoot commit -m $commitMsg

    if ($LASTEXITCODE -ne 0) {
        Write-Host "  ✗ Commit failed." -ForegroundColor Red
        exit 1
    }
    Write-Status "Committed." "Green"
}

if ($AutoPush -or (Confirm-Action "Push to remote?")) {
    Write-Status "Pushing to origin/master..."
    & git -C $repoRoot push origin master 2>&1

    if ($LASTEXITCODE -ne 0) {
        Write-Host "  ✗ Push failed — you may need to push manually." -ForegroundColor Red
    } else {
        Write-Status "Pushed." "Green"
    }

    # Also push the tag if version changed
    if ($versionChanged) {
        Write-Status "Creating and pushing tag $releaseTag..."
        & git -C $repoRoot tag -a $releaseTag -m "Release $releaseTag" 2>&1
        & git -C $repoRoot push origin $releaseTag 2>&1

        if ($LASTEXITCODE -eq 0) {
            Write-Status "Tag $releaseTag pushed." "Green"
        }
    }
} else {
    Write-Status "Push skipped — remember to push manually." "Yellow"
}

# ── Step 6: GitHub release ─────────────────────────────────────────────────────
Write-Step "Step 6/7 — GitHub Release"

if ($installerExePath -and (Test-Path $installerExePath)) {
    Write-Host "  Release tag: $releaseTag" -ForegroundColor White
    Write-Host ""
    Write-Host "    [1] Create/update GitHub release with installer" -ForegroundColor White
    Write-Host "    [2] Skip GitHub release" -ForegroundColor White
    Write-Host ""

    if (-not $ReleaseChoice) {
        $releaseChoice = Read-Host "  Choose (1/2)"
    } else {
        $releaseChoice = $ReleaseChoice
        Write-Status "Using preset release choice: $releaseChoice" "Yellow"
    }

    if ($releaseChoice -eq "1") {
        if (-not $ReleaseTitle) {
            $releaseTitle = Read-Host "  Release title (Enter for '$releaseTag')"
            if ([string]::IsNullOrWhiteSpace($releaseTitle)) {
                $releaseTitle = $releaseTag
            }
        } elseif ([string]::IsNullOrWhiteSpace($ReleaseTitle)) {
            $releaseTitle = $releaseTag
        } else {
            $releaseTitle = $ReleaseTitle
            Write-Status "Using preset release title: $releaseTitle" "Yellow"
        }

        Write-Status "Publishing GitHub release..."
        $publishArgs = @('-File', $publishReleaseScript, '-Tag', $releaseTag, '-Title', $releaseTitle, '-Latest')
        if ($installerExePath) {
            $publishArgs += @('-InstallerPath', $installerExePath)
        }
        & powershell.exe -NoProfile -ExecutionPolicy Bypass @publishArgs

        if ($LASTEXITCODE -eq 0) {
            Write-Status "GitHub release published!" "Green"
            Write-Host ""
            Write-Host "  View: https://github.com/VBCDR/AD-Guardian/releases/tag/$releaseTag" -ForegroundColor Cyan
        } else {
            Write-Host "  ✗ GitHub release publish failed." -ForegroundColor Red
        }
    } else {
        Write-Status "GitHub release skipped." "Yellow"
    }
} else {
    Write-Status "Installer not found — skipping GitHub release." "Yellow"
    Write-Status "Build the installer first by running: build-distributions.ps1 -Installer" "Yellow"
}

# ── Summary ────────────────────────────────────────────────────────────────────
Write-Step "Step 7/7 — Summary"

Write-Host "  Release pipeline complete!" -ForegroundColor Green
Write-Host ""
Write-Host "  Version:    $newVersion" -ForegroundColor White
Write-Host "  Tag:        $releaseTag" -ForegroundColor White
if (Test-Path $portableExePath) {
    Write-Host "  Portable:   $portableExePath" -ForegroundColor White
}
if ($installerExePath -and (Test-Path $installerExePath)) {
    Write-Host "  Installer:  $installerExePath" -ForegroundColor White
}
Write-Host ""
