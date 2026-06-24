$ErrorActionPreference = 'Continue'
$WarningPreference    = 'SilentlyContinue'

$Installer  = 'C:\Users\crogers\AD Guardian Installer\Release\AD Guardian Installer.exe'
$InstallDir = 'C:\Program Files\AD Guardian'
$LogPath    = Join-Path $env:TEMP 'ad_guardian_smoke_install.log'
$BgLogPath  = Join-Path $env:TEMP 'ad_guardian_smoke_install.bg.log'

Write-Host '=================================================================='
Write-Host ' AD Guardian installer-over-running-install SMOKE TEST (v2.0.25)'
Write-Host '=================================================================='
Write-Host ("Installer : " + $Installer)
Write-Host ("InstallDir: " + $InstallDir)
Write-Host ("Inno log  : " + $LogPath)
Write-Host ''

Write-Host '=== PRE-SNAPSHOT (process / files / version) ==='
$preProc = Get-Process -Name 'Domain Guardian','AD Guardian' -ErrorAction SilentlyContinue
foreach ($p in $preProc) {
    Write-Host ("ALIVE  PID {0}  Name='{1}'  StartTime={2}  Path='{3}'" -f $p.Id, $p.ProcessName, $p.StartTime, $p.Path)
    Write-Host ('  Modules matched (runtime locks):')
    try {
        $mods = Get-Process -Id $p.Id -Module -ErrorAction Stop |
                Where-Object { $_.ModuleName -match 'clrjit|mscoree|ni_d|^Microsoft\.NET\.Core|hostfxr' }
        foreach ($m in $mods) { Write-Host ("    mmapped: " + $m.FileName) }
    } catch {
        Write-Host ("    (module enum failed: " + $_.Exception.Message + ")")
    }
}
$preExe  = Join-Path $InstallDir 'Domain Guardian.exe'
$preJit  = Join-Path $InstallDir 'clrjit.dll'
if (Test-Path $preExe) {
    $v = (Get-ItemProperty $preExe).VersionInfo
    $sha = (Get-FileHash $preExe -Algorithm SHA256).Hash
    Write-Host ("PRE Domain Guardian.exe : FileVersion='{0}'  size={1}  sha256={2}" -f $v.FileVersion, (Get-Item $preExe).Length, $sha)
}
if (Test-Path $preJit) {
    Write-Host ("PRE clrjit.dll  present : size={0}" -f (Get-Item $preJit).Length)
} else {
    Write-Host 'PRE clrjit.dll  present : NO'
}
Write-Host ''

