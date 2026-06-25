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
automatically post-install (v2.0.27+) AFTER COPYING any *.log / *.txt /
*.json / *.csv files it finds into a timestamped subfolder of the new log
path; this script is the reusable recovery tool for environments that need
to clean up before/after an install, or as part of CI teardown / image
reset, with the same copy-then-delete behaviour.

The script detects NTFS junctions and symlinks under the target root and
refuses to follow them unless -Force is supplied -- a Defender-locked target
or a misconfigured junction should not be silently cascade-deleted.

Designed to be non-interactive + machine-parseable:
  * -DryRun: print plan + exit 0, never delete (and never copy, if -Migrate).
  * -WhatIf: standard PS WhatIf on Copy-Item + Remove-Item (via SupportsShouldProcess).
  * -Force : bypass the junction safety warning AND the ShouldProcess
    WhatIf/Confirm machinery so the script runs unattended in CI.
  * -Json  : emit a single JSON object (Compressed) instead of human prose.
  * -Migrate : COPY first (per-file to a timestamped subfolder of -DestinationPath),
    THEN delete the legacy tree. Mirrors the installer's CleanupLegacyAdCheckLogs.

.PARAMETER Path
Root directory to remove (and optionally migrate FROM). Default: C:\ADCheckLogs.

.PARAMETER DestinationPath
Root destination directory for the migrated legacy logs. Used only when
-Migrate is supplied; default is %ProgramData%\AdHealthMonitor\Logs (the
run-time AD Guardian log root). The actual write target becomes
"$DestinationPath\legacy-import-yyyyMMdd-HHmmss\...".

.PARAMETER Force
Bypass junction/symlink safety + WhatIf/Confirm prompts. Required when
running non-interactively (CI).

.PARAMETER DryRun
Inventory the directory and print the plan without copying or deleting any
files. Exit code 0. Combines cleanly with -Json for CI artefacts.

.PARAMETER Json
Emit a single-line Compact JSON object describing what was found and what
would be (or was) copied/deleted. Useful for CI artefacts that get parsed.

.PARAMETER NoRecurse
Only enumerate top-level entries (matches the v2.0.26 installer's
behaviour). Off by default so the byte count is recursive + accurate.

.PARAMETER Migrate
When supplied: copy every *.log / *.txt / *.json / *.csv file under -Path
into a timestamped subfolder of -DestinationPath BEFORE deleting the
legacy tree. Mirrors the installer's CleanupLegacyAdCheckLogs flow exactly.

.EXAMPLE
PS> .\scripts\cleanup-adchecklogs.ps1 -DryRun

Inventory C:\ADCheckLogs without deleting.

.EXAMPLE
PS> .\scripts\cleanup-adchecklogs.ps1 -Migrate -DryRun -Json

Inventory C:\ADCheckLogs AND show the copy plan as JSON; never touches disk.

.EXAMPLE
PS> .\scripts\cleanup-adchecklogs.ps1 -Migrate -Force -Json

Migrate C:\ADCheckLogs -> %ProgramData%\AdHealthMonitor\Logs\legacy-import-...,
then delete C:\ADCheckLogs. Single-line JSON summary.

.EXAMPLE
PS> .\scripts\cleanup-adchecklogs.ps1 -Path 'D:\ADCheckLogs' -Migrate -DestinationPath 'E:\adg-logs' -Force

Custom source + custom destination. Useful when the legacy dir was on a
non-system drive or has been redirected by admins.

.EXAMPLE
PS> .\scripts\cleanup-adchecklogs.ps1 -Migrate -WhatIf

PowerShell-native WhatIf -- prints the proposed copy + delete without
touching disk.

.EXAMPLE
PS> .\scripts\cleanup-adchecklogs.ps1 -Path 'D:\SomeSymlink' -Force

Override the junction-safety guard when the admin accepts the cascade risk.
Migration (if -Migrate) ALSO uses -Force-respect for junction skip so
reparses never cascade through the walker.

.NOTES
EXIT CODES (suitable for CI pipelines):

  0 = path absent; dry-run completed; or full successful delete.
  1 = top-level junction/symlink detected AND -Force not supplied;
      OR sub-tree contains reparse-point children AND -Force not supplied;
      OR Remove-Item threw;
      OR the migration destination root could not be created (data
      preservation contract: NEVER delete when the copy destination is
      not creatable -- the caller should investigate the path / ACL).
  2 = partial deletion -- Remove-Item returned without error but entries
      remain on disk (typical when Defender/CFA is mid-scan on a subfile).
      CI consumers should re-run or escalate to operator review.

