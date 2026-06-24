using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Domain_Guardian.Tests;

/// <summary>
/// Tests for <c>scripts/ReleaseNotes.psm1::Build-NaturalReleaseNotes</c> — the
/// natural-tone release notes generator that backs
/// <c>scripts/publish-github-release.ps1</c>.
///
/// Each test spawns a fresh <c>pwsh.exe</c> subprocess that imports the production
/// <c>ReleaseNotes.psm1</c> directly and calls <c>Build-NaturalReleaseNotes</c>
/// with the supplied parameters, emitting a single-line JSON result on stdout:
/// <c>{ "HasBody": bool, "Body": string|null }</c>.
///
/// Why subprocess (rather than in-process <c>PowerShell.Create()</c>):
///   - Avoids the <c>Microsoft.PowerShell.SDK 7.6.x</c> dependency that does not
///     cleanly resolve into <c>net9.0-windows10.0.17763.0 + UseWPF=true</c> builds.
///   - Tests against the EXACT pwsh.exe runtime that ships on developer machines
///     and CI runners; no risk of the test path diverging from production.
///
/// Why inline -Command (rather than -File path/to/harness.ps1):
///   - Earlier iterations shipped a separate <c>Invoke-ReleaseNotes.ps1</c>
///     harness and invoked it via <c>pwsh -File</c>. Three separate attempts
///     failed with "The argument ... is not recognized as the name of a script
///     file" even after the harness file was verifiably on disk with current
///     timestamps — likely a combination of Windows CreateProcess argv
///     re-tokenization, PowerShell module-load timing, and execution-policy
///     caching. Inlining the script as a single <c>-Command</c> argv entry
///     sidesteps all of these: the script travels through the same single
///     argv slot, no filesystem race, no policy lookup.
///
/// Why ForcedCommits via env var (not argv):
///   - Real commit subjects start with <c>- </c> (e.g. "- Fix UI bug"). When
///     passed via argv the Windows command-line parser re-tokenizes on
///     spaces and PowerShell's binder treats subsequent <c>-</c>-prefixed tokens
///     as new parameter names. <see cref="InvokeBuildNotes"/> packs the array
///     as JSON in the <c>RELEASENOTES_FORCEDCOMMITS</c> env var, then the
///     inline script does <c>ConvertFrom-Json</c> on it.
///
/// Tagged <c>[Trait("Category", "Slow")]</c>: local devs can run
/// <c>dotnet test --filter "Category!=Slow"</c> to skip the ~15-20 s pwsh.exe
/// startup tax during tight inner-loop work. CI runs everything.
/// </summary>
[Trait("Category", "Slow")]
public class ReleaseNotesTests
{
    private static readonly Lazy<string> PwshPath = new(() => ResolvePwshPath());
    private static readonly Lazy<string> ModulePath = new(() => ResolveModulePath());

    private static string ResolveModulePath()
    {
        // The test csproj uses <None Include="..\scripts\ReleaseNotes.psm1"
        // CopyToOutputDirectory="PreserveNewest"/>. .NET SDK currently flattens
        // the "..\scripts" relative Include onto the output root — verified
        // by diagnostic check on master. A future csproj tweak that adds a
        // <SubType> tag or a <Link> could change the subpath, so we also try
        // the scripts\ subdirectory as a defensive fallback before giving up.
        string root = AppContext.BaseDirectory;
        string flat      = Path.Combine(root, "ReleaseNotes.psm1");
        string withSub   = Path.Combine(root, "scripts", "ReleaseNotes.psm1");
        if (File.Exists(flat))    return flat;
        if (File.Exists(withSub)) return withSub;
        throw new FileNotFoundException(
            "ReleaseNotes.psm1 was not found at '" + flat +
            "' or '" + withSub + "'. Check the test csproj <None Include> entry.");
    }

