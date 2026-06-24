$ErrorActionPreference = 'Continue'

Write-Host '=== A: AD Guardian install info from registry ==='
$paths = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
           'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
           'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*')
$hits = foreach ($p in $paths) {
    Get-ItemProperty -Path $p -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -match 'AD Guardian|Domain Guardian' }
}
if ($hits) {
    foreach ($h in $hits) {
        Write-Host ("  Name              : " + $h.DisplayName)
        Write-Host ("  DisplayVersion    : " + $h.DisplayVersion)
        Write-Host ("  Publisher         : " + $h.Publisher)
        Write-Host ("  InstallLocation   : " + $h.InstallLocation)
        Write-Host ("  UninstallString   : " + $h.UninstallString)
        Write-Host '  ---'
    }
} else {
    Write-Host '  No AD Guardian / Domain Guardian install found in registry.'
}

Write-Host ''
Write-Host '=== B: candidate install locations ==='
$candidates = @(
    'C:\Program Files\AD Guardian',
    'C:\Program Files (x86)\AD Guardian',
    'C:\Program Files\Domain Guardian',
    "$env:LOCALAPPDATA\AD Guardian",
    "$env:LOCALAPPDATA\Programs\AD Guardian",
    "$env:LOCALAPPDATA\Domain Guardian"
)
foreach ($c in $candidates) {
    if (Test-Path $c) {
        Write-Host ("  PRESENT : " + $c)
        Get-ChildItem -Path $c -File -Filter '*.exe' -ErrorAction SilentlyContinue |
            Select-Object -First 4 | ForEach-Object { Write-Host ("             exe: " + $_.FullName + " (" + $_.Length + ")") }
    } else {
        Write-Host ("  absent  : " + $c)
    }
}

Write-Host ''
Write-Host '=== C: AD Guardian / Domain Guardian processes currently running ==='
$running = Get-Process -Name 'AD Guardian','Domain Guardian','Guardian' -ErrorAction SilentlyContinue
if ($running) {
    foreach ($p in $running) {
        Write-Host ("  PID      : " + $p.Id + "  Name: " + $p.ProcessName + "  Path: " + $p.Path + "  StartTime: " + $p.StartTime)
        Write-Host ('  Modules matching clrjit/mscoree/ni_/Microsoft.NET.Core:')
        try {
            $mods = Get-Process -Id $p.Id -Module -ErrorAction Stop | Where-Object { $_.ModuleName -match 'clrjit|mscoree|ni_d|^Microsoft\.NET\.Core' }
            foreach ($m in $mods) {
                Write-Host ("    mmapped: " + $m.FileName)
            }
        } catch {
            Write-Host ("    (could not enumerate modules: " + $_.Exception.Message + ")")
        }
    }
} else {
    Write-Host '  No AD/Domain Guardian process currently running.'
}

Write-Host ''
Write-Host '=== D: v2.0.25 installer asset on disk ==='
$installer = 'C:\Users\crogers\AD Guardian Installer\Release\AD Guardian Installer.exe'
if (Test-Path $installer) {
    $info = Get-Item $installer
    Write-Host ("  Path   : " + $info.FullName)
    Write-Host ("  Size   : " + $info.Length + " bytes")
    Write-Host ("  MTime  : " + $info.LastWriteTime)
    $sha = (Get-FileHash $installer -Algorithm SHA256).Hash
    Write-Host ("  SHA256 : " + $sha)
} else {
    Write-Host "  NOT FOUND: $installer"
}

Write-Host ''
Write-Host '=== E: working directory of THIS process (where installer would run from) ==='
Write-Host ("  CWD: " + (Get-Location).Path)