Writes only into:
  * -DestinationPath (only when -Migrate) -- creates a timestamped subfolder
  * stdout (when -Json is set, emits the JSON summary)
  * It's read-only everywhere else.

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
    [string]$DestinationPath = (Join-Path -Path ([System.Environment]::GetFolderPath('CommonApplicationData')) -ChildPath 'AdHealthMonitor\Logs'),

    [Parameter()]
    [switch]$Force,

    [Parameter()]
    [switch]$DryRun,

    [Parameter()]
    [switch]$Json,

    [Parameter()]
    [switch]$NoRecurse,

    [Parameter()]
    [switch]$Migrate
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
    if ($Payload.ContainsKey('Files'))         { $summaryLines += "  Files:             $($Payload['Files'])" }
    if ($Payload.ContainsKey('Dirs'))          { $summaryLines += "  Dirs:              $($Payload['Dirs'])" }
    if ($Payload.ContainsKey('Bytes'))         { $summaryLines += "  Bytes:             $($Payload['Bytes'])" }
    if ($Payload.ContainsKey('TopLevel'))      { $summaryLines += "  TopLevel:          $($Payload['TopLevel'])" }
    if ($Payload.ContainsKey('DestinationRoot')) { $summaryLines += "  DestinationRoot:   $($Payload['DestinationRoot'])" }
    if ($Payload.ContainsKey('Copied'))        { $summaryLines += "  Copied:            $($Payload['Copied'])" }
    if ($Payload.ContainsKey('Collisions'))    { $summaryLines += "  Collisions:        $($Payload['Collisions'])" }
    if ($Payload.ContainsKey('CopyErrors'))    { $summaryLines += "  CopyErrors:        $($Payload['CopyErrors'])" }
    if ($Payload.ContainsKey('Reason'))        { $summaryLines += "  Reason:            $($Payload['Reason'])" }
    if ($Payload.ContainsKey('Error'))         { $summaryLines += "  Error:             $($Payload['Error'])" }

    foreach ($line in $summaryLines) {
        Write-Host $line -ForegroundColor $color
    }
}