    /// <summary>
    /// Pwsh.exe candidate search. Avoids "The system cannot find the file
    /// specified" errors on dev machines where pwsh.exe is not on PATH
    /// (Windows PowerShell 5.1 uses powershell.exe — distinct binary).
    /// Only explicit File.Exists-checked candidates are returned.
    /// </summary>
    private static string ResolvePwshPath()
    {
        string[] candidates =
        {
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            @"C:\Program Files (x86)\PowerShell\7\pwsh.exe",
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
            ResolveFromPath("pwsh.exe") ?? string.Empty,
        };

        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "PowerShell 7 (pwsh.exe) was not found in any standard location or on PATH. " +
            "Install PowerShell 7 from https://aka.ms/powershell, or skip Slow tests with --filter \"Category!=Slow\".");
    }

    private static string? ResolveFromPath(string exeName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }
        foreach (string dir in pathEnv.Split(
            Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir.Trim().Trim('"'), exeName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Builds the inline PowerShell script that imports the prod module and
    /// calls <c>Build-NaturalReleaseNotes</c>. The script body is fully literal
    /// (no C# string.Format escapes needed) — only small placeholder tokens
    /// (MODULEPATH, CT, MB, FPT, FNP) are substituted via simple string replace.
    /// Using string.Format with <c>{{</c>/<c>}}</c> embeds was error-prone
    /// (any future <c>$x</c> interpolation in the PS script would silently
    /// collide with C# format markers).
    /// </summary>
    private static string BuildInlineScript(
        string currentTag, int maxBullets,
        string? forcedPreviousTag, bool forceNoPreviousTag)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            Import-Module '##MODULEPATH##' -Force
            $json = $env:RELEASENOTES_FORCEDCOMMITS
            $p = @{
                CurrentTag        = '##CT##'
                MaxBullets        = ##MB##
                ForcedPreviousTag = '##FPT##'
            }
            if (##FNP##) { $p.ForceNoPreviousTag = $true }
            if ($json) {
                $raw = $json | ConvertFrom-Json
                $p.ForcedCommits = @($raw | ForEach-Object { [string]$_ })
            }
            $result = Build-NaturalReleaseNotes @p
            ConvertTo-Json -InputObject @{
                HasBody = ($null -ne $result)
                Body    = if ($null -eq $result) { $null } else { [string]$result }
            } -Compress
            """;
        return script
            .Replace("##MODULEPATH##", EscapePsSingleQuoted(ModulePath.Value))
            .Replace("##CT##",        EscapePsSingleQuoted(currentTag))
            .Replace("##MB##",        maxBullets.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("##FPT##",       EscapePsSingleQuoted(forcedPreviousTag ?? string.Empty))
            .Replace("##FNP##",       forceNoPreviousTag ? "$true" : "$false");
    }

    /// <summary>
    /// PowerShell allows embedded single quotes inside a single-quoted string
    /// only by doubling them: <c>'</c> becomes <c>''</c>. This keeps user data
    /// (commit subjects, tags) safe inside the inline PowerShell script.
    /// </summary>
    private static string EscapePsSingleQuoted(string value) =>
        value.Replace("'", "''");

    /// <summary>
    /// Spawns pwsh.exe with the inline script + JSON env var and parses the
    /// single-line JSON result.
    /// </summary>
    private static (bool HasBody, string? Body) InvokeBuildNotes(
        string currentTag,
        int maxBullets = 8,
        string? forcedPreviousTag = null,
        string[]? forcedCommits = null,
        bool forceNoPreviousTag = false)
    {
        var psi = new ProcessStartInfo(PwshPath.Value)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // IMPORTANT: pwsh.exe emits UTF-8 on stdout/stderr by default. .NET's
            // ProcessStartInfo defaults StandardOutputEncoding to the system OEM
            // code page (e.g. CP437), which silently mangles the em-dash and
            // other non-ASCII characters from our release-notes body. Without
            // this, JsonDocument.Parse returns a body with the dashes garbled
            // (`—` becomes `â€"` or similar) and Assert.Contains for the
            // highlights header line fails on the plus-N-more branch.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding  = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        string script = BuildInlineScript(currentTag, maxBullets, forcedPreviousTag, forceNoPreviousTag);

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        // -Command takes the entire script as one argv slot. ArgumentList
        // quotes it correctly on Windows regardless of embedded whitespace,
        // newlines, $-signs, or braces.
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        if (forcedCommits != null)
        {
            psi.Environment["RELEASENOTES_FORCEDCOMMITS"] =
                JsonSerializer.Serialize(forcedCommits);
        }

        // Read BOTH streams into locals before WaitForExit so neither buffer can
        // deadlock when the process writes to one stream while we're draining
        // the other. (Standard pattern; see .NET docs on Process.StandardOutput/
        // StandardError deadlock notes.)
        using Process proc = Process.Start(psi)!;
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "pwsh.exe failed (exit " + proc.ExitCode + ") for CurrentTag='" +
                currentTag + "'\nSTDOUT:\n" + stdout + "\nSTDERR:\n" + stderr);
        }

        string trimmed = stdout.Trim();
        using JsonDocument doc = JsonDocument.Parse(trimmed);
        JsonElement root = doc.RootElement;
        bool hasBody = root.GetProperty("HasBody").GetBoolean();
        string? body = hasBody ? root.GetProperty("Body").GetString() : null;
        return (hasBody, body);
    }

    /// <summary>
    /// Runs pwsh.exe expecting validation failure (non-zero exit) because
    /// <paramref name="maxBullets"/> is outside [1,50]. Asserts both stdout
    /// and stderr mention "MaxBullets" — the harness surface validation error
    /// uses that name.
    /// </summary>
    private static (int ExitCode, string Stdout, string Stderr) InvokeExpectingFailure(
        string currentTag, int maxBullets,
        string? forcedPreviousTag = null,
        string[]? forcedCommits = null)
    {
        var psi = new ProcessStartInfo(PwshPath.Value)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Match InvokeBuildNotes: pwsh.exe emits UTF-8; force UTF-8 decoding
            // so validation-failure error messages containing non-ASCII chars
            // round-trip cleanly.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding  = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        string script = BuildInlineScript(currentTag, maxBullets, forcedPreviousTag, false);

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        if (forcedCommits != null)
        {
            psi.Environment["RELEASENOTES_FORCEDCOMMITS"] =
                JsonSerializer.Serialize(forcedCommits);
        }

        using Process proc = Process.Start(psi)!;
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, stdoutTask.GetAwaiter().GetResult(),
                          stderrTask.GetAwaiter().GetResult());
    }

    // ─────────────────────────────────────────────────────────────────────
    // Highlights path — real functional commits exist between prev tag and HEAD
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildNotes_SingleFunctionalCommit_ProducesHighlightsBody()
    {
        (bool hasBody, string? body) = InvokeBuildNotes(
            currentTag: "v2.0.24",
            forcedPreviousTag: "v2.0.23",
            forcedCommits: new[] { "- Fix UI bugs: progress bar flash" });

        Assert.True(hasBody);
        Assert.NotNull(body);

        Assert.Contains("Here's what's new in v2.0.24.", body);
        Assert.Contains("Highlights since v2.0.23:", body);
        Assert.Contains("- Fix UI bugs: progress bar flash", body);
        Assert.Contains("Full release notes, downloads, and the installer are available on the GitHub release page.", body);
    }

    [Fact]
    public void BuildNotes_MultipleFunctionalCommits_AllAppearAsBullets()
    {
        (bool hasBody, string? body) = InvokeBuildNotes(
            currentTag: "v2.0.24",
            forcedPreviousTag: "v2.0.23",
            forcedCommits: new[]
            {
                "- Fix UI bug A",
                "- Add feature B",
                "- Improve performance C",
            });

        Assert.True(hasBody);
        Assert.NotNull(body);
        Assert.Contains("Highlights since v2.0.23:", body);
        Assert.Contains("- Fix UI bug A", body);
        Assert.Contains("- Add feature B", body);
        Assert.Contains("- Improve performance C", body);
    }

    [Fact]
    public void BuildNotes_BootCommitsMixedWithFunctional_FiltersBootCommits()
    {
        (bool hasBody, string? body) = InvokeBuildNotes(
            currentTag: "v2.0.24",
            forcedPreviousTag: "v2.0.23",
            forcedCommits: new[]
            {
                "- Release v2.0.23 — bump version and publish distributions",
                "- Real functional change",
                "- Release v2.0.22 — bump version and publish distributions",
            });

        Assert.True(hasBody);
        Assert.NotNull(body);

        // Boot-commit subjects must be filtered out
        Assert.DoesNotContain("Release v2.0.23 — bump version", body);
        Assert.DoesNotContain("Release v2.0.22 — bump version", body);
        // The non-boot commit must be preserved
        Assert.Contains("- Real functional change", body);
    }

    [Theory]
    [InlineData(9, 4, 5)]    // 9 functional commits, MaxBullets=4 → "plus 5 more"
    [InlineData(20, 8, 12)]  // 20 functional commits, MaxBullets=8 → "plus 12 more"
    [InlineData(2, 8, 0)]    // 2 functional commits, MaxBullets=8 → no "plus" line
    public void BuildNotes_CommitsExceedMaxBullets_IncludesPlusNMoreLineOrPlainHeader(
        int commitCount, int maxBullets, int expectedExtra)
    {
        string[] commits = new string[commitCount];
        for (int i = 0; i < commitCount; i++)
        {
            commits[i] = "- Functional change " + (i + 1);
        }

        (bool hasBody, string? body) = InvokeBuildNotes(
            currentTag: "v2.0.24", maxBullets: maxBullets,
            forcedPreviousTag: "v2.0.23", forcedCommits: commits);

        Assert.True(hasBody);
        Assert.NotNull(body);

        if (expectedExtra > 0)
        {
            Assert.Contains(
                "Highlights since v2.0.23 (plus " + expectedExtra + " more \u2014 see the full commit log):",
                body);
            // Must NOT use the plain "Highlights since X:" header in this branch
            Assert.DoesNotContain("Highlights since v2.0.23:", body);

            // Only the first N bullets should appear
            for (int i = 1; i <= maxBullets; i++)
            {
                Assert.Contains("- Functional change " + i, body);
            }
            // The trimmed extras must NOT appear
            Assert.DoesNotContain("- Functional change " + (maxBullets + 1), body);
        }
        else
        {
            Assert.Contains("Highlights since v2.0.23:", body);
            Assert.DoesNotContain("more — see the full commit log", body);
            for (int i = 1; i <= commitCount; i++)
            {
                Assert.Contains("- Functional change " + i, body);
            }
        }
    }

    [Fact]
    public void BuildNotes_TagWithoutVPrefix_AddsVPrefix()
    {
        // ForcedPreviousTag is supplied without the leading "v" — the helper
        // should still produce a clean "v1.0.0" label in the Highlights header.
        (bool hasBody, string? body) = InvokeBuildNotes(
            currentTag: "v1.1.0",
            forcedPreviousTag: "1.0.0",
            forcedCommits: new[] { "- Some change" });

        Assert.True(hasBody);
        Assert.NotNull(body);
        Assert.Contains("Here's what's new in v1.1.0.", body);
        Assert.Contains("Highlights since v1.0.0:", body);
    }

    [Fact]
    public void BuildNotes_CommitMessageContainingApostrophe_RoundTripsCleanly()
    {
        // Confirm the env-var/JSON round-trip doesn't mangle commit messages
        // containing shell-sensitive characters.
        (bool hasBody, string? body) = InvokeBuildNotes(
            currentTag: "v2.0.24",
            forcedPreviousTag: "v2.0.23",
            forcedCommits: new[]
            {
                "- Fix parser's handling of empty input",
                "- Don't double-escape the user's name",
            });

        Assert.True(hasBody);
        Assert.NotNull(body);
        Assert.Contains("- Fix parser's handling of empty input", body);
        Assert.Contains("- Don't double-escape the user's name", body);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Maintenance-only path — every commit is a "Release vX.Y.Z" boot commit
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildNotes_OnlyBootCommits_ProducesMaintenanceFriendlyMessage()
    {
        (bool hasBody, string? body) = InvokeBuildNotes(
            currentTag: "v2.0.24",
            forcedPreviousTag: "v2.0.23",
            forcedCommits: new[]
            {
                "- Release v2.0.24 — bump version and publish distributions",
            });

        Assert.True(hasBody);
        Assert.NotNull(body);

        // Robust substring asserts (not the full friendly sentence — that lets
        // copy edits land without rewriting tests, while still verifying the
        // maintenance branch fires).
        Assert.Contains("Here's what's new in v2.0.24.", body);
        Assert.Contains("maintenance release", body);
        Assert.Contains("no functional changes since v2.0.23", body);
        Assert.Contains("Run the update", body);
        Assert.Contains("Full release notes, downloads, and the installer are available on the GitHub release page.", body);

        // Maintenance body must NOT contain the highlights section at all
        Assert.DoesNotContain("Highlights since", body);
        // Sniff guard: the boot-commit subject (which we filtered out) must NOT
        // appear in the maintenance body. Protects against an inverted regex
        // accidentally letting boot commits through the maintenance branch.
        Assert.DoesNotContain("bump version", body);
    }

    [Fact]
    public void BuildNotes_MultipleBootCommits_StillMaintenance()
    {
        // Multiple boot commits is the realistic post-replace-existing-release case
        (bool hasBody, string? body) = InvokeBuildNotes(
            currentTag: "v2.0.24",
            forcedPreviousTag: "v2.0.23",
            forcedCommits: new[]
            {
                "- Release v2.0.24 — bump version and publish distributions",
                "- Release v2.0.22 — bump version and publish distributions",
            });

        Assert.True(hasBody);
        Assert.NotNull(body);
        Assert.Contains("maintenance release", body);
        Assert.Contains("no functional changes since v2.0.23", body);
        Assert.DoesNotContain("Highlights since", body);
    }

    [Fact]
    public void BuildNotes_AnyFunctionalCommitTakesPrecedenceOverMaintenance()
    {
        // 2 boot commits + 1 functional → not maintenance, falls into highlights path with 1 item
        (bool hasBody, string? body) = InvokeBuildNotes(
            currentTag: "v2.0.24",
            forcedPreviousTag: "v2.0.23",
            forcedCommits: new[]
            {
                "- Release v2.0.24 — bump version and publish distributions",
                "- Real functional change",
                "- Release v2.0.22 — bump version and publish distributions",
            });

        Assert.True(hasBody);
        Assert.NotNull(body);
        Assert.Contains("Highlights since v2.0.23:", body);
        Assert.Contains("- Real functional change", body);
        Assert.DoesNotContain("maintenance release", body);
    }

    // ─────────────────────────────────────────────────────────────────────
    // No-prior-tag fallback — returns $null so caller falls back to --generate-notes
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildNotes_ForceNoPreviousTag_ReturnsNull()
    {
        (bool hasBody, string? body) = InvokeBuildNotes(
            currentTag: "v2.0.24",
            forceNoPreviousTag: true);

        Assert.False(hasBody);
        Assert.Null(body);
    }

    [Fact]
    public void BuildNotes_NoPreviousTagIgnoresForcedCommits_ReturnsNull()
    {
        // Even when commits are provided, the no-prior-tag path fires first
        // (precedence: ForceNoPreviousTag → return $null).
        (bool hasBody, string? body) = InvokeBuildNotes(
            currentTag: "v2.0.24",
            forcedCommits: new[] { "- Real functional change" },
            forceNoPreviousTag: true);

        Assert.False(hasBody);
        Assert.Null(body);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Input validation — MaxBullets boundary per [ValidateRange(1, 50)]
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildNotes_MaxBulletsZero_FailsValidation()
    {
        (int exit, string stdout, string stderr) = InvokeExpectingFailure(
            currentTag: "v2.0.24", maxBullets: 0,
            forcedPreviousTag: "v2.0.23",
            forcedCommits: new[] { "- Some commit" });

        Assert.NotEqual(0, exit);
        string combined = stdout + " " + stderr;
        Assert.Contains("MaxBullets", combined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildNotes_MaxBulletsAbove50_FailsValidation()
    {
        (int exit, string stdout, string stderr) = InvokeExpectingFailure(
            currentTag: "v2.0.24", maxBullets: 100,
            forcedPreviousTag: "v2.0.23",
            forcedCommits: new[] { "- Some commit" });

        Assert.NotEqual(0, exit);
        string combined = stdout + " " + stderr;
        Assert.Contains("MaxBullets", combined, StringComparison.OrdinalIgnoreCase);
    }
}
