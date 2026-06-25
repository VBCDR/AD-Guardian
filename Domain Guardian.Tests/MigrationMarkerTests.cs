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

    /// <summary>
    /// Helper: stage a hand-rolled marker JSON so tests can drive every field.
    /// New optional fields (entriesMigrated, destinationRoot, entriesCollisions,
    /// errorCount) default to 0/empty -- callers can override when exercising
    /// the migration-mirror code paths.
    /// </summary>
    private void WriteMarker(
        string status,
        int entries = 0,
        string installTime = "2026-06-25T15:00:00",
        string reason = "",
        int schemaVersion = 1,
        int entriesMigrated = 0,
        string destinationRoot = "",
        int entriesCollisions = 0,
        int errorCount = 0,
        long bytesMigrated = 0)
    {
        var payload = new
        {
            schemaVersion,
            cleanupStatus = status,
            entriesRemoved = entries,
            installTime,
            reason,
            entriesMigrated,
            destinationRoot,
            entriesCollisions,
            errorCount,
            bytesMigrated
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
        // Migration fields are ADDITIVE; schema bumps only for incompatible shape changes.
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
        WriteMarker("removed", entries: 5, entriesMigrated: 4);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);

        Assert.NotNull(marker);
        Assert.Equal("removed", marker!.CleanupStatus);
        Assert.Equal(5, marker.EntriesRemoved);
        Assert.Equal(4, marker.EntriesMigrated);
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

    // ── Migration field roundtrip (CAS-first additive schema) ────────────

    [Fact]
    public void TryReadAndDelete_MigrationFields_Roundtrip()
    {
        // 12 MiB = 1024 * 1024 * 12 -- an integer literal that survives
        // attribute serialization. Below we cross-check via FormatUnit.
        const long twelveMb = 12L * 1024 * 1024;

        WriteMarker(
            "removed",
            entries: 7,
            entriesMigrated: 9,
            destinationRoot: @"C:\ProgramData\AdHealthMonitor\Logs\legacy-import-20260625-153045",
            entriesCollisions: 3,
            errorCount: 1,
            bytesMigrated: twelveMb);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);

        Assert.NotNull(marker);
        Assert.Equal(9, marker!.EntriesMigrated);
        Assert.Equal(
            @"C:\ProgramData\AdHealthMonitor\Logs\legacy-import-20260625-153045",
            marker.DestinationRoot.Replace('\\', Path.DirectorySeparatorChar));
        Assert.Equal(3, marker.EntriesCollisions);
        Assert.Equal(1, marker.ErrorCount);
        Assert.Equal(twelveMb, marker.BytesMigrated);
    }

    [Fact]
    public void TryReadAndDelete_V1MarkerWithoutMigrationFields_DefaultsToZeros()
    {
        // Hand-roll a v1 marker WITHOUT the new fields (mimics an old installer
        // write before this feature shipped). Newtonsoft auto-defaults handle
        // missing keys; the legacy `entriesRemoved`/`cleanupStatus`/`reason`
        // etc. should deserialize cleanly with zero/empty defaults for the new ones.
        File.WriteAllText(markerPath,
            "{\"schemaVersion\":1,\"cleanupStatus\":\"removed\",\"entriesRemoved\":3,\"installTime\":\"2026-06-25T15:00:00\"}");

        var marker = MigrationMarker.TryReadAndDelete(markerPath);

        Assert.NotNull(marker);
        Assert.Equal("removed", marker!.CleanupStatus);
        Assert.Equal(3, marker.EntriesRemoved);
        Assert.Equal(0, marker.EntriesMigrated);
        Assert.Equal(string.Empty, marker.DestinationRoot);
        Assert.Equal(0, marker.EntriesCollisions);
        Assert.Equal(0, marker.ErrorCount);
    }

    [Fact]
    public void TryReadAndDelete_DestinationRoot_WithBackslashes_Roundtrips()
    {
        // Pascal-side hands DestinationRoot through EscapeJsonString. Windows
        // backslashes don't need JSON-escape, but the roundtrip should still
        // produce the same path string on the C# side.
        WriteMarker(
            "removed",
            destinationRoot: @"C:\ProgramData\AdHealthMonitor\Logs\legacy-import-20260625-153045\sub");

        var marker = MigrationMarker.TryReadAndDelete(markerPath);

        Assert.NotNull(marker);
        Assert.Contains(@"legacy-import-20260625-153045", marker!.DestinationRoot);
        Assert.Contains("sub", marker.DestinationRoot);
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
        Assert.Contains("Cleared 7", marker!.ToToastBody());
        Assert.Contains("C:\\ADCheckLogs", marker.ToToastBody());
        Assert.Contains("AdHealthMonitor", marker.ToToastBody());
    }

    [Fact]
    public void ToToastRemovedBody_WithMigration_SurfacesCopiedCountAndDestination()
    {
        WriteMarker(
            "removed",
            entries: 5,
            entriesMigrated: 12,
            destinationRoot: @"C:\ProgramData\AdHealthMonitor\Logs\legacy-import-20260625-153045");

        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        string body = marker!.ToToastBody();
        Assert.Contains("Copied 12", body); // plural
        Assert.Contains("legacy-import-20260625-153045", body);
    }

    [Fact]
    public void ToToastRemovedBody_WithCollisions_MentionsCollisionRename()
    {
        WriteMarker("removed", entries: 5, entriesMigrated: 5, entriesCollisions: 2);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        string body = marker!.ToToastBody();
        Assert.Contains("2 files collided", body); // plural
        Assert.Contains("renamed with a timestamp suffix", body);
    }

    [Fact]
    public void ToToastRemovedBody_OneCollision_UsesSingularNoun()
    {
        WriteMarker("removed", entries: 5, entriesMigrated: 5, entriesCollisions: 1);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        // Singular form: "1 file collided"
        Assert.Contains("1 file collided", marker!.ToToastBody());
    }

    [Fact]
    public void ToToastRemovedBody_NoFilesMigrated_ShowsFriendlyExplanation()
    {
        WriteMarker("removed", entries: 5, entriesMigrated: 0);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        Assert.Contains("No *.log/*.txt/*.json/*.csv files were found to copy.", marker!.ToToastBody());
    }

    [Fact]
    public void ToToastPartialBody_IncludesErrorCountAndReason()
    {
        WriteMarker(
            "partial",
            entries: 4,
            entriesMigrated: 3,
            errorCount: 2,
            reason: "Defender mid-scan");

        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        string body = marker!.ToToastBody();
        Assert.Contains("2 file copy failures", body); // plural
        Assert.Contains("Defender mid-scan", body);
        Assert.Contains("Copied 3", body);
    }

    [Fact]
    public void ToToastPartialBody_OneError_UsesSingularNoun()
    {
        WriteMarker("partial", entries: 4, entriesMigrated: 3, errorCount: 1);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        Assert.Contains("1 file copy failure", marker!.ToToastBody());
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
    public void ToToastFailedBody_WithMigration_SurfacesCopiedCount()
    {
        // If the migration copy succeeded but the DelTree failed, the user
        // still gets the copy summary in the toast so they know their data
        // is preserved on the new path even though the legacy dir wasn't
        // removed.
        WriteMarker(
            "failed",
            entries: 0,
            entriesMigrated: 8,
            destinationRoot: @"C:\ProgramData\AdHealthMonitor\Logs\legacy-import-20260625-153045",
            reason: "DelTree returned False");

        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        string body = marker!.ToToastBody();
        Assert.Contains("Copied 8", body);
        Assert.Contains("DelTree returned False", body);
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
    public void ToToastRemovedBody_EmptyDestinationRoot_FallsBackToPlaceholder()
    {
        // Defensive: even if the installer wrote an empty destinationRoot
        // (e.g., on a fresh file system), the toast body still renders
        // something readable instead of "Copied N to ".
        WriteMarker("removed", entries: 5, entriesMigrated: 3, destinationRoot: "");

        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        Assert.Contains("legacy-import-*", marker!.ToToastBody());
    }

    [Fact]
    public void ToToastRemovedBody_ZeroEntriesRemoved_StillRenders()
    {
        // Edge case: empty legacy dir -> 0 entries removed; legacy-import path
        // is still created but has 0 files. Toast should still show.
        WriteMarker("removed", entries: 0, entriesMigrated: 0);
        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        Assert.Contains("Cleared 0", marker!.ToToastBody());
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

    // ── Pascal-side JSON mmicks (smoke) ───────────────────────────────────

    [Fact]
    public void TryReadAndDelete_PascalStyle_QuotedBackslashes_DecodeCorrectly()
    {
        // Mirrors Pascal's hand-rolled JSON output where a single backslash in
        // DestinationRoot becomes "\\\\" (escaped). Newtonsoft.Json decodes
        // back to a single backslash on read.
        const string escapedJson =
            "{" +
            "\"schemaVersion\":1," +
            "\"cleanupStatus\":\"removed\"," +
            "\"entriesRemoved\":3," +
            "\"installTime\":\"2026-06-25T15:00:00\"," +
            "\"destinationRoot\":\"C:\\\\ProgramData\\\\AdHealthMonitor\\\\Logs\\\\legacy-import-20260625-153045\"" +
            "}";
        File.WriteAllText(markerPath, escapedJson);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);

        Assert.NotNull(marker);
        Assert.Equal(
            @"C:\ProgramData\AdHealthMonitor\Logs\legacy-import-20260625-153045",
            marker!.DestinationRoot.Replace('\\', Path.DirectorySeparatorChar));
    }

    // ── BytesMigrated roundtrip + schema back-compat (v2.0.27 trigger) ────

    [Fact]
    public void V1MarkerWithoutBytesMigrated_Field_DefaultsToZero()
    {
        // Mirrors TryReadAndDelete_V1MarkerWithoutMigrationFields_DefaultsToZeros
        // but specifically asserts the NEW bytesMigrated field (added in
        // v2.0.27) deserialises cleanly when absent -- Newtonsoft's auto-
        // defaulting leaves the property at its C# initial value (0L for long).
        File.WriteAllText(markerPath,
            "{\"schemaVersion\":1,\"cleanupStatus\":\"removed\",\"entriesRemoved\":3,\"installTime\":\"2026-06-25T15:00:00\"}");

        var marker = MigrationMarker.TryReadAndDelete(markerPath);

        Assert.NotNull(marker);
        Assert.Equal(0L, marker!.BytesMigrated);
    }

    // ── FormatUnit boundary cases ─────────────────────────────────────────

    [Theory]
    [InlineData(0L, "0 bytes")]                  // exact zero
    [InlineData(-7L, "0 bytes")]                 // negative input is defanged to zero
    [InlineData(1L, "1 byte")]                   // singular byte
    [InlineData(2L, "2 bytes")]                  // plural bytes, plural noun
    [InlineData(42L, "42 bytes")]
    [InlineData(1023L, "1023 bytes")]            // edge: just below 1 KB
    [InlineData(1024L, "1 KB")]                  // exact 1 KB
    [InlineData(1536L, "1 KB")]                  // 1.5 KB floors to 1 KB (int KB)
    [InlineData(2048L, "2 KB")]
    [InlineData(1024L * 1024, "1 MB")]          // exact 1 MB
    [InlineData(1572864L, "1.5 MB")]            // 1.5 MB rounds to one DP
    [InlineData(1024L * 1024 * 1024, "1 GB")]    // exact 1 GB
    [InlineData(1024L * 1024 * 1024 * 2, "2 GB")]
    public void FormatUnit_RoundsToLargestUnit_LongInput(long bytes, string expected)
    {
        Assert.Equal(expected, MigrationMarker.FormatUnit(bytes));
    }

    [Fact]
    public void FormatUnit_IntInput_OverloadGlyph()
    {
        // Defensive: callers that pass an int (older Pascal interop code,
        // configuration files) should still get the same string as a long.
        Assert.Equal("1 MB", MigrationMarker.FormatUnit(1024 * 1024));
    }

    // ── ComputeBytesMigratedFromDisk (defensive fallback for old markers) ─

    [Fact]
    public void ComputeBytesMigratedFromDisk_NullOrEmpty_ReturnsZero()
    {
        Assert.Equal(0L, MigrationMarker.ComputeBytesMigratedFromDisk(null));
        Assert.Equal(0L, MigrationMarker.ComputeBytesMigratedFromDisk(""));
        Assert.Equal(0L, MigrationMarker.ComputeBytesMigratedFromDisk("   "));
    }

    [Fact]
    public void ComputeBytesMigratedFromDisk_NonexistentDir_ReturnsZero()
    {
        Assert.Equal(0L, MigrationMarker.ComputeBytesMigratedFromDisk(@"C:\does-not-exist-1234567890"));
    }

    [Fact]
    public void ComputeBytesMigratedFromDisk_EmptyDir_ReturnsZero()
    {
        var dir = Path.Combine(testDirectory, "empty");
        Directory.CreateDirectory(dir);

        Assert.Equal(0L, MigrationMarker.ComputeBytesMigratedFromDisk(dir));
    }

    [Fact]
    public void ComputeBytesMigratedFromDisk_PopulatedDir_SumsAllowedFileSizes()
    {
        var dir = Path.Combine(testDirectory, "populated");
        Directory.CreateDirectory(dir);
        // Sizes chosen to make the sum obvious in test output if it goes wrong.
        File.WriteAllBytes(Path.Combine(dir, "a.log"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(dir, "b.txt"), new byte[2000]);
        File.WriteAllBytes(Path.Combine(dir, "c.json"), new byte[3000]);
        File.WriteAllBytes(Path.Combine(dir, "d.csv"), new byte[4000]);

        long total = MigrationMarker.ComputeBytesMigratedFromDisk(dir);

        Assert.Equal(10_000L, total);
    }

    [Fact]
    public void ComputeBytesMigratedFromDisk_FiltersDisallowedExtensions()
    {
        var dir = Path.Combine(testDirectory, "filter");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "kept.log"), new byte[100]);
        File.WriteAllBytes(Path.Combine(dir, "ignored.exe"), new byte[999_999]); // huge but disallowed
        File.WriteAllBytes(Path.Combine(dir, "ignored.dll"), new byte[999_999]);
        File.WriteAllBytes(Path.Combine(dir, "ignored.bin"), new byte[999_999]);

        long total = MigrationMarker.ComputeBytesMigratedFromDisk(dir);

        Assert.Equal(100L, total);
    }

    [Fact]
    public void ComputeBytesMigratedFromDisk_RecursesIntoSubdirs()
    {
        var root = Path.Combine(testDirectory, "tree");
        var sub = Path.Combine(root, "runs", "2026-01-15");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(root, "top.log"), new byte[500]);
        File.WriteAllBytes(Path.Combine(sub, "run1.txt"), new byte[1500]);
        File.WriteAllBytes(Path.Combine(sub, "run2.json"), new byte[3000]);

        long total = MigrationMarker.ComputeBytesMigratedFromDisk(root);

        Assert.Equal(5000L, total);
    }

    [Fact]
    public void ComputeBytesMigratedFromDisk_MaxFilesCap_StopsTheWalk()
    {
        // Plant 100 files of 1 byte each so the cap is the ONLY thing that
        // could produce a value below the full 100. Equal(10L) is the only
        // value that proves the maxFiles cap is exactly 10:
        //   * Without the cap (always-false or off-by-one): total == 100.
        //   * With maxFiles=10: total == 10.
        //   * With maxFiles=1: total == 1.
        // (The previous test used 100-byte files with cap=2 and asserted 200L,
        // which would also pass if the cap was simply not implemented at all
        // but the walker happened to count the first 2 files -- this version
        // rules out that ambiguity by using a uniform 1-byte size so a cap
        // failure would produce a totally different number.)
        var dir = Path.Combine(testDirectory, "capped");
        Directory.CreateDirectory(dir);
        for (int i = 0; i < 100; i++)
        {
            File.WriteAllBytes(Path.Combine(dir, $"f{i:D3}.log"), new byte[1]);
        }

        long total = MigrationMarker.ComputeBytesMigratedFromDisk(dir, timeoutMs: 5000, maxFiles: 10);

        Assert.Equal(10L, total);
    }

    // ── AutoDismissSeconds (data-driven dismiss policy) ────────────────────

    [Theory]
    [InlineData("removed", 8)]   // with data: see AutoDismissSeconds_RemovedStatus_WithNoData_ReturnsZero for empty case
    [InlineData("PARTIAL", 0)]   // mixed case: partial/failed always manual
    [InlineData("partial", 0)]
    [InlineData("failed", 0)]
    [InlineData("absent", 0)]
    [InlineData("Unknown", 0)]
    [InlineData("", 0)]
    public void AutoDismissSeconds_DataDrivesTheDismissPolicy(string status, int expected)
    {
        // Force the "with data" branch via BytesMigrated so the theory
        // unambiguously asserts the WITH-data auto-dismiss behavior. The
        // empty-data branch is covered by AutoDismissSeconds_RemovedStatus_
        // WithNoData_ReturnsZero below.
        var marker = new MigrationMarker { CleanupStatus = status, BytesMigrated = 100 };
        Assert.Equal(expected, marker.AutoDismissSeconds);
    }

    [Fact]
    public void AutoDismissSeconds_RemovedStatus_WithNoData_ReturnsZero()
    {
        // Removed cleanup that found zero matching extensions (or an empty
        // legacy dir) presents a body saying "no files were found" -- if we
        // auto-dismiss the toast in 8 s, that message is invisible to most
        // users. Hold the dialog open so the empty-success case is explicitly
        // acknowledged.
        var marker = new MigrationMarker
        {
            CleanupStatus = "removed",
            BytesMigrated = 0,
            EntriesMigrated = 0,
            EntriesRemoved = 0,
        };
        Assert.Equal(0, marker.AutoDismissSeconds);
    }

    [Fact]
    public void AutoDismissSeconds_RemovedIsCaseInsensitive()
    {
        // Both 'REMOVED' and 'Removed' (case-insensitive match) with non-zero
        // BytesMigrated auto-dismiss to 8 seconds. The empty-data branch is
        // covered by AutoDismissSeconds_RemovedStatus_WithNoData_ReturnsZero.
        var removed = new MigrationMarker { CleanupStatus = "REMOVED", BytesMigrated = 1024 };
        Assert.Equal(8, removed.AutoDismissSeconds);
        var alsoRemoved = new MigrationMarker { CleanupStatus = "Removed", BytesMigrated = 1024 };
        Assert.Equal(8, alsoRemoved.AutoDismissSeconds);
    }

    // ── Byte-aware toast body (v2.0.27 user-facing phrasing) ──────────────

    [Fact]
    public void ToToastRemovedBody_WithBytesMigrated_SurfacesMegabyteHint()
    {
        // 12 MiB.
        const long twelveMb = 12L * 1024 * 1024;
        WriteMarker(
            "removed",
            entries: 5,
            entriesMigrated: 3,
            destinationRoot: @"C:\ProgramData\AdHealthMonitor\Logs\legacy-import-20260625-153045",
            bytesMigrated: twelveMb);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        string body = marker!.ToToastBody();

        // Headline appears BEFORE the legacy detail paragraphs -- the user
        // sees "We cleaned up X MB" first thing.
        Assert.Contains("We cleaned up 12 MB of legacy logs.", body);
        Assert.Contains("Any new runs will write to %ProgramData%\\AdHealthMonitor\\Logs", body);
        Assert.Contains("Copied 3 legacy log files to:", body);
        Assert.Contains("legacy-import-20260625-153045", body);
    }

    [Fact]
    public void ToToastRemovedBody_WithBytesMigrated_KbRange_UsesKbUnit()
    {
        const long fortyTwoKb = 42L * 1024;
        WriteMarker("removed", entries: 4, entriesMigrated: 2, bytesMigrated: fortyTwoKb);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        Assert.Contains("We cleaned up 42 KB of legacy logs.", marker!.ToToastBody());
    }

    [Fact]
    public void ToToastRemovedBody_WithBytesMigrated_BytesRange_UsesBytesUnit()
    {
        // Below 1 KB stays in 'bytes' -- the FormatUnit contract ensures an
        // honest "N bytes" rather than rounding to "1 KB".
        WriteMarker("removed", entries: 4, entriesMigrated: 1, bytesMigrated: 847L);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        Assert.Contains("We cleaned up 847 bytes of legacy logs.", marker!.ToToastBody());
    }

    [Fact]
    public void ToToastRemovedBody_WithoutBytesMigrated_NoLeadIn()
    {
        // bytesMigrated=0 (older installer writes, or empty legacy dir).
        // Lead-in paragraph must be ABSENT -- not "We cleaned up 0 bytes".
        WriteMarker("removed", entries: 5, entriesMigrated: 0, bytesMigrated: 0L);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        string body = marker!.ToToastBody();
        Assert.DoesNotContain("We cleaned up", body);
        Assert.DoesNotContain("We migrated", body);
        // Existing skeletal copy-note still applies.
        Assert.Contains("No *.log/*.txt/*.json/*.csv files were found to copy.", body);
    }

    [Fact]
    public void ToToastPartialBody_WithBytesMigrated_UsesMigrationFraming()
    {
        const long eightMb = 8L * 1024 * 1024;
        WriteMarker(
            "partial",
            entries: 4,
            entriesMigrated: 3,
            destinationRoot: @"C:\ProgramData\AdHealthMonitor\Logs\legacy-import-20260625-153045",
            errorCount: 2,
            reason: "Defender mid-scan",
            bytesMigrated: eightMb);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        string body = marker!.ToToastBody();
        Assert.Contains("We migrated 8 MB of legacy logs before cleanup stopped at the legacy path.", body);
        Assert.Contains("Copied 3 legacy log files", body);
        Assert.Contains("2 file copy failures", body);
        Assert.Contains("Defender mid-scan", body);
    }

    [Fact]
    public void ToToastFailedBody_WithBytesMigrated_UsesMigrationFraming()
    {
        const long twelveKb = 12L * 1024;
        WriteMarker(
            "failed",
            entries: 0,
            entriesMigrated: 8,
            destinationRoot: @"C:\ProgramData\AdHealthMonitor\Logs\legacy-import-20260625-153045",
            reason: "DelTree returned False",
            bytesMigrated: twelveKb);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        string body = marker!.ToToastBody();
        Assert.Contains("We migrated 12 KB of legacy logs but C:\\ADCheckLogs could not be removed.", body);
        Assert.Contains("DelTree returned False", body);
        Assert.Contains("Setup Log", body);
    }

    [Fact]
    public void ToToastFailedBody_WithoutBytesMigrated_NoLeadIn()
    {
        WriteMarker("failed", entries: 0, reason: "Defender CFA lock", bytesMigrated: 0L);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        string body = marker!.ToToastBody();
        Assert.DoesNotContain("We cleaned up", body);
        Assert.DoesNotContain("We migrated", body);
        Assert.Contains("tried to clean up C:\\ADCheckLogs but could not remove the directory.", body);
        Assert.Contains("Defender CFA lock", body);
    }

    // ── Reviewer NEEDS-FIX #6: lead-in and legacy 'Cleared N' must NOT both appear ──

    [Fact]
    public void ToToastRemovedBody_WithBytesMigrated_DropsRedundantClearedCount()
    {
        // When the byte lead-in already conveys size, the legacy "Cleared N
        // top-level entries from C:\\ADCheckLogs" detail is redundant and
        // noisy -- dropping it. Body must NOT contain the cleared-count line.
        const long twelveMb = 12L * 1024 * 1024;
        WriteMarker(
            "removed",
            entries: 5,
            entriesMigrated: 3,
            destinationRoot: @"C:\ProgramData\AdHealthMonitor\Logs\legacy-import-20260625-153045",
            bytesMigrated: twelveMb);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        string body = marker!.ToToastBody();
        Assert.DoesNotContain("Cleared 5 top-level entries", body);
        Assert.DoesNotContain("top-level entries from C:\\ADCheckLogs", body);
    }

    [Fact]
    public void ToToastRemovedBody_WithBytesMigrated_StillNamesCopiedDestination()
    {
        // Belt-and-braces companion to the lead-in test: even after dropping
        // the redundant "Cleared N" line, the body still pinpoints WHERE the
        // legacy files ended up -- the lead-in alone doesn't carry that.
        // Catches a future regression where the destination paragraph gets
        // removed "for symmetry" with the cleared-count removal.
        const long twelveMb = 12L * 1024 * 1024;
        WriteMarker(
            "removed",
            entries: 5,
            entriesMigrated: 3,
            destinationRoot: @"C:\ProgramData\AdHealthMonitor\Logs\legacy-import-20260625-153045",
            bytesMigrated: twelveMb);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        string body = marker!.ToToastBody();
        Assert.Contains("Copied 3 legacy log files to:", body);
        Assert.Contains("legacy-import-20260625-153045", body);
    }

    [Fact]
    public void ToToastRemovedBody_WithoutBytesMigrated_KeepsClearedCountDetail()
    {
        // Compensating guarantee: when BytesMigrated == 0 (empty legacy dir
        // or pre-v2.0.27 marker), the "Cleared N" detail is the ONLY size
        // information available in the body. It must remain.
        WriteMarker("removed", entries: 5, entriesMigrated: 0);

        var marker = MigrationMarker.TryReadAndDelete(markerPath);
        Assert.NotNull(marker);
        Assert.Contains("Cleared 5 top-level entries", marker!.ToToastBody());
    }
}
