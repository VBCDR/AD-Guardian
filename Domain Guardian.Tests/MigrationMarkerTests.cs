using System;
using System.IO;
using AdHealthMonitor;
using Newtonsoft.Json;
using Xunit;

namespace Domain_Guardian.Tests;

public class MigrationMarkerTests : IDisposable
{
    private readonly string testDirectory;
    private readonly string markerPath;

    public MigrationMarkerTests()
    {
        testDirectory = Path.Combine(Path.GetTempPath(), "MigrationMarkerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        markerPath = Path.Combine(testDirectory, "MigrationMarker.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
        }
        catch
        {
            // best-effort cleanup; survives CI runners with locked files
        }
    }

    /// <summary>Helper: stage a hand-rolled marker JSON so tests can drive every field.</summary>
    private void WriteMarker(string status, int entries = 0, string installTime = "2026-06-25T15:00:00", string reason = "", int schemaVersion = 1)
    {
        var payload = new
        {
            schemaVersion,
            cleanupStatus = status,
            entriesRemoved = entries,
            installTime,
            reason
        };
        File.WriteAllText(markerPath, JsonConvert.SerializeObject(payload));
    }

    // ── Default path resolution ───────────────────────────────────────────

    [Fact]
    public void DefaultMarkerPath_ResolvesUnderCommonApplicationData()
    {
        string expectedRoot = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string fullPath = Path.GetFullPath(MigrationMarker.DefaultMarkerPath);

        Assert.StartsWith(expectedRoot, fullPath);
        Assert.EndsWith("MigrationMarker.json", fullPath);
        Assert.Contains("AdHealthMonitor", fullPath);
    }

    [Fact]
    public void DefaultMarkerPath_IsSiblingOfLogDirectoryPath()
    {
        // Log dir = %ProgramData%\AdHealthMonitor\Logs; marker must be a sibling
        // (same parent) so the installer's ExpandConstant('{commonappdata}')
        // produces the same path the app computes.
        string logParent = Path.GetDirectoryName(App.LogDirectoryPath)!;
        string markerParent = Path.GetDirectoryName(MigrationMarker.DefaultMarkerPath)!;

        Assert.Equal(
            logParent.Replace('\\', Path.DirectorySeparatorChar),
            markerParent.Replace('\\', Path.DirectorySeparatorChar));
    }

    [Fact]
    public void CurrentSchemaVersion_IsOne()
    {
        // If you bump this, the installer + tests must be updated together.
        Assert.Equal(1, MigrationMarker.CurrentSchemaVersion);
    }

    // ── Happy path + consumption ──────────────────────────────────────────

    [Fact]
    public void TryReadAndDelete_MissingFile_ReturnsNull()
    {
        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.Null(marker);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public void TryReadAndDelete_ValidRemovedStatus_ReturnsMarker_AndDeletesFile()
    {
        WriteMarker("removed", entries: 5);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);

        Assert.NotNull(marker);
        Assert.Equal("removed", marker!.CleanupStatus);
        Assert.Equal(5, marker.EntriesRemoved);
        Assert.True(marker.IsSignificantForToast);
        Assert.False(File.Exists(markerPath)); // consumed
    }

    [Fact]
    public void TryReadAndDelete_ValidFailedStatus_ReturnsMarker_AndSignalsWarning()
    {
        WriteMarker("failed", entries: 0, reason: "Defender CFA lock");

        var marker = MigrationMarker.TryReadAndDelete(markerPath);

        Assert.NotNull(marker);
        Assert.Equal("failed", marker!.CleanupStatus);
        Assert.Equal("Defender CFA lock", marker.Reason);
        Assert.True(marker.IsSignificantForToast);
        Assert.Equal("Migration Cleanup Warning", marker.ToToastTitle());
    }

    [Fact]
    public void TryReadAndDelete_AbsentStatus_ReturnsMarker_ButNotSignificant()
    {
        WriteMarker("absent");

        var marker = MigrationMarker.TryReadAndDelete(markerPath);

        Assert.NotNull(marker);
        Assert.Equal("absent", marker!.CleanupStatus);
        Assert.False(marker.IsSignificantForToast); // clean install = no toast
    }

    // ── Corrupted / partial / future schema ────────────────────────────────

    [Fact]
    public void TryReadAndDelete_InvalidJson_ReturnsNull_AndKeepsMarkerForForensics()
    {
        File.WriteAllText(markerPath, "{ not valid json ");

        var marker = MigrationMarker.TryReadAndDelete(markerPath);

        Assert.Null(marker);
        Assert.True(File.Exists(markerPath)); // forensics preserved
    }

    [Fact]
    public void TryReadAndDelete_PartialJson_ReturnsNull_AndKeepsMarker()
    {
        File.WriteAllText(markerPath, "{\"schemaVersion\":1,");

        var marker = MigrationMarker.TryReadAndDelete(markerPath);

        Assert.Null(marker);
        Assert.True(File.Exists(markerPath));
    }

    [Fact]
    public void TryReadAndDelete_FutureSchemaVersion_ReturnsNull_AndKeepsMarker()
    {
        WriteMarker("removed", schemaVersion: 99);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);

