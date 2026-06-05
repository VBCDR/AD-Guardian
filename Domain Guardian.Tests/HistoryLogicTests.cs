using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AdHealthMonitor;
using Xunit;

namespace Domain_Guardian.Tests;

/// <summary>
/// Tests for the History tab logic: BuildHistoryEntryKey, IsDuplicateHistoryEntry,
/// and MatchesHistoryFilter (date + search text filtering).
/// </summary>
public class HistoryLogicTests
{
    // ── BuildHistoryEntryKey tests ───────────────────────────────────────

    [Fact]
    public void BuildHistoryEntryKey_IncludesAllFields()
    {
        var entry = new TestHistoryEntry
        {
            RunDate = new DateTime(2025, 6, 15, 14, 30, 0),
            TestType = "Manual",
            LogFilePath = @"C:\logs\test.log",
            Total = 20,
            Passed = 18,
            Failed = 2,
            Details = "Domain controllers tested: 2"
        };

        string key = MainWindow.BuildHistoryEntryKey(entry);

        Assert.Contains(entry.RunDate.Ticks.ToString(CultureInfo.InvariantCulture), key);
        Assert.Contains("Manual", key);
        Assert.Contains("test.log", key);
        Assert.Contains("20", key);
        Assert.Contains("18", key);
        Assert.Contains("2", key);
        Assert.Contains("Domain controllers tested", key);
    }

    [Fact]
    public void BuildHistoryEntryKey_HandlesNullFields()
    {
        var entry = new TestHistoryEntry
        {
            RunDate = new DateTime(2025, 6, 15),
            TestType = null!,
            LogFilePath = null!,
            Details = null!
        };

        // Should not throw
        string key = MainWindow.BuildHistoryEntryKey(entry);
        Assert.NotNull(key);
        Assert.Contains("|", key); // pipe-delimited
    }

    [Fact]
    public void BuildHistoryEntryKey_DifferentEntries_ProduceDifferentKeys()
    {
        var e1 = new TestHistoryEntry { RunDate = new DateTime(2025, 6, 15), Total = 10, Passed = 8, Failed = 2 };
        var e2 = new TestHistoryEntry { RunDate = new DateTime(2025, 6, 15), Total = 10, Passed = 9, Failed = 1 };

        Assert.NotEqual(MainWindow.BuildHistoryEntryKey(e1), MainWindow.BuildHistoryEntryKey(e2));
    }

    [Fact]
    public void BuildHistoryEntryKey_SameEntries_ProduceSameKeys()
    {
        var dt = new DateTime(2025, 6, 15, 10, 0, 0);
        var e1 = new TestHistoryEntry { RunDate = dt, TestType = "Manual", Total = 20, Passed = 18, Failed = 2, Details = "ok", LogFilePath = "test.log" };
        var e2 = new TestHistoryEntry { RunDate = dt, TestType = "Manual", Total = 20, Passed = 18, Failed = 2, Details = "ok", LogFilePath = "test.log" };

        Assert.Equal(MainWindow.BuildHistoryEntryKey(e1), MainWindow.BuildHistoryEntryKey(e2));
    }

    // ── IsDuplicateHistoryEntry tests ────────────────────────────────────

    [Fact]
    public void IsDuplicateHistoryEntry_EmptyList_ReturnsFalse()
    {
        var candidate = new TestHistoryEntry
        {
            RunDate = DateTime.Now,
            Total = 20, Passed = 18, Failed = 2,
            TestType = "Manual",
            Details = "ok",
            LogFilePath = "test.log"
        };

        Assert.False(MainWindow.IsDuplicateHistoryEntry(new List<TestHistoryEntry>(), candidate));
    }

    [Fact]
    public void IsDuplicateHistoryEntry_ExactDuplicate_ReturnsTrue()
    {
        DateTime now = DateTime.Now;
        var existing = new TestHistoryEntry
        {
            RunDate = now,
            Total = 20, Passed = 18, Failed = 2,
            TestType = "Manual",
            Details = "ok",
            LogFilePath = "test.log"
        };
        var candidate = new TestHistoryEntry
        {
            RunDate = now.AddSeconds(30), // within 2 minutes
            Total = 20, Passed = 18, Failed = 2,
            TestType = "Manual",
            Details = "ok",
            LogFilePath = "test.log"
        };

        Assert.True(MainWindow.IsDuplicateHistoryEntry(new List<TestHistoryEntry> { existing }, candidate));
    }

