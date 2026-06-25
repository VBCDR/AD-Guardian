# Smoke test for scripts/cleanup-adchecklogs.ps1 Copy-Tree collision-rename behaviour.
# After the [System.IO.File]::Copy(src, dest, $false) swap, the second migration run
# against the same destination must NOT silently overwrite previously-imported files.
# Expected: two distinct timestamped legacy-import-yyyymmdd-HHmmss subfolders.
#
# This file is a transient smoke harness; safe to delete after verification.

[CmdletBinding()]
param(
    [string]$Helper = 'C:\Users\crogers\AD-Guardian\scripts\cleanup-adchecklogs.ps1'
)

$ErrorActionPreference = 'Stop'

$src = Join-Path $env:TEMP ("CclSmokeSrc_" + (Get-Random))
$dst = Join-Path $env:TEMP ("CclSmokeDst_" + (Get-Random))
New-Item -ItemType Directory -Path $src -Force | Out-Null
New-Item -ItemType Directory -Path $dst -Force | Out-Null

# Stage legacy .log files. Copy-Tree allowlist = .log / .txt / .json / .csv.
'first migration payload'  | Set-Content -LiteralPath (Join-Path $src 'run1.log')
'second migration payload' | Set-Content -LiteralPath (Join-Path $src 'run2.log')
New-Item -ItemType Directory -Path (Join-Path $src 'subdir') -Force | Out-Null
'third migration payload'  | Set-Content -LiteralPath (Join-Path $src 'subdir\run3.log')

Write-Host "Source:    $src  (3 .log files in 2 subdirs)"
Write-Host "Dest base: $dst"
Write-Host ""

Write-Host "=== Run 1: -Migrate -Force -Json ==="
$run1 = & $Helper -Path $src -DestinationPath $dst -Migrate -Force -Json
Write-Host $run1

Write-Host ""
Write-Host "=== Run 2 (same legacy tree, same destination) ==="
$run2 = & $Helper -Path $src -DestinationPath $dst -Migrate -Force -Json
Write-Host $run2

Write-Host ""
Write-Host "=== Audit: destination root subfolders + file counts ==="
$subfolders = @(Get-ChildItem -LiteralPath $dst -Directory -ErrorAction SilentlyContinue)
Write-Host ("Subfolders found: {0}" -f $subfolders.Count)
$totalFiles = 0
$collisionRenamed = 0
foreach ($s in $subfolders) {
    $files = @(Get-ChildItem -LiteralPath $s.FullName -Recurse -File -ErrorAction SilentlyContinue)
    $totalFiles += $files.Count
    foreach ($f in $files) { if ($f.Name -match '-\d{8}-\d{6}\.(log|txt|json|csv)$') { $collisionRenamed++ } }
    Write-Host ("  {0}  ({1} files)" -f $s.Name, $files.Count)
}

Write-Host ""
Write-Host ("Total files:    {0}" -f $totalFiles)
Write-Host ("Collision-rename suffix matches: {0}" -f $collisionRenamed)

# The real invariant:
#   Run 1 copies 3 .log files to the destination tree.
#   Run 2 attempts to copy the SAME 3 files again. Without the fix,
#   File.Copy overwrite=$false would NOT be honoured (Copy-Item -Force
#   silently overwrites), so total file count would stay at 3.
#   With the fix, Run 2 hits the collision-rename path on each file,
#   producing 3 collision-renamed copies (e.g. "run1.log-20260625-074217.log")
#   alongside the originals -- total 6 files.
$expectedFiles = 6   # 3 from Run 1 + 3 collision-renamed from Run 2
$expectedRenamed = 3 # all of Run 2's files qualify as collision-renamed

if ($totalFiles -ge $expectedFiles -and $collisionRenamed -ge $expectedRenamed) {
    Write-Host "RESULT: PASS -- Run 2 produced collision-renamed copies; no silent overwrite." -ForegroundColor Green
    $exitCode = 0
} else {
    Write-Host "RESULT: FAIL -- expected at least $expectedFiles total / $expectedRenamed renamed, got $totalFiles / $collisionRenamed. Collision branch unreachable." -ForegroundColor Red
    $exitCode = 1
}

# Cleanup temp dirs.
Remove-Item -LiteralPath $src -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $dst -Recurse -Force -ErrorAction SilentlyContinue
exit $exitCode
