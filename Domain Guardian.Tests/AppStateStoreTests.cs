using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        Assert.False(settings.TestDnsCheck);
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

    [Fact]
    public void LoadStartupState_WithHistoryLimit_LoadsMostRecentEntriesOnly()
    {
        var store = CreateStore();
        DateTime baseDate = new(2026, 1, 1);

        store.SaveHistory(Enumerable.Range(0, 5)
            .Select(index => new TestHistoryEntry
            {
                RunDate = baseDate.AddDays(index),
                Total = index + 1,
                Passed = index + 1
            })
            .ToList());

        var state = store.LoadStartupState(historyLimit: 2);

        Assert.Equal(2, state.History.Count);
        Assert.Equal(baseDate.AddDays(4), state.History[0].RunDate);
        Assert.Equal(baseDate.AddDays(3), state.History[1].RunDate);
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

    // ── Special characters ────────────────────────────────────────────────

    [Fact]
    public void SaveSettings_SpecialCharacters_Roundtrips()
    {
        var store = CreateStore();
        var original = new PersistedAppSettings
        {
            DomainControllers = "dc\"01\".corp.local, dc\\02",
            RecipientEmail = "admin+josé@corp.local"
        };

        store.SaveSettings(original);
        var loaded = store.LoadSettings();

        Assert.Equal(original.DomainControllers, loaded.DomainControllers);
        Assert.Equal(original.RecipientEmail, loaded.RecipientEmail);
    }

    [Fact]
    public void SaveSettings_UnicodePath_Roundtrips()
    {
        var store = CreateStore();
        var original = new PersistedAppSettings
        {
            DomainControllers = "东京.corp.local, Zürich.dc"
        };

        store.SaveSettings(original);
        var loaded = store.LoadSettings();

        Assert.Equal("东京.corp.local, Zürich.dc", loaded.DomainControllers);
    }

    [Fact]
    public void SaveHistory_SpecialCharactersInPaths_Roundtrips()
    {
        var store = CreateStore();
        var entries = new List<TestHistoryEntry>
        {
            new()
            {
                RunDate = DateTime.Now,
                Total = 1,
                Passed = 1,
                LogFilePath = @"C:\Users\José García\logs\run with spaces.log",
                Details = "Test with \"quotes\" and \\backslashes\\"
            },
            new()
            {
                RunDate = DateTime.Now,
                Total = 1,
                Passed = 1,
                LogFilePath = @"C:\logs\测试\日本語.log",
                Details = "Unicode: café résumé naïve"
            }
        };

        store.SaveHistory(entries);
        var loaded = store.LoadHistory();

        Assert.Equal(2, loaded.Count);
        var allLogPaths = loaded.Select(e => e.LogFilePath).ToList();
        var allDetails = loaded.Select(e => e.Details).ToList();
        Assert.Contains(allLogPaths, p => p.Contains("José García"));
        Assert.Contains(allLogPaths, p => p.Contains("日本語"));
        Assert.Contains(allDetails, d => d.Contains("quotes"));
        Assert.Contains(allDetails, d => d.Contains("café résumé"));
    }

    // ── DateTime kind preservation ────────────────────────────────────────

    [Fact]
    public void SaveHistory_LocalDateTime_PreservesTicksOnLoad()
    {
        var store = CreateStore();
        var localDate = new DateTime(2025, 6, 1, 14, 30, 0, DateTimeKind.Local);
        var entries = new List<TestHistoryEntry>
        {
            new() { RunDate = localDate, Total = 1, Passed = 1 }
        };

        store.SaveHistory(entries);
        var loaded = store.LoadHistory();

        Assert.Single(loaded);
        Assert.Equal(localDate.Ticks, loaded[0].RunDate.Ticks);
        Assert.Equal(DateTimeKind.Local, loaded[0].RunDate.Kind);
    }

    [Fact]
    public void SaveHistory_UtcDateTime_LoadsAsLocal()
    {
        var store = CreateStore();
        var utcDate = new DateTime(2025, 6, 1, 14, 30, 0, DateTimeKind.Utc);
        var entries = new List<TestHistoryEntry>
        {
            new() { RunDate = utcDate, Total = 1, Passed = 1 }
        };

        store.SaveHistory(entries);
        var loaded = store.LoadHistory();

        Assert.Single(loaded);
        // Store preserves ticks, but reconstructs as Local kind
        Assert.Equal(utcDate.Ticks, loaded[0].RunDate.Ticks);
        Assert.Equal(DateTimeKind.Local, loaded[0].RunDate.Kind);
    }

    [Fact]
    public void SaveDashboardSnapshot_UtcDateTime_PreservesTicks()
    {
        var store = CreateStore();
        var utcDate = new DateTime(2025, 12, 31, 23, 59, 59, DateTimeKind.Utc);
        var snapshot = new DashboardSnapshot { CapturedAtUtc = utcDate, HealthScore = 77 };

        store.SaveDashboardSnapshot(snapshot);
        var loaded = store.LoadDashboardSnapshot();

        Assert.NotNull(loaded);
        Assert.Equal(utcDate.Ticks, loaded.CapturedAtUtc.Ticks);
        Assert.Equal(77, loaded.HealthScore);
    }

    // ── Startup initialization timing ────────────────────────────────────

    [Fact]
    public void LoadStartupState_ColdStart_CompletesWithinThreshold()
    {
        // Arrange: seed a database with realistic startup data to simulate
        // a user who has been using the app for a while.
        var seedStore = CreateStore();

        seedStore.SaveSettings(new PersistedAppSettings
        {
            DomainControllers = "dc01.corp.local,dc02.corp.local,dc03.corp.local",
            RecipientEmail = "admin@corp.local",
            TestDnsCheck = true,
            TestReplication = true,
            TestTimeSkew = true,
            TestLdapBind = true,
            TestCertDhcp = false,
            TestSmbLdapSigning = true,
            SendEmailManual = true,
            SendEmailScheduled = true
        });

        seedStore.SaveDashboardSnapshot(new DashboardSnapshot
        {
            CapturedAtUtc = DateTime.UtcNow,
            HealthScore = 87,
            CriticalFindings = 1,
            PassingTests = 17,
            ConfiguredDomainControllers = 3,
            TotalRuns = 42,
            LastRunSummary = "17/20 passed",
            FindingsCriticalCount = 1,
            FindingsHighCount = 2,
            FindingsMediumCount = 4,
            FindingsLowCount = 1,
            LastRunPassed = 17,
            LastRunFailed = 3,
            LastRunTotal = 20
        });

        var historyEntries = Enumerable.Range(0, 50)
            .Select(i => new TestHistoryEntry
            {
                RunDate = DateTime.Now.AddDays(-i),
                Total = 20,
                Passed = 20 - (i % 4),
                Failed = i % 4,
                Details = $"Run {i}: {20 - (i % 4)}/20 passed",
                LogFilePath = $@"{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AdHealthMonitor", "Logs", "runs", $"2026-06-{(i + 1):D2}", $"run{i}.txt")}",
                TestType = i % 3 == 0 ? "Scheduled" : "Manual"
            })
            .ToList();
        seedStore.SaveHistory(historyEntries);

        seedStore.SaveScheduledTasks(new List<ScheduledTask>
        {
            new() { TaskName = "Nightly Health Check", DomainController = "dc01.corp.local", Frequency = "Daily", StartDate = DateTime.Today, StartTime = "22:00" },
            new() { TaskName = "Weekly Full Scan", DomainController = "dc01.corp.local,dc02.corp.local", Frequency = "Weekly", StartDate = DateTime.Today, StartTime = "06:00" }
        });

        // Act: measure the full cold-start LoadStartupState call,
        // which includes Initialize() + all data loading.
        // Use a separate database path so Initialize() actually runs DDL
        // (AppStateStore tracks initialized paths in a static set and
        // short-circuits on repeat calls for the same path).
        // Clear the pool first to flush WAL data into the main .db file.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        string coldStartDbPath = Path.Combine(testDirectory, "coldstart.db");
        File.Copy(databasePath, coldStartDbPath, overwrite: true);
        // Also copy WAL/SHM sidecar files if present.
        foreach (string suffix in new[] { "-wal", "-shm" })
        {
            string sidecar = databasePath + suffix;
            if (File.Exists(sidecar))
                File.Copy(sidecar, coldStartDbPath + suffix, overwrite: true);
        }
        var startupStore = new AppStateStore(coldStartDbPath);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var state = startupStore.LoadStartupState(historyLimit: 10);
        stopwatch.Stop();

        // Assert: startup data load must complete within a reasonable threshold.
        // On modern hardware this typically takes <200ms; 2000ms catches regressions
        // without being flaky on CI or slower machines.
        Assert.True(stopwatch.ElapsedMilliseconds < 2000,
            $"LoadStartupState took {stopwatch.ElapsedMilliseconds}ms, expected <2000ms");

        // Verify the loaded state is complete and correct
        Assert.Equal("dc01.corp.local,dc02.corp.local,dc03.corp.local", state.Settings.DomainControllers);
        Assert.NotNull(state.DashboardSnapshot);
        Assert.Equal(87, state.DashboardSnapshot.HealthScore);
        Assert.True(state.History.Count <= 10, "History limit should be respected");
        Assert.NotEmpty(state.ScheduledTasks);
    }

    // ── Concurrent access ─────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentWrites_DoNotCorruptDatabase()
    {
        var store = CreateStore();
        store.Initialize();

        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            int taskId = i;
            tasks.Add(Task.Run(() =>
            {
                store.SaveHistory(new List<TestHistoryEntry>
                {
                    new()
                    {
                        RunDate = DateTime.Now,
                        Total = taskId,
                        Passed = taskId,
                        Details = $"Concurrent write {taskId}"
                    }
                });
            }));
        }

        await Task.WhenAll(tasks);

        // Should not throw and should return valid data
        var loaded = store.LoadHistory();
        Assert.NotNull(loaded);
        Assert.NotEmpty(loaded); // SaveHistory overwrites, concurrent writes may leave 1+ entries
    }

    [Fact]
    public async Task ConcurrentReadAndWrite_DoNotThrow()
    {
        var store = CreateStore();
        store.Initialize();

        // Seed initial data
        store.SaveHistory(new List<TestHistoryEntry>
        {
            new() { RunDate = DateTime.Now, Total = 5, Passed = 5, Details = "seed" }
        });

        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            int taskId = i;
            tasks.Add(Task.Run(() =>
            {
                // Mix reads and writes
                if (taskId % 2 == 0)
                {
                    store.SaveHistory(new List<TestHistoryEntry>
                    {
                        new() { RunDate = DateTime.Now, Total = taskId, Passed = taskId, Details = $"write {taskId}" }
                    });
                }
                else
                {
                    var history = store.LoadHistory();
                    Assert.NotNull(history);
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Verify final state is consistent (may be 1+ entries due to concurrent DELETE+INSERT)
        var final = store.LoadHistory();
        Assert.NotNull(final);
        Assert.NotEmpty(final);
    }

    [Fact]
    public async Task ConcurrentSettingsAccess_DoNotThrow()
    {
        var store = CreateStore();
        store.Initialize();

        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            int taskId = i;
            tasks.Add(Task.Run(() =>
            {
                if (taskId % 2 == 0)
                {
                    store.SaveSettings(new PersistedAppSettings { DomainControllers = $"dc{taskId}" });
                }
                else
                {
                    var settings = store.LoadSettings();
                    Assert.NotNull(settings);
                }
            }));
        }

        await Task.WhenAll(tasks);

        var final = store.LoadSettings();
        Assert.NotNull(final);
    }

    // ── SQLite startup performance ────────────────────────────────────────

    [Fact]
    public void Initialize_ColdStart_CompletesWithinThreshold()
    {
        // Measure the full cold-start Initialize() including DDL + PRAGMAs.
        // Uses a fresh database path so the volatile fast-path doesn't short-circuit.
        string coldPath = Path.Combine(testDirectory, "cold_init.db");
        var store = new AppStateStore(coldPath);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        store.Initialize();
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 1000,
            $"Initialize() took {stopwatch.ElapsedMilliseconds}ms, expected <1000ms");
        Assert.True(File.Exists(coldPath), "Database file should exist after Initialize()");
    }

    [Fact]
    public void Initialize_SecondCall_IsNearInstant()
    {
        // The volatile fast-path + HashSet guard should make repeat calls
        // complete in <1ms. This verifies the fast-path isn't regressing.
        var store = CreateStore();
        store.Initialize(); // Warm up

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            store.Initialize();
        }
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 100,
            $"1000x repeat Initialize() took {stopwatch.ElapsedMilliseconds}ms, expected <100ms");
    }

    [Fact]
    public void Initialize_SetsWalJournalMode()
    {
        // WAL mode is critical for concurrent read/write performance.
        // Verify it was set during Initialize().
        var store = CreateStore();
        store.Initialize();

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Shared
            }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        string journalMode = (string)cmd.ExecuteScalar()!;

        Assert.Equal("wal", journalMode);
    }

    [Fact]
    public void Initialize_CreatesAllRequiredTables()
    {
        var store = CreateStore();
        store.Initialize();

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Shared
            }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
        var tables = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) tables.Add(reader.GetString(0));

        Assert.Contains("TestHistory", tables);
        Assert.Contains("DashboardSnapshot", tables);
        Assert.Contains("AppDocuments", tables);
    }

    [Fact]
    public void Initialize_CreatesTestHistoryIndex()
    {
        var store = CreateStore();
        store.Initialize();

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Shared
            }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name='IX_TestHistory_RunDateTicks';";
        string? indexName = cmd.ExecuteScalar() as string;

        Assert.NotNull(indexName);
        Assert.Equal("IX_TestHistory_RunDateTicks", indexName);
    }

    [Fact]
    public void LoadStartupState_FullDataRoundtrip_CompletesWithinThreshold()
    {
        // Simulate a mature user database with 200 history entries,
        // dashboard snapshot, settings, and scheduled tasks.
        // Then measure a cold LoadStartupState (historyLimit=10).
        var seedStore = CreateStore();
        seedStore.SaveSettings(new PersistedAppSettings
        {
            DomainControllers = string.Join(",", Enumerable.Range(1, 10).Select(i => $"dc{i:D2}.corp.local")),
            RecipientEmail = "admin@corp.local",
            TestDnsCheck = true, TestReplication = true, TestTimeSkew = true
        });
        seedStore.SaveDashboardSnapshot(new DashboardSnapshot
        {
            CapturedAtUtc = DateTime.UtcNow,
            HealthScore = 82,
            CriticalFindings = 3,
            PassingTests = 15,
            ConfiguredDomainControllers = 10,
            TotalRuns = 200
        });
        seedStore.SaveHistory(Enumerable.Range(0, 200).Select(i => new TestHistoryEntry
        {
            RunDate = DateTime.Now.AddDays(-i),
            Total = 20, Passed = 20 - (i % 5), Failed = i % 5,
            Details = $"Run {i}",
            LogFilePath = $@"C:\logs\run{i}.txt",
            TestType = i % 3 == 0 ? "Scheduled" : "Manual"
        }).ToList());
        seedStore.SaveScheduledTasks(Enumerable.Range(0, 5).Select(i => new ScheduledTask
        {
            TaskName = $"Task {i}",
            DomainController = $"dc{i:D2}.corp.local",
            Frequency = "Daily"
        }).ToList());

        // Cold-start path
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        string coldPath = Path.Combine(testDirectory, "cold_full.db");
        File.Copy(databasePath, coldPath, overwrite: true);
        foreach (string suffix in new[] { "-wal", "-shm" })
        {
            string sidecar = databasePath + suffix;
            if (File.Exists(sidecar)) File.Copy(sidecar, coldPath + suffix, overwrite: true);
        }

        var coldStore = new AppStateStore(coldPath);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var state = coldStore.LoadStartupState(historyLimit: 10);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 2000,
            $"Cold LoadStartupState with 200 entries took {stopwatch.ElapsedMilliseconds}ms, expected <2000ms");
        Assert.Equal(10, state.History.Count);
        Assert.NotNull(state.DashboardSnapshot);
        Assert.Equal(5, state.ScheduledTasks.Count);
        Assert.Equal(82, state.DashboardSnapshot.HealthScore);
    }

    [Fact]
    public void LoadSettings_ColdStart_CompletesWithinThreshold()
    {
        var seedStore = CreateStore();
        seedStore.SaveSettings(new PersistedAppSettings
        {
            DomainControllers = "dc01.corp.local,dc02.corp.local",
            RecipientEmail = "admin@corp.local"
        });

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        string coldPath = Path.Combine(testDirectory, "cold_settings.db");
        File.Copy(databasePath, coldPath, overwrite: true);
        foreach (string suffix in new[] { "-wal", "-shm" })
        {
            string sidecar = databasePath + suffix;
            if (File.Exists(sidecar)) File.Copy(sidecar, coldPath + suffix, overwrite: true);
        }

        var coldStore = new AppStateStore(coldPath);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var settings = coldStore.LoadSettings();
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 1000,
            $"Cold LoadSettings took {stopwatch.ElapsedMilliseconds}ms, expected <1000ms");
        Assert.Equal("dc01.corp.local,dc02.corp.local", settings.DomainControllers);
    }

    [Fact]
    public void SaveHistory_ThenLoad_LargeDataset_CompletesWithinThreshold()
    {
        var store = CreateStore();
        var entries = Enumerable.Range(0, 500).Select(i => new TestHistoryEntry
        {
            RunDate = DateTime.Now.AddDays(-i),
            Total = 20, Passed = 20 - (i % 5), Failed = i % 5,
            Details = $"Run {i}: detailed result text for performance testing",
            LogFilePath = $@"{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AdHealthMonitor", "Logs", "runs", $"2026-{(i / 30 + 1):D2}-{(i % 30 + 1):D2}", $"run{i}.txt")}",
            TestType = i % 3 == 0 ? "Scheduled" : "Manual"
        }).ToList();

        var swSave = System.Diagnostics.Stopwatch.StartNew();
        store.SaveHistory(entries);
        swSave.Stop();

        var swLoad = System.Diagnostics.Stopwatch.StartNew();
        var loaded = store.LoadHistory();
        swLoad.Stop();

        Assert.True(swSave.ElapsedMilliseconds < 2000,
            $"SaveHistory(500) took {swSave.ElapsedMilliseconds}ms, expected <2000ms");
        Assert.True(swLoad.ElapsedMilliseconds < 1000,
            $"LoadHistory(500) took {swLoad.ElapsedMilliseconds}ms, expected <1000ms");
        Assert.Equal(500, loaded.Count);
    }

    [Fact]
    public void SaveDashboardSnapshot_JsonSerialization_HandlesLargePayload()
    {
        // Verify large JSON payloads (e.g. detailed findings) don't crash or hang.
        var store = CreateStore();
        var snapshot = new DashboardSnapshot
        {
            CapturedAtUtc = DateTime.UtcNow,
            HealthScore = 75,
            LastRunSummary = new string('A', 50_000) // 50KB string
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        store.SaveDashboardSnapshot(snapshot);
        var loaded = store.LoadDashboardSnapshot();
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"Large snapshot roundtrip took {sw.ElapsedMilliseconds}ms, expected <1000ms");
        Assert.NotNull(loaded);
        Assert.Equal(50_000, loaded.LastRunSummary.Length);
    }

    [Fact]
    public void ConcurrentInitialize_MultiplePaths_DoNotDeadlock()
    {
        // Verify that concurrent Initialize() calls for different database
        // paths don't deadlock on the shared InitializationLock.
        var tasks = new List<Task>();
        var stores = new List<AppStateStore>();
        for (int i = 0; i < 5; i++)
        {
            string path = Path.Combine(testDirectory, $"concurrent_{i}.db");
            var store = new AppStateStore(path);
            stores.Add(store);
            int idx = i;
            tasks.Add(Task.Run(() =>
            {
                store.Initialize();
                store.SaveSettings(new PersistedAppSettings { DomainControllers = $"dc{idx}" });
            }));
        }

        bool completed = Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(10));
        Assert.True(completed, "Concurrent Initialize() calls should not deadlock");

        // Verify all stores wrote their data
        for (int i = 0; i < 5; i++)
        {
            var settings = stores[i].LoadSettings();
            Assert.Equal($"dc{i}", settings.DomainControllers);
        }
    }

    [Fact]
    public void Initialize_SetsPerformancePragmas()
    {
        // Verify the key performance PRAGMAs (mmap_size, cache_size) are
        // applied during Initialize(). These were added to speed up reads.
        var store = CreateStore();
        store.Initialize();

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Shared
            }.ToString());
        connection.Open();

        // Note: mmap_size and cache_size are persisted per-database (cache_size
        // since SQLite 3.32+, mmap_size since 3.39+), so they can be verified on
        // a fresh connection. synchronous and temp_store are per-connection only.
        // synchronous and temp_store are per-connection and not persisted,
        // so they cannot be tested this way.
        using var mmapCmd = connection.CreateCommand();
        mmapCmd.CommandText = "PRAGMA mmap_size;";
        long mmapSize = (long)mmapCmd.ExecuteScalar()!;
        Assert.True(mmapSize > 0, $"mmap_size should be >0, got {mmapSize}");

        using var cacheCmd = connection.CreateCommand();
        cacheCmd.CommandText = "PRAGMA cache_size;";
        long cacheSize = (long)cacheCmd.ExecuteScalar()!;
        // cache_size=-8000 means 8MB; SQLite returns the negative value.
        Assert.True(cacheSize != 0, $"cache_size should be non-zero, got {cacheSize}");
    }

    [Fact]
    public void LoadStartupState_AfterSaveAndReopen_DataIsConsistent()
    {
        // Simulate the real startup flow: save data, close pool (flush WAL),
        // open a new store on the same file, and verify all data survives.
        var store = CreateStore();
        store.SaveSettings(new PersistedAppSettings { DomainControllers = "dc01" });
        store.SaveDashboardSnapshot(new DashboardSnapshot { HealthScore = 90 });
        store.SaveHistory(new List<TestHistoryEntry>
        {
            new() { RunDate = DateTime.Now, Total = 20, Passed = 20 }
        });
        store.SaveScheduledTasks(new List<ScheduledTask>
        {
            new() { TaskName = "Daily", DomainController = "dc01" }
        });

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // New store instance simulates app restart
        var store2 = CreateStore();
        var state = store2.LoadStartupState();

        Assert.Equal("dc01", state.Settings.DomainControllers);
        Assert.NotNull(state.DashboardSnapshot);
        Assert.Equal(90, state.DashboardSnapshot.HealthScore);
        Assert.Single(state.History);
        Assert.Equal(20, state.History[0].Passed);
        Assert.Single(state.ScheduledTasks);
        Assert.Equal("Daily", state.ScheduledTasks[0].TaskName);
    }
}
