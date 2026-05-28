[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [switch]$Portable,
    [switch]$Installer,
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"

if (-not $Portable -and -not $Installer) {
    $Portable = $true
    $Installer = $true
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "Domain Guardian.csproj"
$assemblyInfoPath = Join-Path $repoRoot "AssemblyInfo.cs"
$installerProjectDir = Join-Path (Split-Path -Parent $repoRoot) "AD Guardian Installer"
$installerScriptPath = Join-Path $installerProjectDir "AD Guardian Installer.iss"

$distRoot = Join-Path $repoRoot "artifacts\distributions"
$portableRoot = Join-Path $distRoot ("portable\" + $RuntimeIdentifier)
$portableAppDir = Join-Path $portableRoot "app"
$installerOutputDir = Join-Path $installerProjectDir "Release"

function Reset-Directory([string]$Path) {
    if (Test-Path $Path) {
        Remove-Item -Path $Path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Path | Out-Null
}

function Copy-IfExists([string]$Source, [string]$Destination) {
    if (Test-Path $Source) {
        Copy-Item -Path $Source -Destination $Destination -Force
    }
}

function Get-AppVersion([string]$AssemblyInfoPath) {
    $match = Select-String -Path $AssemblyInfoPath -Pattern 'AssemblyInformationalVersion\("([^"]+)"\)' | Select-Object -First 1
    if ($match -and $match.Matches.Count -gt 0) {
        return $match.Matches[0].Groups[1].Value
    }

    return "2.0.0"
}

if ($Portable) {
    Reset-Directory $portableAppDir

    & dotnet publish $projectPath `
        -c $Configuration `
        -p:PublishProfile=PortableWinX64 `
        -p:RuntimeIdentifier=$RuntimeIdentifier `
        -o $portableAppDir

    Copy-IfExists (Join-Path $repoRoot "AD-Guardian-logo-_2_.ico") (Join-Path $portableAppDir "AD Guardian logo.ico")

    if (-not $NoZip) {
        $zipPath = Join-Path $portableRoot ("DomainGuardian-portable-" + $RuntimeIdentifier + ".zip")
        if (Test-Path $zipPath) {
            Remove-Item $zipPath -Force
        }

        Compress-Archive -Path (Join-Path $portableAppDir "*") -DestinationPath $zipPath
    }
}

if ($Installer) {
    if (-not (Test-Path $installerScriptPath)) {
        throw "Installer script not found at '$installerScriptPath'."
    }

    if (-not (Test-Path $portableAppDir)) {
        throw "Portable publish payload not found at '$portableAppDir'. Build portable output first or pass -Portable."
    }

    Reset-Directory $installerOutputDir

    $isccCandidates = @(
        "$Env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
        "$Env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )
    $iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) {
        throw "ISCC.exe was not found. Install Inno Setup 6 on Windows to build the installer."
    }

    $appVersion = Get-AppVersion $assemblyInfoPath
    $payloadDir = (Resolve-Path $portableAppDir).Path
    $resolvedOutputDir = (Resolve-Path $installerOutputDir).Path
    $resolvedInstallerScript = (Resolve-Path $installerScriptPath).Path

    & $iscc `
        "/DMyAppVersion=$appVersion" `
        "/DSourcePayloadDir=$payloadDir" `
        "/DInstallerOutputDir=$resolvedOutputDir" `
        $resolvedInstallerScript

    $compilerExitCode = $LASTEXITCODE
    if ($null -eq $compilerExitCode) {
        $compilerExitCode = 0
    }

    if ($compilerExitCode -ne 0) {
        throw "Installer build failed with exit code $compilerExitCode."
    }

    $primaryInstaller = Join-Path $installerOutputDir "AD Guardian Installer.exe"
    if (Test-Path $primaryInstaller) {
        Copy-Item -Path $primaryInstaller -Destination (Join-Path $installerOutputDir "setup.exe") -Force
    }
}
