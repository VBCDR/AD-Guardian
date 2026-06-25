<#
.SYNOPSIS
Removes the pre-2.0.26 AD Guardian legacy log directory C:\ADCheckLogs (or a
caller-specified path) with parameterised safety rails so the same cleanup
can be invoked identically across dev machines, test-boxes, and CI artifacts.

.DESCRIPTION
AD Guardian v2.0.26+ writes new log files to %ProgramData%\AdHealthMonitor\Logs.
Pre-2.0.26 builds wrote directly to C:\ADCheckLogs -- a path that Defender
Controlled Folder Access, Smart App Control, and third-party AV routinely
lock on modern Windows builds. The current installer removes this dir tree
automatically post-install (v2.0.27+); this script is the reusable recovery
tool for environments that need to clean up before/after an install, or as
part of CI teardown / image reset.

The script detects NTFS junctions and symlinks under the target root and
refuses to follow them unless -Force is supplied -- a Defender-locked target
or a misconfigured junction should not be silently cascade-deleted.

Designed to be non-interactive + machine-parseable:
  * -DryRun: print plan + exit 0, never delete.
  * -WhatIf: standard PS WhatIf on Remove-Item (via SupportsShouldProcess).
  * -Force : bypass the junction safety warning AND the ShouldProcess
    WhatIf/Confirm machinery so the script runs unattended in CI.
  * -Json  : emit a single JSON object (Compressed) instead of human prose.

.PARAMETER Path
Root directory to remove. Default: C:\ADCheckLogs (the historical app path).
Any path works -- pass D:\ADCheckLogs or a redirected location if your
environment mounted the legacy dir elsewhere.

.PARAMETER Force
Bypass junction/symlink safety + WhatIf/Confirm prompts. Required when
running non-interactively (CI).

.PARAMETER DryRun
Inventory the directory and print the plan without deleting any files.
Exit code 0. Combines cleanly with -Json for CI artefacts.

.PARAMETER Json
Emit a single-line Compact JSON object describing what was found and what
would be (or was) deleted. Useful for CI artefacts that get parsed.

.PARAMETER NoRecurse
Only enumerate top-level entries (matches the v2.0.26 installer's
behaviour). Off by default so the byte count is recursive + accurate.

.EXAMPLE
PS> .\scripts\cleanup-adchecklogs.ps1 -DryRun

Inventory C:\ADCheckLogs without deleting.

.EXAMPLE
PS> .\scripts\cleanup-adchecklogs.ps1 -Force -Json

Remove C:\ADCheckLogs without prompts; emit a single-line JSON summary.

.EXAMPLE
PS> .\scripts\cleanup-adchecklogs.ps1 -Path 'C:\ADCheckLogs' -WhatIf

Standard PS WhatIf -- prints the would-be action without touching disk.

.EXAMPLE
PS> .\scripts\cleanup-adchecklogs.ps1 -Path 'D:\SomeSymlink' -Force

Override the junction-safety guard when the admin accepts the cascade risk.

.NOTES
EXIT CODES (suitable for CI pipelines):

  0 = path absent; dry-run completed; or full successful deletion.
  1 = top-level junction/symlink detected AND -Force not supplied;
      OR sub-tree contains reparse-point children AND -Force not supplied;
      OR Remove-Item threw.
  2 = partial deletion -- Remove-Item returned without error but entries
      remain on disk (typical when Defender/CFA is mid-scan on a subfile).
      CI consumers should re-run or escalate to operator review.

The script is intentionally read-only relative to anything outside the
target Path. It never touches the new AD Guardian log dir at
%ProgramData%\AdHealthMonitor\Logs, never edits git refs, and never writes
to %TEMP%. The only side-effects are the (-DryRun / -Json stdout) output
and (on success) the recursive Remove-Item of the target Path.

-ALWAYS PASS ABSOLUTE PATHS. Relative paths to -Path are accepted by
PowerShell (Test-Path / Get-ChildItem resolve them against $PWD) but easy
to mis-target on CI agents whose cwd is not what you expect.

Companion to: installer/AD Guardian Installer.iss CleanupLegacyAdCheckLogs
(section). That installer section runs the same conceptual job automatically
post-install; this script is the operator-side escape hatch when the
installer can't (CI image reset, manual forensics, mixed-version fleet).
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter()]
    [string]$Path = 'C:\ADCheckLogs',

    [Parameter()]
    [switch]$Force,

    [Parameter()]
    [switch]$DryRun,

    [Parameter()]
    [switch]$Json,

    [Parameter()]
    [switch]$NoRecurse
)