Write-Host '=== UI WATCHER (background job; detects any modal dialog with ARI markers) ==='
$watcher = {
    param($BgLogPath, $JitterSec)
    $ariSeen = $false
    $ariTitle = ''
    $tStart = Get-Date
    while ((New-TimeSpan -Start $tStart -End (Get-Date)).TotalSeconds -lt 90) {
        Start-Sleep -Milliseconds 500
        # Look at TOP-level windows of all known installer-related processes.
        $candidates = Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowTitle -and `
                          ($_.Path -like '*AD Guardian*' -or `
                           $_.MainWindowTitle -match 'Access is denied|DeleteFile failed|code 5|Setup|Inno' -or `
                           $_.ProcessName -match 'setup|innosetup|isdev') }
        foreach ($c in $candidates) {
            if ($c.MainWindowTitle) {
                $hit = $false
                foreach ($marker in 'DeleteFile failed','code 5','Access is denied','ADRIS','Access Is Denied') {
                    if ($c.MainWindowTitle -like "*$marker*") { $hit = $true; break }
                }
                if ($hit) {
                    $ariSeen = $true
                    $ariTitle = $c.MainWindowTitle
                    Write-Host ("[" + (Get-Date).ToString('HH:mm:ss') + "] WATCHER HIT pid=" + $c.Id + " title='" + $c.MainWindowTitle + "'")
                    break
                }
            }
        }
    }
    $obj = New-Object PSObject -Property @{ ARI=$ariSeen; Title=$ariTitle }
    $obj | Export-Clixml $BgLogPath
    $obj
}
$job = Start-Job -ScriptBlock $watcher -ArgumentList $BgLogPath, 0.5

Write-Host ''
Write-Host '=== INVOKING INSTALLER ==='
Write-Host ('Flags: /SILENT  /NORESTART  /CLOSEAPPLICATIONS  /LOG="' + $LogPath + '"')
Write-Host '  (also implicitly /CLOSEAPPLICATIONS so RM gets first crack; if RM fails or is bypassed,'
Write-Host '   PreHandleLockedFiles runs at the top of CurStepChanged(ssInstall))'
Write-Host ''
$proc = Start-Process -FilePath $Installer `
    -ArgumentList @('/SILENT','/NORESTART','/LOG=' + $LogPath) `
    -PassThru -WindowStyle Hidden
Write-Host ("Installer PID: " + $proc.Id + "  StartTime: " + $proc.StartTime)

# Wait up to 90s for installer to exit
$exited = $proc.WaitForExit(90000)
Write-Host ("WaitForExit returned: " + $exited)
Write-Host ("Installer ExitCode  : " + $proc.ExitCode)
Write-Host ("Installer EndTime   : " + (Get-Date))
try { $proc.Close() } catch {}

# Stop watcher job
$watcherResult = Receive-Job $job -Wait
Remove-Job $job -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host '=== POST-SNAPSHOT ==='
$postProc = Get-Process -Name 'Domain Guardian','AD Guardian' -ErrorAction SilentlyContinue
foreach ($p in $postProc) {
    Write-Host ("ALIVE  PID {0}  Name='{1}'  StartTime={2}" -f $p.Id, $p.ProcessName, $p.StartTime)
}
$postExe = Join-Path $InstallDir 'Domain Guardian.exe'
$postJit = Join-Path $InstallDir 'clrjit.dll'
if (Test-Path $postExe) {
    $v = (Get-ItemProperty $postExe).VersionInfo
    $sha = (Get-FileHash $postExe -Algorithm SHA256).Hash
    Write-Host ("POST Domain Guardian.exe : FileVersion='{0}'  size={1}  sha256={2}" -f $v.FileVersion, (Get-Item $postExe).Length, $sha)
}
if (Test-Path $postJit) {
    Write-Host ("POST clrjit.dll present  : size={0}" -f (Get-Item $postJit).Length)
} else {
    Write-Host 'POST clrjit.dll present  : NO (was renamed away)'
}

Write-Host ''
Write-Host '=== INNO LOG ANALYSIS ==='
if (Test-Path $LogPath) {
    $content = Get-Content $LogPath -Raw -ErrorAction SilentlyContinue
    Write-Host ("Inno log size : " + (Get-Item $LogPath).Length + ' bytes')
    Write-Host ''
    if ($content) {
        Write-Host '--- hits for DeleteFile failed / code 5 / ARI markers ---'
        $hits = $content -split "`r?`n" |
                Where-Object { $_ -match 'PreHandleLockedFiles|QueueLockedFileForRebootRemoval|deleteme|DeleteFile failed|code 5|Need Restart|Restart needed|MoveFileExW' }
        if ($hits) {
            foreach ($h in $hits) { Write-Host ('  ' + $h) }
        } else {
            Write-Host '  (none — neither ARI nor PreHandleLockedFiles path was reached)'
        }
        Write-Host ''
        Write-Host '--- first 8 + last 6 lines of Inno log (whole-file transcript if PreHandleLockedFiles ran) ---'
        $lines = $content -split "`r?`n"
        $n = $lines.Count
        if ($n -le 16) {
            $lines | ForEach-Object { Write-Host ('  ' + $_) }
        } else {
            @(0..7) + @(($n-6)..($n-1)) | ForEach-Object { Write-Host ('  L' + ($_+1) + ' ' + $lines[$_]) }
        }
    } else {
        Write-Host 'Inno log empty (installer never wrote anything — odd)'
    }
} else {
    Write-Host ('Inno log not found at ' + $LogPath)
}

Write-Host ''
Write-Host '=== CLEANUP PENDING REBOOT (the rename+queue target dir) ==='
$cleanupDir = Join-Path $InstallDir '__cleanup_pending_reboot__'
if (Test-Path $cleanupDir) {
    Get-ChildItem $cleanupDir | ForEach-Object {
        Write-Host ("  queued: " + $_.FullName + "  size=" + $_.Length)
    }
} else {
    Write-Host '  __cleanup_pending_reboot__  : absent (PreHandleLockedFiles did not create it)'
}

Write-Host ''
Write-Host '=== STRANDED .deleteme files in install dir (should be empty once queued) ==='
Get-ChildItem -Path $InstallDir -Filter '*.deleteme' -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host ("  stranded: " + $_.FullName + " (" + $_.Length + ")") }
$hasStranded = (Get-ChildItem -Path $InstallDir -Filter '*.deleteme' -ErrorAction SilentlyContinue | Measure-Object).Count
if ($hasStranded -eq 0) { Write-Host '  (none) clean' }

Write-Host ''
Write-Host '=== UI WATCHER VERDICT ==='
if (Test-Path $BgLogPath) {
    $w = Import-Clixml $BgLogPath
    Write-Host ("Watcher observed ARI dialog : " + $w.ARI)
    if ($w.ARI) { Write-Host ("  title captured       : '" + $w.Title + "'") }
} else {
    Write-Host '  no watcher output captured'
}

Write-Host ''
Write-Host '=== PENDING REBOOT REGISTRY (MoveFileExW-MOVEFILE_DELAY_UNTIL_REBOOT entries) ==='
$pfro = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\' -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty PendingFileRenameOperations -ErrorAction SilentlyContinue
if ($pfro) {
    Write-Host 'PendingFileRenameOperations is set:'
    $pfro | ForEach-Object { Write-Host ('  ' + $_) }
} else {
    Write-Host '  PendingFileRenameOperations is empty (kernel processed or never queued)'
}

Write-Host ''
Write-Host '=================================================================='
Write-Host ' SMOKE TEST DONE'
Write-Host '=================================================================='