    [Fact]
    public void IsDuplicateHistoryEntry_OutsideTimeWindow_ReturnsFalse()
    {
        DateTime now = DateTime.Now;
        var existing = new TestHistoryEntry
        {
            RunDate = now,
            Total = 20, Passed = 18, Failed = 2,
            TestType = "Manual",
            Details = "ok",
            LogFilePath = "test.log"
        };
        var candidate = new TestHistoryEntry
        {
            RunDate = now.AddMinutes(3), // outside 2-minute window
            Total = 20, Passed = 18, Failed = 2,
            TestType = "Manual",
            Details = "ok",
            LogFilePath = "test.log"
        };

        Assert.False(MainWindow.IsDuplicateHistoryEntry(new List<TestHistoryEntry> { existing }, candidate));
    }

    [Fact]
    public void IsDuplicateHistoryEntry_DifferentTotal_ReturnsFalse()
    {
        DateTime now = DateTime.Now;
        var existing = new TestHistoryEntry
        {
            RunDate = now, Total = 20, Passed = 18, Failed = 2,
            TestType = "Manual", Details = "ok", LogFilePath = "test.log"
        };
        var candidate = new TestHistoryEntry
        {
            RunDate = now.AddSeconds(10), Total = 22, Passed = 20, Failed = 2,
            TestType = "Manual", Details = "ok", LogFilePath = "test.log"
        };

        Assert.False(MainWindow.IsDuplicateHistoryEntry(new List<TestHistoryEntry> { existing }, candidate));
    }

    [Fact]
    public void IsDuplicateHistoryEntry_DifferentTestType_ReturnsFalse()
    {
        DateTime now = DateTime.Now;
        var existing = new TestHistoryEntry
        {
            RunDate = now, Total = 20, Passed = 18, Failed = 2,
            TestType = "Manual", Details = "ok", LogFilePath = "test.log"
        };
        var candidate = new TestHistoryEntry
        {
            RunDate = now.AddSeconds(10), Total = 20, Passed = 18, Failed = 2,
            TestType = "Scheduled", Details = "ok", LogFilePath = "test.log"
        };

        Assert.False(MainWindow.IsDuplicateHistoryEntry(new List<TestHistoryEntry> { existing }, candidate));
    }

    [Fact]
    public void IsDuplicateHistoryEntry_TestCaseInsensitiveTestType()
    {
        DateTime now = DateTime.Now;
        var existing = new TestHistoryEntry
        {
            RunDate = now, Total = 20, Passed = 18, Failed = 2,
            TestType = "Manual", Details = "ok", LogFilePath = "test.log"
        };
        var candidate = new TestHistoryEntry
        {
            RunDate = now.AddSeconds(10), Total = 20, Passed = 18, Failed = 2,
            TestType = "manual", Details = "ok", LogFilePath = "test.log"
        };

        // TestType comparison is case-insensitive
        Assert.True(MainWindow.IsDuplicateHistoryEntry(new List<TestHistoryEntry> { existing }, candidate));
    }

    [Fact]
    public void IsDuplicateHistoryEntry_CaseSensitiveDetails()
    {
        DateTime now = DateTime.Now;
        var existing = new TestHistoryEntry
        {
            RunDate = now, Total = 20, Passed = 18, Failed = 2,
            TestType = "Manual", Details = "Domain controllers tested: 2", LogFilePath = "test.log"
        };
        var candidate = new TestHistoryEntry
        {
            RunDate = now.AddSeconds(10), Total = 20, Passed = 18, Failed = 2,
            TestType = "Manual", Details = "domain controllers tested: 2", LogFilePath = "test.log"
        };

        // Details comparison is case-SENSITIVE (Ordinal)
        Assert.False(MainWindow.IsDuplicateHistoryEntry(new List<TestHistoryEntry> { existing }, candidate));
    }