$ErrorActionPreference = 'Stop'

# ── Helpers ────────────────────────────────────────────────────────────────────

function Test-IsReparsePoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$LiteralPath
    )

    $item = Get-Item -LiteralPath $LiteralPath -Force -ErrorAction SilentlyContinue
    if (-not $item) { return $false }
    $attrs = $item.Attributes
    if (-not $attrs) { return $false }
    # ReparsePoint = 0x400 in System.IO.FileAttributes. Bitwise check.
    return ([int]($attrs -band [System.IO.FileAttributes]::ReparsePoint)) -ne 0
}

function Get-PathInventory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$LiteralPath,

        [Parameter()]
        [bool]$Recurse = $true
    )

    $fileArgs = @{
        LiteralPath = $LiteralPath
        File      = $true
        Force    = $true
        ErrorAction = 'SilentlyContinue'
    }
    $dirArgs = @{
        LiteralPath = $LiteralPath
        Directory = $true
        Force    = $true
        ErrorAction = 'SilentlyContinue'
    }
    if ($Recurse) {
        $fileArgs['Recurse'] = $true
        $dirArgs['Recurse']  = $true
    }

    $files = @(Get-ChildItem @fileArgs)
    $dirs  = @(Get-ChildItem @dirArgs)
    $bytes = if ($files.Count -gt 0) {
        ($files | Measure-Object -Property Length -Sum).Sum
    } else { 0 }

    $topLevelCount = @(Get-ChildItem -LiteralPath $LiteralPath -Force -ErrorAction SilentlyContinue).Count

    # Count any reparse-point DIRECTORIES inside the tree (not the root, which
    # is already checked separately above by Test-IsReparsePoint). The matcher
    # is bitwise-AND on the System.IO.FileAttributes::ReparsePoint bit (0x400).
    # If the script's caller hasn't supplied -Force, we refuse to descend into
    # a tree that contains sub-junctions because Remove-Item -Recurse would
    # silently cascade-delete their targets outside the legacy log path.
    $reparseDirEnum = @{
        LiteralPath  = $LiteralPath
        Directory    = $true
        Force        = $true
        ErrorAction  = 'SilentlyContinue'
    }
    if ($Recurse) { $reparseDirEnum['Recurse'] = $true }

    $reparseChildren = @(Get-ChildItem @reparseDirEnum |
        Where-Object { $_ -and $_.Attributes -and ([int]($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) -ne 0 })

    return [pscustomobject]@{
        Files            = $files.Count
        Dirs             = $dirs.Count
        Bytes            = [int64]$bytes
        TopLevel         = $topLevelCount
        ReparseChildren  = $reparseChildren.Count
    }
}

function Emit-Result {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Payload,

        [Parameter(Mandatory = $true)]
        [bool]$AsJson
    )

    if ($AsJson) {
        $Payload | ConvertTo-Json -Compress -Depth 5
        return
    }

    $status = $Payload['Status']
    $color = switch ($status) {
        'removed'   { 'Green' }
        'absent'    { 'Yellow' }
        'dry-run'   { 'Cyan' }
        'refused'   { 'Red' }
        'failed'    { 'Red' }
        default     { 'Gray' }
    }

    $summaryLines = @("[$status] $($Payload['Path'])")
    if ($Payload.ContainsKey('Files'))  { $summaryLines += "  Files:    $($Payload['Files'])" }
    if ($Payload.ContainsKey('Dirs'))   { $summaryLines += "  Dirs:     $($Payload['Dirs'])" }
    if ($Payload.ContainsKey('Bytes'))  { $summaryLines += "  Bytes:    $($Payload['Bytes'])" }
    if ($Payload.ContainsKey('TopLevel')) { $summaryLines += "  TopLevel: $($Payload['TopLevel'])" }
    if ($Payload.ContainsKey('Reason'))  { $summaryLines += "  Reason:   $($Payload['Reason'])" }
    if ($Payload.ContainsKey('Error'))   { $summaryLines += "  Error:    $($Payload['Error'])" }

    foreach ($line in $summaryLines) {
        Write-Host $line -ForegroundColor $color
    }
}

