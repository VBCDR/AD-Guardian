using System;
using System.Collections.Generic;
using System.IO;
using AdHealthMonitor;
using Xunit;

namespace Domain_Guardian.Tests;

public class AppStateStoreTests : IDisposable
{
    private readonly string testDirectory;
    private readonly string databasePath;

    public AppStateStoreTests()
    {
        testDirectory = Path.Combine(Path.GetTempPath(), "AdHealthMonitorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        databasePath = Path.Combine(testDirectory, "test.db");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private AppStateStore CreateStore() => new(databasePath);

    // ── Initialization ────────────────────────────────────────────────────

    [Fact]
    public void Initialize_CreatesDatabaseFile()
    {
        var store = CreateStore();

        store.Initialize();

        Assert.True(File.Exists(databasePath));
    }

    [Fact]
    public void Initialize_CreatesParentDirectory()
    {
        string nestedPath = Path.Combine(testDirectory, "sub", "folder", "test.db");
        var store = new AppStateStore(nestedPath);

        store.Initialize();

        Assert.True(File.Exists(nestedPath));
    }

    [Fact]
    public void Initialize_CalledTwice_DoesNotThrow()
    {
        var store = CreateStore();

        store.Initialize();
        store.Initialize(); // Should not throw
    }

    // ── Settings roundtrip ────────────────────────────────────────────────

    [Fact]
    public void LoadSettings_EmptyDatabase_ReturnsDefaults()
    {
        var store = CreateStore();

        var settings = store.LoadSettings();

        Assert.NotNull(settings);
        Assert.Equal(string.Empty, settings.DomainControllers);
        Assert.True(settings.TestDnsCheck);
        Assert.True(settings.TestReplication);
    }

    [Fact]
    public void SaveSettings_ThenLoad_Roundtrips()
    {
        var store = CreateStore();
        var original = new PersistedAppSettings
        {
            DomainControllers = "dc01.corp.local,dc02.corp.local",
            RecipientEmail = "admin@corp.local",
            TestDnsCheck = false,
            TestReplication = false,
            TestTimeSkew = true,
            TestLdapBind = false,
            TestCertDhcp = true,
            TestSmbLdapSigning = false,
            SendEmailManual = false,
            SendEmailScheduled = true
        };

        store.SaveSettings(original);
        var loaded = store.LoadSettings();

        Assert.Equal(original.DomainControllers, loaded.DomainControllers);
        Assert.Equal(original.RecipientEmail, loaded.RecipientEmail);
        Assert.Equal(original.TestDnsCheck, loaded.TestDnsCheck);
        Assert.Equal(original.TestReplication, loaded.TestReplication);
        Assert.Equal(original.TestTimeSkew, loaded.TestTimeSkew);
        Assert.Equal(original.TestLdapBind, loaded.TestLdapBind);
        Assert.Equal(original.TestCertDhcp, loaded.TestCertDhcp);
        Assert.Equal(original.TestSmbLdapSigning, loaded.TestSmbLdapSigning);
        Assert.Equal(original.SendEmailManual, loaded.SendEmailManual);
        Assert.Equal(original.SendEmailScheduled, loaded.SendEmailScheduled);
    }

    [Fact]
    public void SaveSettings_CalledTwice_OverwritesPrevious()
    {
        var store = CreateStore();

        store.SaveSettings(new PersistedAppSettings { DomainControllers = "dc01" });
        store.SaveSettings(new PersistedAppSettings { DomainControllers = "dc02" });

        var loaded = store.LoadSettings();
        Assert.Equal("dc02", loaded.DomainControllers);
    }

    // ── History roundtrip ─────────────────────────────────────────────────

    [Fact]
    public void LoadHistory_EmptyDatabase_ReturnsEmptyList()
    {
        var store = CreateStore();

        var history = store.LoadHistory();

        Assert.NotNull(history);
        Assert.Empty(history);
    }

    [Fact]
    public void SaveHistory_ThenLoad_Roundtrips()
    {
        var store = CreateStore();
        var entries = new List<TestHistoryEntry>
        {
            new()
            {
                RunDate = new DateTime(2025, 6, 1, 10, 0, 0, DateTimeKind.Local),
                Total = 20,
                Passed = 18,
                Failed = 2,
                Details = "DNS check failed on dc01",
                LogFilePath = @"C:\logs\run1.log",
                TestType = "Manual"
            },
            new()
            {
                RunDate = new DateTime(2025, 5, 31, 22, 0, 0, DateTimeKind.Local),
                Total = 20,
                Passed = 20,
                Failed = 0,
                Details = "All tests passed",
                LogFilePath = @"C:\logs\run2.log",
                TestType = "Scheduled"
            }
        };

        store.SaveHistory(entries);
        var loaded = store.LoadHistory();

        Assert.Equal(2, loaded.Count);
        // Results are ordered by RunDateTicks DESC
        Assert.Equal(new DateTime(2025, 6, 1, 10, 0, 0, DateTimeKind.Local), loaded[0].RunDate);
        Assert.Equal(20, loaded[0].Total);
        Assert.Equal(18, loaded[0].Passed);
        Assert.Equal(2, loaded[0].Failed);
        Assert.Equal("DNS check failed on dc01", loaded[0].Details);
        Assert.Equal(@"C:\logs\run1.log", loaded[0].LogFilePath);
        Assert.Equal("Manual", loaded[0].TestType);

        Assert.Equal(new DateTime(2025, 5, 31, 22, 0, 0, DateTimeKind.Local), loaded[1].RunDate);
        Assert.Equal(20, loaded[1].Passed);
        Assert.Equal(0, loaded[1].Failed);
        Assert.Equal("Scheduled", loaded[1].TestType);
    }

    [Fact]
    public void SaveHistory_OverwritesPreviousEntries()
    {
        var store = CreateStore();

        store.SaveHistory(new List<TestHistoryEntry>
        {
            new() { RunDate = DateTime.Now, Total = 5, Passed = 5, Details = "first" }
        });

        store.SaveHistory(new List<TestHistoryEntry>
        {
            new() { RunDate = DateTime.Now, Total = 10, Passed = 8, Details = "second" }
        });

        var loaded = store.LoadHistory();
        Assert.Single(loaded);
        Assert.Equal("second", loaded[0].Details);
    }

    [Fact]
    public void SaveHistory_EmptyList_ClearsHistory()
    {
        var store = CreateStore();

        store.SaveHistory(new List<TestHistoryEntry>
        {
            new() { RunDate = DateTime.Now, Total = 5, Passed = 5 }
        });

        store.SaveHistory(new List<TestHistoryEntry>());

        var loaded = store.LoadHistory();
        Assert.Empty(loaded);
    }

    [Fact]
    public void SaveHistory_HandlesNullStrings()
    {
        var store = CreateStore();
        var entries = new List<TestHistoryEntry>
        {
            new()
            {
                RunDate = DateTime.Now,
                Total = 1,
                Passed = 1,
                Failed = 0,
                Details = null!,
                LogFilePath = null!,
                TestType = null!
            }
        };

        store.SaveHistory(entries);
        var loaded = store.LoadHistory();

        Assert.Single(loaded);
        Assert.Equal(string.Empty, loaded[0].Details);
        Assert.Equal(string.Empty, loaded[0].LogFilePath);
        Assert.Equal(string.Empty, loaded[0].TestType);
    }

    // ── Dashboard Snapshot roundtrip ──────────────────────────────────────

    [Fact]
    public void LoadDashboardSnapshot_EmptyDatabase_ReturnsNull()
    {
        var store = CreateStore();

        var snapshot = store.LoadDashboardSnapshot();

        Assert.Null(snapshot);
    }

    [Fact]
    public void SaveDashboardSnapshot_ThenLoad_Roundtrips()
    {
        var store = CreateStore();
        var original = new DashboardSnapshot
        {
            CapturedAtUtc = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            HealthScore = 85,
            CriticalFindings = 2,
            PassingTests = 18,
            ConfiguredDomainControllers = 3,
            TotalRuns = 42,
            LastRunSummary = "18/20 passed",
            FindingsCriticalCount = 2,
            FindingsHighCount = 3,
            FindingsMediumCount = 5,
            FindingsLowCount = 1,
            LastRunPassed = 18,
            LastRunFailed = 2,
            LastRunTotal = 20
        };

        store.SaveDashboardSnapshot(original);
        var loaded = store.LoadDashboardSnapshot();

        Assert.NotNull(loaded);
        Assert.Equal(original.CapturedAtUtc, loaded.CapturedAtUtc);
        Assert.Equal(85, loaded.HealthScore);
        Assert.Equal(2, loaded.CriticalFindings);
        Assert.Equal(18, loaded.PassingTests);
        Assert.Equal(3, loaded.ConfiguredDomainControllers);
        Assert.Equal(42, loaded.TotalRuns);
        Assert.Equal("18/20 passed", loaded.LastRunSummary);
        Assert.Equal(2, loaded.FindingsCriticalCount);
        Assert.Equal(3, loaded.FindingsHighCount);
        Assert.Equal(5, loaded.FindingsMediumCount);
        Assert.Equal(1, loaded.FindingsLowCount);
        Assert.Equal(18, loaded.LastRunPassed);
        Assert.Equal(2, loaded.LastRunFailed);
        Assert.Equal(20, loaded.LastRunTotal);
    }

    [Fact]
    public void SaveDashboardSnapshot_CalledTwice_OverwritesPrevious()
    {
        var store = CreateStore();

        store.SaveDashboardSnapshot(new DashboardSnapshot { HealthScore = 50 });
        store.SaveDashboardSnapshot(new DashboardSnapshot { HealthScore = 90 });

        var loaded = store.LoadDashboardSnapshot();
        Assert.NotNull(loaded);
        Assert.Equal(90, loaded.HealthScore);
    }

    // ── Scheduled Tasks roundtrip ─────────────────────────────────────────

    [Fact]
    public void LoadScheduledTasks_EmptyDatabase_ReturnsEmptyList()
    {
        var store = CreateStore();

        var tasks = store.LoadScheduledTasks();

        Assert.NotNull(tasks);
        Assert.Empty(tasks);
    }

    [Fact]
    public void SaveScheduledTasks_ThenLoad_Roundtrips()
    {
        var store = CreateStore();
        var tasks = new List<ScheduledTask>
        {
            new()
            {
                TaskName = "Nightly Health Check",
                DomainController = "dc01.corp.local",
                Frequency = "Daily",
                StartDate = new DateTime(2025, 6, 1),
                StartTime = "22:00"
            },
            new()
            {
                TaskName = "Weekly Replication",
                DomainController = "dc01.corp.local,dc02.corp.local",
                Frequency = "Weekly",
                StartDate = new DateTime(2025, 6, 2),
                StartTime = "06:00"
            }
        };

        store.SaveScheduledTasks(tasks);
        var loaded = store.LoadScheduledTasks();

        Assert.Equal(2, loaded.Count);
        Assert.Equal("Nightly Health Check", loaded[0].TaskName);
        Assert.Equal("dc01.corp.local", loaded[0].DomainController);
        Assert.Equal("Daily", loaded[0].Frequency);
        Assert.Equal("Weekly Replication", loaded[1].TaskName);
    }

    [Fact]
    public void SaveScheduledTasks_OverwritesPrevious()
    {
        var store = CreateStore();

        store.SaveScheduledTasks(new List<ScheduledTask> { new() { TaskName = "Old" } });
        store.SaveScheduledTasks(new List<ScheduledTask> { new() { TaskName = "New" } });

        var loaded = store.LoadScheduledTasks();
        Assert.Single(loaded);
        Assert.Equal("New", loaded[0].TaskName);
    }

    // ── LoadStartupState ──────────────────────────────────────────────────

    [Fact]
    public void LoadStartupState_EmptyDatabase_ReturnsDefaults()
    {
        var store = CreateStore();

        var state = store.LoadStartupState();

        Assert.NotNull(state);
        Assert.NotNull(state.Settings);
        Assert.Equal(string.Empty, state.Settings.DomainControllers);
        Assert.Null(state.DashboardSnapshot);
        Assert.Empty(state.History);
        Assert.Empty(state.ScheduledTasks);
    }

    [Fact]
    public void LoadStartupState_WithData_ReturnsAllDocuments()
    {
        var store = CreateStore();

        store.SaveSettings(new PersistedAppSettings { DomainControllers = "dc01" });
        store.SaveDashboardSnapshot(new DashboardSnapshot { HealthScore = 95 });
        store.SaveHistory(new List<TestHistoryEntry> { new() { Total = 10, Passed = 10 } });
        store.SaveScheduledTasks(new List<ScheduledTask> { new() { TaskName = "Daily" } });

        var state = store.LoadStartupState();

        Assert.Equal("dc01", state.Settings.DomainControllers);
        Assert.NotNull(state.DashboardSnapshot);
        Assert.Equal(95, state.DashboardSnapshot.HealthScore);
        Assert.Single(state.History);
        Assert.Equal(10, state.History[0].Total);
        Assert.Single(state.ScheduledTasks);
        Assert.Equal("Daily", state.ScheduledTasks[0].TaskName);
    }

    // ── Separate instances ────────────────────────────────────────────────

    [Fact]
    public void DifferentInstances_SamePath_ShareData()
    {
        var store1 = CreateStore();
        store1.SaveSettings(new PersistedAppSettings { DomainControllers = "shared" });

        var store2 = CreateStore();
        var loaded = store2.LoadSettings();

        Assert.Equal("shared", loaded.DomainControllers);
    }

    [Fact]
    public void DifferentPaths_HaveIndependentData()
    {
        string path2 = Path.Combine(testDirectory, "test2.db");
        var store1 = CreateStore();
        var store2 = new AppStateStore(path2);

        store1.SaveSettings(new PersistedAppSettings { DomainControllers = "store1" });
        store2.SaveSettings(new PersistedAppSettings { DomainControllers = "store2" });

        Assert.Equal("store1", store1.LoadSettings().DomainControllers);
        Assert.Equal("store2", store2.LoadSettings().DomainControllers);
    }

    // ── Large data ────────────────────────────────────────────────────────

    [Fact]
    public void SaveHistory_HandlesLargeNumberOfEntries()
    {
        var store = CreateStore();
        var entries = new List<TestHistoryEntry>();
        for (int i = 0; i < 1000; i++)
        {
            entries.Add(new TestHistoryEntry
            {
                RunDate = DateTime.Now.AddDays(-i),
                Total = 20,
                Passed = 20 - (i % 3),
                Failed = i % 3,
                Details = $"Run {i}",
                LogFilePath = $@"C:\logs\run{i}.log",
                TestType = i % 2 == 0 ? "Manual" : "Scheduled"
            });
        }

        store.SaveHistory(entries);
        var loaded = store.LoadHistory();

        Assert.Equal(1000, loaded.Count);
    }

    [Fact]
    public void SaveDashboardSnapshot_HandlesLargeJsonPayload()
    {
        var store = CreateStore();
        var snapshot = new DashboardSnapshot
        {
            LastRunSummary = new string('X', 10000) // Large string
        };

        store.SaveDashboardSnapshot(snapshot);
        var loaded = store.LoadDashboardSnapshot();

        Assert.NotNull(loaded);
        Assert.Equal(10000, loaded.LastRunSummary.Length);
    }
}