    [Fact]
    public void IsDuplicateHistoryEntry_DifferentLogFilePath_ReturnsFalse()
    {
        DateTime now = DateTime.Now;
        var existing = new TestHistoryEntry
        {
            RunDate = now, Total = 20, Passed = 18, Failed = 2,
            TestType = "Manual", Details = "ok", LogFilePath = "test1.log"
        };
        var candidate = new TestHistoryEntry
        {
            RunDate = now.AddSeconds(10), Total = 20, Passed = 18, Failed = 2,
            TestType = "Manual", Details = "ok", LogFilePath = "test2.log"
        };

        Assert.False(MainWindow.IsDuplicateHistoryEntry(new List<TestHistoryEntry> { existing }, candidate));
    }

    [Fact]
    public void IsDuplicateHistoryEntry_MultipleEntries_ChecksAll()
    {
        DateTime now = DateTime.Now;
        var entries = new List<TestHistoryEntry>
        {
            new() { RunDate = now.AddDays(-1), Total = 10, Passed = 10, Failed = 0, TestType = "Manual", Details = "a", LogFilePath = "a.log" },
            new() { RunDate = now.AddDays(-2), Total = 15, Passed = 15, Failed = 0, TestType = "Manual", Details = "b", LogFilePath = "b.log" },
        };
        var candidate = new TestHistoryEntry
        {
            RunDate = now.AddDays(-2).AddSeconds(30), Total = 15, Passed = 15, Failed = 0,
            TestType = "Manual", Details = "b", LogFilePath = "b.log"
        };

        // Matches the second entry
        Assert.True(MainWindow.IsDuplicateHistoryEntry(entries, candidate));
    }

    // ── MatchesHistoryFilter tests ───────────────────────────────────────

    private static readonly TestHistoryEntry SampleEntry = new()
    {
        RunDate = new DateTime(2025, 6, 15, 14, 30, 0),
        TestType = "Manual",
        Total = 20,
        Passed = 18,
        Failed = 2,
        Details = "Domain controllers tested: 2. Controllers: DC01, DC02. Total tests: 20.",
        LogFilePath = @"C:\logs\test.log"
    };

    [Fact]
    public void MatchesHistoryFilter_NoFilters_ReturnsTrue()
    {
        Assert.True(MainWindow.MatchesHistoryFilter(SampleEntry, "", null));
    }

    [Fact]
    public void MatchesHistoryFilter_DateFilter_MatchingDate_ReturnsTrue()
    {
        Assert.True(MainWindow.MatchesHistoryFilter(SampleEntry, "", new DateTime(2025, 6, 15)));
    }

    [Fact]
    public void MatchesHistoryFilter_DateFilter_DifferentDate_ReturnsFalse()
    {
        Assert.False(MainWindow.MatchesHistoryFilter(SampleEntry, "", new DateTime(2025, 6, 16)));
    }

    [Fact]
    public void MatchesHistoryFilter_SearchText_MatchesDetails()
    {
        Assert.True(MainWindow.MatchesHistoryFilter(SampleEntry, "controllers", null));
    }

    [Fact]
    public void MatchesHistoryFilter_SearchText_MatchesTestType()
    {
        Assert.True(MainWindow.MatchesHistoryFilter(SampleEntry, "manual", null));
    }

    [Fact]
    public void MatchesHistoryFilter_SearchText_MatchesTotal()
    {
        Assert.True(MainWindow.MatchesHistoryFilter(SampleEntry, "20", null));
    }

    [Fact]
    public void MatchesHistoryFilter_SearchText_MatchesPassed()
    {
        Assert.True(MainWindow.MatchesHistoryFilter(SampleEntry, "18", null));
    }

    [Fact]
    public void MatchesHistoryFilter_SearchText_MatchesFailed()
    {
        Assert.True(MainWindow.MatchesHistoryFilter(SampleEntry, "2", null));
    }

    [Fact]
    public void MatchesHistoryFilter_SearchText_CaseInsensitive()
    {
        Assert.True(MainWindow.MatchesHistoryFilter(SampleEntry, "MANUAL", null));
        Assert.True(MainWindow.MatchesHistoryFilter(SampleEntry, "domain CONTROLLERS", null));
    }

    [Fact]
    public void MatchesHistoryFilter_SearchText_NoMatch_ReturnsFalse()
    {
        Assert.False(MainWindow.MatchesHistoryFilter(SampleEntry, "nonexistent_xyz", null));
    }

