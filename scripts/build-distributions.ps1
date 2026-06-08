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

$dotnetPath = "C:\Program Files\dotnet\dotnet.exe"
if (-not (Test-Path $dotnetPath)) {
    $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
}

if (-not $Portable -and -not $Installer) {
    $Portable = $true
    $Installer = $true
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "Domain Guardian.csproj"
$assemblyInfoPath = Join-Path $repoRoot "AssemblyInfo.cs"
$installerProjectDir = $null
$installerScriptPath = $null

$distRoot = Join-Path $repoRoot "artifacts\distributions"
$portableRoot = Join-Path $distRoot ("portable\" + $RuntimeIdentifier)
$portableAppDir = Join-Path $portableRoot "app"
$installerOutputDir = $null
$expectedPortableExe = Join-Path $portableAppDir "Domain Guardian.exe"

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
    if (Test-Path $AssemblyInfoPath) {
        $match = Select-String -Path $AssemblyInfoPath -Pattern 'AssemblyInformationalVersion\("([^"]+)"\)' | Select-Object -First 1
        if ($match -and $match.Matches.Count -gt 0) {
            return $match.Matches[0].Groups[1].Value
        }
    }

    if (Test-Path $projectPath) {
        [xml]$projectXml = Get-Content -Path $projectPath
        $versionNode = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
        if ($versionNode) {
            return [string]$versionNode
        }
    }

    return "2.0.0"
}

function Resolve-InstallerPaths {
    $candidateDirs = @(
        (Join-Path (Split-Path -Parent $repoRoot) "AD Guardian Installer"),
        (Join-Path $repoRoot "installer")
    )

    foreach ($candidateDir in $candidateDirs) {
        $candidateScript = Join-Path $candidateDir "AD Guardian Installer.iss"
        if (Test-Path $candidateScript) {
            return @{
                Dir = $candidateDir
                Script = $candidateScript
                Output = Join-Path $candidateDir "Release"
            }
        }
    }

    throw "Installer script was not found in any expected location."
}

function Assert-PortablePayload {
    if (-not (Test-Path $expectedPortableExe)) {
        $payloadContents = if (Test-Path $portableAppDir) {
            (Get-ChildItem -Path $portableAppDir -Force | Select-Object -ExpandProperty Name) -join ", "
        } else {
            "<missing directory>"
        }

        throw "Portable publish did not produce '$expectedPortableExe'. Current payload contents: $payloadContents"
    }
}

function Publish-PortableApp {
    Reset-Directory $portableAppDir

    & $dotnetPath publish $projectPath `
        -c $Configuration `
        -p:PublishProfile=PortableWinX64 `
        -p:RuntimeIdentifier=$RuntimeIdentifier `
        -o $portableAppDir

    Copy-IfExists (Join-Path $repoRoot "AD-Guardian-logo-_2_.ico") (Join-Path $portableAppDir "AD Guardian logo.ico")
    Assert-PortablePayload
}

function Create-PortableZip {
    $zipPath = Join-Path $portableRoot ("DomainGuardian-portable-" + $RuntimeIdentifier + ".zip")
    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $portableAppDir "*") -DestinationPath $zipPath
}

if ($Installer) {
    $resolvedInstaller = Resolve-InstallerPaths
    $installerProjectDir = $resolvedInstaller.Dir
    $installerScriptPath = $resolvedInstaller.Script
    $installerOutputDir = $resolvedInstaller.Output
}

if ($Portable) {
    Publish-PortableApp

    if (-not $NoZip) {
        Create-PortableZip
    }
}

if ($Installer) {
    if (-not (Test-Path $portableAppDir)) {
        throw "Portable publish payload not found at '$portableAppDir'. Build portable output first or pass -Portable."
    }

    Assert-PortablePayload

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

}