# Copy-Tree mirrors the Pascal-side MigrateLegacyLogsTo in installer/AD Guardian
# Installer.iss: a stack-based DFS that filters by extension, skips NTFS
# junctions/symlinks, and renames per-file collisions with a timestamp
# suffix. Each individual Copy-Item is best-effort -- failures are counted
# rather than aborting the migration, matching the installer's policy.
function Copy-Tree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceRoot,

        [Parameter(Mandatory = $true)]
        [string]$DestinationRoot,

        [Parameter(Mandatory = $true)]
        [string]$TimeStamp
    )

    $allowedExts = @('.log', '.txt', '.json', '.csv')
    $stack = [System.Collections.Stack]::new()
    $stack.Push($SourceRoot)
    $entriesMigrated = 0
    $entriesCollisions = 0
    $entriesErrors = 0

    while ($stack.Count -gt 0) {
        $currentDir = $stack.Pop()
        $children = @(Get-ChildItem -LiteralPath $currentDir -Force -ErrorAction SilentlyContinue)

        foreach ($child in $children) {
            if ($child.Name -in '.', '..') { continue }

            # Skip junctions/symlinks -- they're never followed.
            if (Test-IsReparsePoint -LiteralPath $child.FullName) { continue }

            if ($child.PSIsContainer) {
                $stack.Push($child.FullName)
                continue
            }

            $ext = [System.IO.Path]::GetExtension($child.Name).ToLowerInvariant()
            if (-not $allowedExts.Contains($ext)) { continue }

            # Relative path under SourceRoot -> destination under DestinationRoot.
            $relPath = $child.FullName.Substring($SourceRoot.Length).TrimStart('\', '/')
            if ([string]::IsNullOrEmpty($relPath)) { $relPath = $child.Name }
            $destPath = Join-Path -Path $DestinationRoot -ChildPath $relPath
            $destParent = Split-Path -Parent $destPath

            try {
                if (-not (Test-Path -LiteralPath $destParent)) {
                    # IMPORTANT: PS 5.1's New-Item does NOT expose a -LiteralPath
                    # parameter (unlike Set-Content/Get-ChildItem/Test-Path/Remove-Item
                    # which all have it). Use [System.IO.Directory]::CreateDirectory
                    # which is also Pascal-equivalent (`ForceDirectories`): creates
                    # intermediate dirs as needed, no wildcard interpretation, no
                    # PowerShell parameter binding required. Wrapped in ShouldProcess
                    # so -WhatIf still suppresses the actual mkdir.
                    if ($PSCmdlet.ShouldProcess($destParent, "Create destination directory")) {
                        [System.IO.Directory]::CreateDirectory($destParent) | Out-Null
                    }
                }

                # IMPORTANT: Copy-Item -Force silently overwrites an existing
                # destination file. That's the wrong default for a migration --
                # we'd silently clobber a previous migration's content at the
                # same relative path. Use [System.IO.File]::Copy with
                # overwrite=$false so a pre-existing target raises IOException
                # and we fall into the collision-rename catch path below.
                #
                # Note: [System.IO.File]::Copy does not honour ShouldProcess
                # on its own. We wrap it in an explicit $PSCmdlet.ShouldProcess
                # so -WhatIf flows through cleanly (printed would-copy message,
                # no actual disk write). -Force at the script level suppresses
                # the ShouldProcess prompt so CI runs are silent.
                if ($PSCmdlet.ShouldProcess($destPath, "Copy legacy log to")) {
                    [System.IO.File]::Copy($child.FullName, $destPath, $false)
                    $entriesMigrated++
                }
            } catch {
                # Direct copy failed -- most likely a name collision
                # (IOException: file exists) or an external IO error
                # (Defender mid-scan on parent dir, ACL on dest, etc.).
                # Try a collision-rename BEFORE counting as a generic error,
                # because the timestamp suffix makes the renamed target
                # unique per-migration and effectively free of collisions.
                $stem = [System.IO.Path]::GetFileNameWithoutExtension($child.Name)
                $collidedPath = Join-Path -Path $destParent -ChildPath ($stem + '-' + $TimeStamp + $ext)
                try {
                    if ($PSCmdlet.ShouldProcess($collidedPath, "Copy legacy log to (collision-rename)")) {
                        [System.IO.File]::Copy($child.FullName, $collidedPath, $false)
                        $entriesMigrated++
                        $entriesCollisions++
                    }
                } catch {
                    $entriesErrors++
                    if (-not $Json) {
                        Write-Host ("Migration: WARNING could not copy {0} -> {1} ({2})" -f $child.FullName, $collidedPath, $_.Exception.Message) -ForegroundColor Yellow
                    }
                }
            }
        }
    }

    return [pscustomobject]@{
        EntriesMigrated = $entriesMigrated
        EntriesCollisions = $entriesCollisions
        CopyErrors = $entriesErrors
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
# For -DryRun -Migrate, we emit a dry-run summary including the would-be
# destination root so CI consumers can preview without writing.

if ($DryRun) {
    $dryPayload = @{
        Path            = $Path
        Status          = 'dry-run'
        Files           = $inventory.Files
        Dirs            = $inventory.Dirs
        Bytes           = $inventory.Bytes
        TopLevel        = $inventory.TopLevel
        ReparseChildren = $inventory.ReparseChildren
    }
    if ($Migrate) {
        $dryPayload['DestinationRoot'] = Join-Path -Path $DestinationPath -ChildPath ('legacy-import-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        $dryPayload['Migrate']         = $true
    }
    Emit-Result -AsJson $Json -Payload $dryPayload
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

# ── Migration (pre-delete, when -Migrate) ─────────────────────────────────────
# Strict order: copy FIRST, then delete. If the migration destination folder
# cannot be created, ABORT the entire cleanup -- we never silently lose data.

$migrationSummary = $null
$migratedRoot = ''
if ($Migrate) {
    $timeStamp = (Get-Date -Format 'yyyyMMdd-HHmmss')
    $migratedRoot = Join-Path -Path $DestinationPath -ChildPath ('legacy-import-' + $timeStamp)

    try {
        if (-not (Test-Path -LiteralPath $migratedRoot)) {
            # PS 5.1 New-Item has no -LiteralPath parameter; use the .NET
            # equivalent which is also honestly equivalent to Pascal's
            # ForceDirectories: creates intermediate dirs idempotently.
            # SupportsShouldProcess flows through here too (the script-level
            # [CmdletBinding(SupportsShouldProcess)] is in effect), so -WhatIf
            # will surface a would-create message and skip the mkdir.
            if ($PSCmdlet.ShouldProcess($migratedRoot, "Create migration root")) {
                [System.IO.Directory]::CreateDirectory($migratedRoot) | Out-Null
            }
        }
        $migrationSummary = Copy-Tree -SourceRoot $Path -DestinationRoot $migratedRoot -TimeStamp $timeStamp

        if (-not $Json) {
            Write-Host ("Migration: copied {0} file(s) to {1} ({2} collisions, {3} errors)" -f `
                $migrationSummary.EntriesMigrated, $migratedRoot, `
                $migrationSummary.EntriesCollisions, $migrationSummary.CopyErrors) -ForegroundColor Cyan
        }
    } catch {
        # Destination folder could not be created (ACL / disk / etc.). Refuse
        # to delete the legacy tree -- the user explicitly wants the data
        # preserved. Emit a 'failed' result the operator can act on.
        Emit-Result -AsJson $Json -Payload @{
            Path       = $Path
            Status     = 'failed'
            Error      = "Migration destination folder could not be created at $migratedRoot ($($_.Exception.Message)); legacy directory retained."
            Files      = $inventory.Files
            Dirs       = $inventory.Dirs
            Bytes      = $inventory.Bytes
        }
        exit 1
    }
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
            $postPayload = @{
                Path      = $Path
                Status    = 'partial'
                Reason    = "Remove-Item returned without error but $remainingCount entries remain (likely Defender or ACL locked). Re-run with -Force or escalate."
                Files     = $inventory.Files
                Dirs      = $inventory.Dirs
                Bytes     = $inventory.Bytes
                Remaining = [int]$remainingCount
            }
            if ($Migrate) {
                $postPayload['DestinationRoot'] = $migratedRoot
                $postPayload['Copied']          = $migrationSummary.EntriesMigrated
                $postPayload['Collisions']      = $migrationSummary.EntriesCollisions
                $postPayload['CopyErrors']      = $migrationSummary.CopyErrors
            }
            Emit-Result -AsJson $Json -Payload $postPayload
            exit 2
        }

        # Full success: status distinguishes clean-migration (removed + copied
        # > 0) from clean-deletion-of-empty-legacy (removed + copied = 0).
        $removedPayload = @{
            Path   = $Path
            Status = 'removed'
            Files  = $inventory.Files
            Dirs   = $inventory.Dirs
            Bytes  = $inventory.Bytes
        }
        if ($Migrate) {
            $removedPayload['DestinationRoot'] = $migratedRoot
            $removedPayload['Copied']          = $migrationSummary.EntriesMigrated
            $removedPayload['Collisions']      = $migrationSummary.EntriesCollisions
            $removedPayload['CopyErrors']      = $migrationSummary.CopyErrors
        }
        Emit-Result -AsJson $Json -Payload $removedPayload
        exit 0
    } catch {
        $failedPayload = @{
            Path    = $Path
            Status  = 'failed'
            Error   = $_.Exception.Message
            Files   = $inventory.Files
            Dirs    = $inventory.Dirs
            Bytes   = $inventory.Bytes
        }
        if ($Migrate) {
            $failedPayload['DestinationRoot'] = $migratedRoot
            $failedPayload['Copied']          = $migrationSummary.EntriesMigrated
            $failedPayload['Collisions']      = $migrationSummary.EntriesCollisions
            $failedPayload['CopyErrors']      = $migrationSummary.CopyErrors
        }
        Emit-Result -AsJson $Json -Payload $failedPayload
        exit 1
    }
}

# ShouldProcess returned false (e.g., WhatIf or Confirm said No) -- no action taken.
$skippedPayload = @{
    Path   = $Path
    Status = 'skipped'
    Files  = $inventory.Files
    Dirs   = $inventory.Dirs
    Bytes  = $inventory.Bytes
}
if ($Migrate) {
    $skippedPayload['DestinationRoot'] = $migratedRoot
    $skippedPayload['Copied']          = if ($migrationSummary) { $migrationSummary.EntriesMigrated } else { 0 }
    $skippedPayload['Collisions']      = if ($migrationSummary) { $migrationSummary.EntriesCollisions } else { 0 }
    $skippedPayload['CopyErrors']      = if ($migrationSummary) { $migrationSummary.CopyErrors } else { 0 }
}
Emit-Result -AsJson $Json -Payload $skippedPayload
exit 0
