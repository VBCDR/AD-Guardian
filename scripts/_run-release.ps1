#requires -Version 5.1
$ErrorActionPreference = 'Continue'

$repoRoot = 'C:\Users\crogers\AD-Guardian'
$transcriptPath = Join-Path $repoRoot 'artifacts\release-run.log'
New-Item -Path (Split-Path -Parent $transcriptPath) -ItemType Directory -Force | Out-Null
Start-Transcript -Path $transcriptPath -Force | Out-Null

Write-Host "=== Pre-cleanup: removing debug helpers so they don't get committed ===" -ForegroundColor Yellow
foreach ($f in @(
    (Join-Path $repoRoot 'scripts\_diag.ps1'),
    (Join-Path $repoRoot 'scripts\_probe.ps1'),
    (Join-Path $repoRoot 'scripts\_restore-properly.ps1'),
    (Join-Path $repoRoot 'artifacts\release-run.log'),
    (Join-Path $repoRoot 'artifacts\diag.log'),
    (Join-Path $repoRoot 'artifacts\probe.log'),
    (Join-Path $repoRoot 'artifacts\restore.log')
)) {
    if (Test-Path $f) {
        Remove-Item -Path $f -Force
        Write-Host "  Removed: $(Split-Path -Leaf $f)"
    }
}

# Override Read-Host with an INDEX-COUNTER queue (bug-free vs the array-slice dequeue
# which fails when exactly 1 element remains because $arr[1..0] returns the same element).
$script:readHostResponses = @("1", "Y", "1", "")
$script:readHostIndex = 0
function global:Read-Host {
    param([Parameter(Position=0)][string]$Prompt)
    if ($Prompt) { Write-Host $Prompt -NoNewline }
    if ($script:readHostIndex -lt $script:readHostResponses.Length) {
        $response = $script:readHostResponses[$script:readHostIndex]
        $script:readHostIndex++
        Write-Host $response
        return $response
    } else {
        Write-Host ""
        return ""
    }
}

Write-Host ""
Write-Host "=== Running release.ps1 ===" -ForegroundColor Yellow
try {
    . (Join-Path $repoRoot 'scripts\release.ps1') -Configuration Release
    Write-Host ""
    Write-Host "=== release.ps1 finished, LASTEXITCODE=$LASTEXITCODE ===" -ForegroundColor Magenta
} catch {
    Write-Host ""
    Write-Host "=== release.ps1 threw an exception ===" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor Red
}

Write-Host ""
Write-Host "=== Post-pipeline verification ===" -ForegroundColor Yellow
$gitExe = 'C:\Program Files\Git\bin\git.exe'
$ghExe  = 'C:\Program Files\GitHub CLI\gh.exe'

Write-Host "Git log (last 5):"
& $gitExe -C $repoRoot log --oneline -5 2>&1 | ForEach-Object { Write-Host "  $_" }

Write-Host "Git status:"
$status = & $gitExe -C $repoRoot status --short 2>&1
if ([string]::IsNullOrWhiteSpace($status)) { Write-Host "  (clean)" } else { $status | ForEach-Object { Write-Host "  $_" } }

Write-Host "Tags:"
& $gitExe -C $repoRoot tag -l 2>&1 | ForEach-Object { Write-Host "  $_" }

Write-Host "v2.0.17 release on GitHub:"
& $ghExe release view v2.0.17 --repo VBCDR/AD-Guardian 2>&1 | Select-Object -First 12 | ForEach-Object { Write-Host "  $_" }

Stop-Transcript | Out-Null
Write-Host ""
Write-Host "Transcript saved to: $transcriptPath"