# ── Resolution + safety ───────────────────────────────────────────────────────

if (-not (Test-Path -LiteralPath $Path)) {
    Emit-Result -AsJson $Json -Payload @{
        Path   = $Path
        Status = 'absent'
        Files  = 0
        Dirs   = 0
        Bytes  = 0
    }
    exit 0
}

if (Test-IsReparsePoint -LiteralPath $Path) {
    if (-not $Force) {
        Emit-Result -AsJson $Json -Payload @{
            Path   = $Path
            Status = 'refused'
            Reason = 'target is a junction or symlink (use -Force to override)'
        }
        exit 1
    }

    if (-not $Json) {
        Write-Host "WARNING: $Path is a junction / symlink. -Force supplied so cascade-delete proceeds." -ForegroundColor Yellow
    }
}

$inventory = Get-PathInventory -LiteralPath $Path -Recurse (-not $NoRecurse)

# ── Dry-run path ──────────────────────────────────────────────────────────────

if ($DryRun) {
    Emit-Result -AsJson $Json -Payload @{
        Path            = $Path
        Status          = 'dry-run'
        Files           = $inventory.Files
        Dirs            = $inventory.Dirs
        Bytes           = $inventory.Bytes
        TopLevel        = $inventory.TopLevel
        ReparseChildren = $inventory.ReparseChildren
    }
    exit 0
}

# Mid-tree reparse-point check (top-level is already gated above). Cascade-
# delete through junctions would silently kill user data outside the legacy
# log path; the operator must explicitly opt in via -Force.
if ($inventory.ReparseChildren -gt 0 -and -not $Force) {
    Emit-Result -AsJson $Json -Payload @{
        Path            = $Path
        Status          = 'refused'
        Reason          = "tree contains $($inventory.ReparseChildren) junction/symlink sub-directories (use -Force to follow them anyway)"
        ReparseChildren = $inventory.ReparseChildren
        Files           = $inventory.Files
        Dirs            = $inventory.Dirs
        Bytes           = $inventory.Bytes
    }
    exit 1
}

# ── Live removal path ─────────────────────────────────────────────────────────

# SupportsShouldProcess enables the built-in -WhatIf (and -Confirm) on
# Remove-Item via ShouldProcess(). When -Force is supplied at the script
# level, PowerShell automatically suppresses ShouldProcess prompts so the
# removal runs unattended -- which is the CI behaviour this script exists for.
if ($PSCmdlet.ShouldProcess($Path, 'Recursively remove')) {
    try {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop

        # Post-delete verification. Remove-Item in older PS versions coerces
        # share-violation leaves to $false rather than throwing, so the catch
        # above can miss the partial-failure case. Recheck the target path
        # and emit 'partial' (exit 2) if entries remain on disk -- the CI loop
        # can then re-run or escalate.
        if (Test-Path -LiteralPath $Path) {
            $remainingCount = @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue).Count
            Emit-Result -AsJson $Json -Payload @{
                Path      = $Path
                Status    = 'partial'
                Reason    = "Remove-Item returned without error but $remainingCount entries remain (likely Defender or ACL locked). Re-run with -Force or escalate."
                Files     = $inventory.Files
                Dirs      = $inventory.Dirs
                Bytes     = $inventory.Bytes
                Remaining = [int]$remainingCount
            }
            exit 2
        }

        Emit-Result -AsJson $Json -Payload @{
            Path   = $Path
            Status = 'removed'
            Files  = $inventory.Files
            Dirs   = $inventory.Dirs
            Bytes  = $inventory.Bytes
        }
        exit 0
    } catch {
        Emit-Result -AsJson $Json -Payload @{
            Path    = $Path
            Status  = 'failed'
            Error   = $_.Exception.Message
            Files   = $inventory.Files
            Dirs    = $inventory.Dirs
            Bytes   = $inventory.Bytes
        }
        exit 1
    }
}

# ShouldProcess returned false (e.g., WhatIf or Confirm said No) -- no action taken.
Emit-Result -AsJson $Json -Payload @{
    Path   = $Path
    Status = 'skipped'
    Files  = $inventory.Files
    Dirs   = $inventory.Dirs
    Bytes  = $inventory.Bytes
}
exit 0
