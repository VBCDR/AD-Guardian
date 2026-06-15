$ErrorActionPreference = 'Continue'
$repo = 'C:\Users\crogers\AD-Guardian'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'

function Step($m) {
  Write-Host ''
  Write-Host "=== $m ===" -ForegroundColor Cyan
}

Step 'Check available SDKs (looking for 9.0.x)'
$sdks = & $dotnet --list-sdks 2>&1
$sdks | ForEach-Object { Write-Host "  $_" }
$has90 = $sdks | Where-Object { $_ -match '^9\.' }
if ($has90) {
  Write-Host "  -> 9.x SDK available: $has90" -ForegroundColor Green
} else {
  Write-Host "  -> No 9.x SDK installed locally (CI will install 9.0.x automatically)" -ForegroundColor Yellow
}

Step 'Pin to 9.0.x with a temporary global.json (if 9.x is available)'
if ($has90) {
  $gj = Join-Path $repo 'global.json'
  $backup = "$gj.bak"
  $gjContent = @{ sdk = @{ version = '9.0.100', rollForward = 'latestFeature' } } | ConvertTo-Json -Depth 5
  if (Test-Path $gj) { Move-Item -LiteralPath $gj -Destination $backup -Force }
  Set-Content -LiteralPath $gj -Value $gjContent -Encoding utf8
  Write-Host "  temporary global.json created (will be removed)"
  & $dotnet --version
} else {
  Write-Host "  skipping global.json (no 9.x SDK locally)"
}

Step 'Restore main project with --use-lock-file (using whichever SDK is active)'
Push-Location $repo
try {
  & $dotnet restore 'Domain Guardian.csproj' --use-lock-file 2>&1 | Select-Object -First 10
  Write-Host "  exit: $LASTEXITCODE"
}
finally {
  Pop-Location
}

Step 'Build main project (Release)'
Push-Location $repo
try {
  & $dotnet build 'Domain Guardian.csproj' -c Release --no-restore --nologo 2>&1 | Select-String -Pattern 'error|Build succeeded|Build FAILED|Warning\(s\)|Error\(s\)' | Select-Object -First 10
  Write-Host "  exit: $LASTEXITCODE"
}
finally {
  Pop-Location
}

Step 'Build test project (Release)'
Push-Location $repo
try {
  & $dotnet build 'Domain Guardian.Tests/Domain Guardian.Tests.csproj' -c Release --no-restore --nologo 2>&1 | Select-String -Pattern 'error|Build succeeded|Build FAILED|Warning\(s\)|Error\(s\)' | Select-Object -First 10
  Write-Host "  exit: $LASTEXITCODE"
}
finally {
  Pop-Location
}

Step 'Cleanup temporary global.json'
$gj = Join-Path $repo 'global.json'
$backup = "$gj.bak"
if (Test-Path $gj) { Remove-Item -LiteralPath $gj -Force }
if (Test-Path $backup) { Move-Item -LiteralPath $backup -Destination $gj -Force }

# Self-delete
$self = $MyInvocation.MyCommand.Path
if ($self) { Remove-Item -LiteralPath $self -Force -ErrorAction SilentlyContinue }
