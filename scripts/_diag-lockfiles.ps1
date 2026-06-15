$ErrorActionPreference = 'Continue'
$repo = 'C:\Users\crogers\AD-Guardian'
$git = 'C:\Program Files\Git\bin\git.exe'

function Step($m) {
  Write-Host ''
  Write-Host "=== $m ===" -ForegroundColor Cyan
}

Step 'Fetch origin (fix stale push)'
& $git -C $repo fetch origin 2>&1 | Out-Null
& $git -C $repo log origin/master --oneline -3

Step 'Working tree status'
& $git -C $repo status --short

Step 'List lock files as git sees them (porcelain format)'
# Use git status to see exact paths
& $git -C $repo status --porcelain 2>&1

Step 'Find the actual lock file paths on disk'
$locks = @(Get-ChildItem -Path $repo -Recurse -Filter packages.lock.json -ErrorAction SilentlyContinue)
foreach ($l in $locks) {
  $full = $l.FullName
  $rel = $l.FullName.Substring($repo.Length).TrimStart('\', '/')
  Write-Host "  full: $full"
  Write-Host "  rel:  $rel"
  # Show the git-tracked path
  $gitPath = & $git -C $repo ls-files --error-unmatch $rel 2>&1
  Write-Host "  git:  $gitPath"
}

Step 'Try staging with -A (add all modifications + new files)'
Push-Location $repo
try {
  & $git add -A 2>&1
  & $git status --short
}
finally {
  Pop-Location
}

# Self-delete
$self = $MyInvocation.MyCommand.Path
if ($self) { Remove-Item -LiteralPath $self -Force -ErrorAction SilentlyContinue }