    [Fact]
    public void MatchesHistoryFilter_CombinedFilters_DateAndSearch()
    {
        // Matching date + matching search
        Assert.True(MainWindow.MatchesHistoryFilter(SampleEntry, "manual", new DateTime(2025, 6, 15)));
        // Non-matching date + matching search
        Assert.False(MainWindow.MatchesHistoryFilter(SampleEntry, "manual", new DateTime(2025, 6, 16)));
        // Matching date + non-matching search
        Assert.False(MainWindow.MatchesHistoryFilter(SampleEntry, "nonexistent", new DateTime(2025, 6, 15)));
    }

    [Fact]
    public void MatchesHistoryFilter_NullDetails_DoesNotThrow()
    {
        var entry = new TestHistoryEntry
        {
            RunDate = new DateTime(2025, 6, 15),
            TestType = "Manual",
            Details = null!,
            Total = 10, Passed = 10, Failed = 0
        };

        // Should not throw; search text won't match null Details
        Assert.False(MainWindow.MatchesHistoryFilter(entry, "something", null));
        Assert.True(MainWindow.MatchesHistoryFilter(entry, "", null));
    }

    [Fact]
    public void MatchesHistoryFilter_NullTestType_DoesNotThrow()
    {
        var entry = new TestHistoryEntry
        {
            RunDate = new DateTime(2025, 6, 15),
            TestType = null!,
            Details = "ok",
            Total = 10, Passed = 10, Failed = 0
        };

        Assert.False(MainWindow.MatchesHistoryFilter(entry, "manual", null));
        Assert.True(MainWindow.MatchesHistoryFilter(entry, "", null));
    }

    // ── Full pipeline: BuildHistoryEntryKey + IsDuplicate dedup ──────────

    [Fact]
    public void FullPipeline_DeduplicateByKey_WorksCorrectly()
    {
        DateTime now = DateTime.Now;
        var entries = new List<TestHistoryEntry>
        {
            new() { RunDate = now, Total = 20, Passed = 18, Failed = 2, TestType = "Manual", Details = "a", LogFilePath = "test.log" },
            new() { RunDate = now.AddMinutes(-5), Total = 20, Passed = 18, Failed = 2, TestType = "Manual", Details = "a", LogFilePath = "test.log" },
        };

        // Deduplicate using keys
        var deduped = new List<TestHistoryEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            string key = MainWindow.BuildHistoryEntryKey(entry);
            if (seen.Add(key))
                deduped.Add(entry);
        }

        // Both entries have different RunDate.Ticks so different keys → both kept
        Assert.Equal(2, deduped.Count);

        // But IsDuplicateHistoryEntry considers them duplicates (within 2 min, same data)
        var candidate = new TestHistoryEntry
        {
            RunDate = now.AddSeconds(30), Total = 20, Passed = 18, Failed = 2,
            TestType = "Manual", Details = "a", LogFilePath = "test.log"
        };
        Assert.True(MainWindow.IsDuplicateHistoryEntry(entries, candidate));
    }

    [Fact]
    public void FullPipeline_FilterThenDeduplicate_WorksCorrectly()
    {
        DateTime now = DateTime.Now;
        var entries = new List<TestHistoryEntry>
        {
            new() { RunDate = now, Total = 20, Passed = 18, Failed = 2, TestType = "Manual", Details = "DNS check failed", LogFilePath = "test1.log" },
            new() { RunDate = now.AddDays(-1), Total = 10, Passed = 10, Failed = 0, TestType = "Scheduled", Details = "All passed", LogFilePath = "test2.log" },
            new() { RunDate = now.AddDays(-2), Total = 15, Passed = 12, Failed = 3, TestType = "Manual", Details = "Replication issues", LogFilePath = "test3.log" },
        };

        // Filter: search for "Manual"
        var filtered = entries
            .Where(e => MainWindow.MatchesHistoryFilter(e, "manual", null))
            .ToList();

        Assert.Equal(2, filtered.Count);
        Assert.All(filtered, e => Assert.Equal("Manual", e.TestType));

        // A new candidate that doesn't match any existing entry
        var newCandidate = new TestHistoryEntry
        {
            RunDate = now.AddMinutes(10), Total = 25, Passed = 25, Failed = 0,
            TestType = "Manual", Details = "New run", LogFilePath = "new.log"
        };
        Assert.False(MainWindow.IsDuplicateHistoryEntry(filtered, newCandidate));
    }
}