        Assert.Null(marker);
        Assert.True(File.Exists(markerPath));
    }

    [Fact]
    public void TryReadAndDelete_SchemaVersionZero_ReturnsNull_AndKeepsMarker()
    {
        WriteMarker("removed", schemaVersion: 0);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);

        Assert.Null(marker);
        Assert.True(File.Exists(markerPath));
    }

    [Fact]
    public void TryReadAndDelete_NegativeSchemaVersion_ReturnsNull_AndKeepsMarker()
    {
        WriteMarker("removed", schemaVersion: -1);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);

        Assert.Null(marker);
        Assert.True(File.Exists(markerPath));
    }

    [Fact]
    public void TryReadAndDelete_LiteralJsonNull_ReturnsNull_AndKeepsMarker()
    {
        File.WriteAllText(markerPath, "null");

        var marker = MigrationMarker.TryReadAndDelete(markerPath);

        Assert.Null(marker);
        Assert.True(File.Exists(markerPath));
    }

    [Fact]
    public void TryReadAndDelete_EmptyCleanupStatus_ReturnsNull_AndKeepsMarker()
    {
        File.WriteAllText(markerPath, "{\"schemaVersion\":1,\"cleanupStatus\":\"\",\"entriesRemoved\":0,\"installTime\":\"\",\"reason\":\"\"}");

        var marker = MigrationMarker.TryReadAndDelete(markerPath);

        Assert.Null(marker);
        Assert.True(File.Exists(markerPath));
    }

    [Fact]
    public void TryReadAndDelete_EmptyFile_ReturnsNull_AndKeepsMarker()
    {
        File.WriteAllText(markerPath, string.Empty);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);

        Assert.Null(marker);
        Assert.True(File.Exists(markerPath));
    }

    // ── Title + body formatting ───────────────────────────────────────────

    [Fact]
    public void ToToastTitle_RemovedStatus_ReturnsCompletionTitle()
    {
        WriteMarker("removed", entries: 3);
        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        Assert.Equal("Migration Complete", marker!.ToToastTitle());
    }

    [Fact]
    public void ToToastTitle_PartialStatus_ReturnsPartialTitle()
    {
        WriteMarker("partial", entries: 2);
        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        Assert.Equal("Migration Partially Complete", marker!.ToToastTitle());
    }

    [Fact]
    public void ToToastRemovedBody_MentionsEntriesRemoved_AndMigrationPath()
    {
        WriteMarker("removed", entries: 7);
        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        Assert.Contains("Removed 7", marker!.ToToastBody());
        Assert.Contains("C:\\ADCheckLogs", marker.ToToastBody());
        Assert.Contains("AdHealthMonitor", marker.ToToastBody());
    }

    [Fact]
    public void ToToastFailedBody_MentionsReason()
    {
        WriteMarker("failed", entries: 0, reason: "Defender CFA lock");
        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        Assert.Contains("Defender CFA lock", marker!.ToToastBody());
    }

    [Fact]
    public void ToToastPartialBody_MentionsEntriesAndReason()
    {
        WriteMarker("partial", entries: 2, reason: "AV holding 1 file");
        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        Assert.Contains("2", marker!.ToToastBody());
        Assert.Contains("AV holding 1 file", marker.ToToastBody());
    }

    [Fact]
    public void ToToastFailedBody_NoReason_FallsBackToPlaceholder()
    {
        WriteMarker("failed", entries: 0, reason: "");
        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        Assert.Contains("(none provided)", marker!.ToToastBody());
    }

    [Fact]
    public void ToToastRemovedBody_ZeroEntries_MentionsZero()
    {
        // Edge case: empty legacy dir -> 0 entries removed but still toast.
        WriteMarker("removed", entries: 0);
        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        Assert.Contains("Removed 0", marker!.ToToastBody());
    }

    // ── IsSignificantForToast predicate ───────────────────────────────────

    [Theory]
    [InlineData("removed", true)]
    [InlineData("partial", true)]
    [InlineData("failed", true)]
    [InlineData("absent", false)]
    [InlineData("REMOVED", true)] // case-insensitive
    [InlineData("Unknown", false)]
    [InlineData("", false)]
    public void IsSignificantForToast_MatchesExpected(string status, bool expected)
    {
        var marker = new MigrationMarker { CleanupStatus = status };
        Assert.Equal(expected, marker.IsSignificantForToast);
    }

    // ── Idempotence: second read after first read+delete returns null ─────

    [Fact]
    public void TryReadAndDelete_Twice_SecondReturnsNull()
    {
        WriteMarker("removed", entries: 3);
        var first = MigrationMarker.TryReadAndDelete(markerPath);
        var second = MigrationMarker.TryReadAndDelete(markerPath);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    // ── Unicode reasons roundtrip through Pascal's RFC-8259 escaping ──────

    [Fact]
    public void TryReadAndDelete_UnicodeInReason_Roundtrips()
    {
        File.WriteAllText(markerPath,
            "{\"schemaVersion\":1,\"cleanupStatus\":\"failed\",\"entriesRemoved\":0,\"installTime\":\"2026-06-25T15:00:00\",\"reason\":\"Jos\\u00e9 held file lock\"}");

        var marker = MigrationMarker.TryReadAndDelete(markerPath);

        Assert.NotNull(marker);
        Assert.Contains("Jos\u00e9 held file lock", marker!.Reason);
    }
}
