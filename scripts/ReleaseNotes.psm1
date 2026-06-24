# ── AD Guardian — natural-tone release notes generator ────────────────────────
#
# Builds a short, friendly release body from commits since the previous tag, in
# the form:
#
#     Here's what's new in v2.0.X.
#
#     Highlights since v2.0.Y:
#     - Subject of commit 1
#     - Subject of commit 2
#     - ...
#
#     Full release notes, downloads, and the installer are available on the GitHub release page.
#
# Falls back to $null when git history can't be read (e.g. shallow clone in CI
# without prior tags) — callers fall back to GitHub's --generate-notes.
#
# The optional -ForcedPreviousTag / -ForcedCommits / -ForceNoPreviousTag
# parameters exist only for the xUnit test harness so commit input can be
# injected without manipulating real git refs. Production callers
# (publish-github-release.ps1) never set them.

function Get-PreviousReleaseTag {
    param([string]$CurrentTag)

    # Sort by -version:refname so lightweight AND annotated tags are ordered
    # correctly by semver. Supported by every modern git (>= 2.7, Nov 2015).
    # A $null result indicates no prior tag exists (e.g. first-ever release) or
    # the repo isn't a git working tree — callers fall back to --generate-notes.
    $tags = & git tag --sort=-version:refname 2>$null |
        Where-Object { $_ -and $_ -ne $CurrentTag }
    return ($tags | Select-Object -First 1)
}

function Build-NaturalReleaseNotes {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CurrentTag,
        [ValidateRange(1, 50)]
        [int]$MaxBullets,
        [string]$ForcedPreviousTag = '',
        [string[]]$ForcedCommits = $null,
        [switch]$ForceNoPreviousTag
    )

    # Test-mode short-circuit: skip the real `git tag` lookup and force the
    # "no previous tag" path so the null/fallback branch can be exercised
    # deterministically without manipulating real git refs.
    if ($ForceNoPreviousTag) {
        return $null
    }

    $previousTag = if ($ForcedPreviousTag) { $ForcedPreviousTag } else { Get-PreviousReleaseTag -CurrentTag $CurrentTag }
    if (-not $previousTag) {
        return $null
    }

    $commits = @()
    if ($null -ne $ForcedCommits) {
        $commits = $ForcedCommits
    } else {
        $range = "$previousTag..HEAD"
        try {
            $output = & git log $range --no-merges --pretty=format:'- %s' 2>$null
            if ($LASTEXITCODE -eq 0 -and $output) {
                $commits = @($output | Where-Object { $_ -and $_.Trim() -ne '' })
            }
        } catch {
            $commits = @()
        }
    }

    if ($commits.Count -eq 0) {
        return $null
    }

    # Trim the "Release vX.Y.Z — bump version and publish distributions" boot
    # commits so they don't dominate the highlights list.
    $filtered = $commits | Where-Object {
        $_ -notmatch '^\s*-\s*Release v\d+\.\d+\.\d+'
    }

    # Use ${name} form to disambiguate from PowerShell scope qualifiers when
    # the variable is followed by punctuation like "{" or ":".
    $previousLabel = if ($previousTag -match '^v') { $previousTag } else { "v$previousTag" }
    $currentLabel = if ($CurrentTag -match '^v') { $CurrentTag } else { "v$CurrentTag" }

    # Maintenance-only release: filtered everything out, only the version-bump
    # commit(s) remain. Emit a short acknowledgement instead of a useless list.
    if ($filtered.Count -eq 0) {
        $lines = @()
        $lines += "Here's what's new in ${currentLabel}."
        $lines += ''
        $lines += "This is a maintenance release with no functional changes since ${previousLabel} — it ships the latest version stamp and rebuilt installer. Run the update to stay in sync."
        $lines += ''
        $lines += 'Full release notes, downloads, and the installer are available on the GitHub release page.'
        return ($lines -join "`n")
    }

    $highlights = $filtered | Select-Object -First $MaxBullets
    $extraCount = $filtered.Count - $highlights.Count

    $lines = @()
    $lines += "Here's what's new in ${currentLabel}."
    $lines += ''
    if ($extraCount -gt 0) {
        $lines += "Highlights since ${previousLabel} (plus $($extraCount) more — see the full commit log):"
    } else {
        $lines += "Highlights since ${previousLabel}:"
    }
    $lines += ''
    foreach ($line in $highlights) {
        # Strip the leading "- " that git log --pretty=format added so we can
        # use proper markdown bullets without double-prefixing.
        $cleanLine = ($line -replace '^- ', '').Trim()
        if ($cleanLine) {
            $lines += "- $cleanLine"
        }
    }
    $lines += ''
    $lines += 'Full release notes, downloads, and the installer are available on the GitHub release page.'

    return ($lines -join "`n")
}

Export-ModuleMember -Function Get-PreviousReleaseTag, Build-NaturalReleaseNotes
