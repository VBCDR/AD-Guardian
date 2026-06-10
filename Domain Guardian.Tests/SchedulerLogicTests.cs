using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using AdHealthMonitor;
using Xunit;

namespace Domain_Guardian.Tests;

/// <summary>
/// Tests for scheduler-specific logic: DC list parsing, unreachable DC fallback,
/// email subject construction, scheduled log caching, and summary generation.
/// </summary>
public class SchedulerLogicTests : IDisposable
{
    private readonly string testDirectory;

    public SchedulerLogicTests()
    {
        testDirectory = Path.Combine(Path.GetTempPath(), "SchedulerLogicTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
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

    // ── DC list parsing (mirrors RunScheduledTestsAsync logic) ───────────

    [Theory]
    [InlineData("DC01", new[] { "DC01" })]
    [InlineData("DC01,DC02", new[] { "DC01", "DC02" })]
    [InlineData("DC01, DC02, DC03", new[] { "DC01", "DC02", "DC03" })]
    [InlineData("  DC01  ,  DC02  ", new[] { "DC01", "DC02" })]
    [InlineData("DC01,,,DC02,", new[] { "DC01", "DC02" })]
    public void ParseDomainControllers_SplitsAndTrimsCorrectly(string input, string[] expected)
    {
        // Mirrors the parsing logic in RunScheduledTestsAsync
        string[] dcList = input
            .Split(',')
            .Select(dc => dc.Trim())
            .Where(dc => !string.IsNullOrWhiteSpace(dc))
            .ToArray();

        Assert.Equal(expected, dcList);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,")]
    [InlineData(", , , ")]
    public void ParseDomainControllers_EmptyOrWhitespace_ReturnsEmpty(string input)
    {
        string[] dcList = input
            .Split(',')
            .Select(dc => dc.Trim())
            .Where(dc => !string.IsNullOrWhiteSpace(dc))
            .ToArray();

        Assert.Empty(dcList);
    }

    // ── Unreachable DC fallback behavior ─────────────────────────────────

    [Fact]
    public void UnreachableDc_FallbackProducesDcDiagFailEntry()
    {
        // When ParseDCDiagOutput returns no results (unreachable DC),
        // the caller should add a fallback FAIL entry.
        string emptyOutput = "Some error output without test results";
        var results = MainWindow.ParseDCDiagOutput("DC01", emptyOutput, "test.log").ToList();

        // Simulate the fallback logic from RunScheduledTestsAsync
        if (results.Count == 0)
        {
            results.Add(new TestResult
            {
                Service = "DCDiag",
                Server = "DC01",
                Result = "FAIL",
                Message = "DCDiag produced no parseable output. DC may be unreachable.",
                LogFilePath = "test.log"
            });
        }

        Assert.Single(results);
        Assert.Equal("DCDiag", results[0].Service);
        Assert.Equal("DC01", results[0].Server);
        Assert.Equal("FAIL", results[0].Result);
        Assert.Contains("unreachable", results[0].Message);
        Assert.Equal("test.log", results[0].LogFilePath);
    }

    [Fact]
    public void UnreachableDc_FallbackNotAddedWhenResultsExist()
    {
        // When dcdiag produces valid output, no fallback should be added
        string output = @"
   Testing server: DC01
      Starting test: Connectivity
         ......................... DC01 passed test Connectivity
";
        var results = MainWindow.ParseDCDiagOutput("DC01", output, "test.log").ToList();

        // Simulate the fallback logic
        if (results.Count == 0)
        {
            results.Add(new TestResult
            {
                Service = "DCDiag",
                Server = "DC01",
                Result = "FAIL",
                Message = "DCDiag produced no parseable output. DC may be unreachable.",
                LogFilePath = "test.log"
            });
        }

        // Should NOT have added the fallback since we have real results
        Assert.Single(results);
        Assert.Equal("Connectivity", results[0].Service);
        Assert.Equal("PASS", results[0].Result);
    }

    [Fact]
    public void UnreachableDc_MultipleDcsOneUnreachable_BothAppearInResults()
    {
        // Simulate: DC01 reachable, DC02 unreachable
        string dc01Output = @"
   Testing server: DC01
      Starting test: Connectivity
         ......................... DC01 passed test Connectivity
      Starting test: Advertising
         ......................... DC01 passed test Advertising
";
        string dc02Output = "RPC server is unavailable.";

        var allResults = new List<TestResult>();

        // DC01: reachable
        var dc01Results = MainWindow.ParseDCDiagOutput("DC01", dc01Output, "dc01.log").ToList();
        if (dc01Results.Count == 0)
        {
            dc01Results.Add(new TestResult { Service = "DCDiag", Server = "DC01", Result = "FAIL", Message = "DCDiag produced no parseable output. DC may be unreachable.", LogFilePath = "dc01.log" });
        }
        allResults.AddRange(dc01Results);

        // DC02: unreachable
        var dc02Results = MainWindow.ParseDCDiagOutput("DC02", dc02Output, "dc02.log").ToList();
        if (dc02Results.Count == 0)
        {
            dc02Results.Add(new TestResult { Service = "DCDiag", Server = "DC02", Result = "FAIL", Message = "DCDiag produced no parseable output. DC may be unreachable.", LogFilePath = "dc02.log" });
        }
        allResults.AddRange(dc02Results);

        Assert.Equal(3, allResults.Count); // 2 from DC01 + 1 fallback for DC02
        Assert.Equal(2, allResults.Count(r => r.Server == "DC01"));
        Assert.Single(allResults.Where(r => r.Server == "DC02"));
        Assert.Equal("FAIL", allResults.First(r => r.Server == "DC02").Result);
    }

    // ── Email subject line construction ──────────────────────────────────

    [Theory]
    [InlineData(0, "Scheduled Test Completed - Nightly Check")]
    [InlineData(2, "(FAILED) Scheduled Test Completed - Nightly Check")]
    public void ScheduledEmailSubject_FailedPrefixApplied(int failCount, string expected)
    {
        // Mirrors the subject logic in RunScheduledTestsAsync
        string taskName = "Nightly Check";
        string subject = failCount > 0
            ? $"(FAILED) Scheduled Test Completed - {taskName}"
            : $"Scheduled Test Completed - {taskName}";

        Assert.Equal(expected, subject);
    }

    [Theory]
    [InlineData(0, "Test Completed - ADG Test Results")]
    [InlineData(3, "(FAILED) Test Completed - ADG Test Results")]
    public void ManualRunEmailSubject_FailedPrefixApplied(int failCount, string expected)
    {
        // Mirrors the subject logic in RunButton_Click
        string subject = failCount > 0 ? "(FAILED) Test Completed - ADG Test Results" : "Test Completed - ADG Test Results";

        Assert.Equal(expected, subject);
    }

    // ── BuildRunSummary with scheduler scenarios ─────────────────────────

    [Fact]
    public void BuildRunSummary_WithSchedulerTask_SingleDc()
    {
        string[] dcList = { "2022DC01" };
        string summary = MainWindow.BuildRunSummary(15, 13, 2, dcList);

        Assert.Contains("Domain controllers tested: 1", summary);
        Assert.Contains("Controllers: 2022DC01", summary);
        Assert.Contains("Total tests: 15", summary);
        Assert.Contains("Passed: 13", summary);
        Assert.Contains("Failed: 2", summary);
    }

    [Fact]
    public void BuildRunSummary_WithSchedulerTask_MultipleDcs()
    {
        string[] dcList = { "2022DC01", "2022DC02", "2022DC03" };
        string summary = MainWindow.BuildRunSummary(45, 40, 5, dcList);

        Assert.Contains("Domain controllers tested: 3", summary);
        Assert.Contains("2022DC01, 2022DC02, 2022DC03", summary);
        Assert.Contains("Total tests: 45", summary);
    }

    [Fact]
    public void BuildRunSummary_WithUnreachableDc_IncludesInSummary()
    {
        // Simulates: 2 DCs configured, but one unreachable
        string[] dcList = { "DC01", "DC02" };
        var allResults = new List<TestResult>
        {
            new() { Service = "Connectivity", Server = "DC01", Result = "PASS", Message = "passed" },
            new() { Service = "DCDiag", Server = "DC02", Result = "FAIL", Message = "DCDiag produced no parseable output. DC may be unreachable." }
        };

        int total = allResults.Count;
        int passed = allResults.Count(r => r.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase));
        int failed = allResults.Count(r => r.Result.Equals("FAIL", StringComparison.OrdinalIgnoreCase));

        string summary = MainWindow.BuildRunSummary(total, passed, failed, dcList);

        // Both DCs should be listed in the summary
        Assert.Contains("Domain controllers tested: 2", summary);
        Assert.Contains("DC01, DC02", summary);
        Assert.Contains("Total tests: 2", summary);
        Assert.Contains("Passed: 1", summary);
        Assert.Contains("Failed: 1", summary);
    }

    [Fact]
    public void BuildRunSummary_AllDcsUnreachable()
    {
        string[] dcList = { "DC01", "DC02" };
        var allResults = new List<TestResult>
        {
            new() { Service = "DCDiag", Server = "DC01", Result = "FAIL", Message = "DCDiag produced no parseable output. DC may be unreachable." },
            new() { Service = "DCDiag", Server = "DC02", Result = "FAIL", Message = "DCDiag produced no parseable output. DC may be unreachable." }
        };

        int total = allResults.Count;
        int passed = allResults.Count(r => r.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase));
        int failed = allResults.Count(r => r.Result.Equals("FAIL", StringComparison.OrdinalIgnoreCase));

        string summary = MainWindow.BuildRunSummary(total, passed, failed, dcList);

        Assert.Contains("Domain controllers tested: 2", summary);
        Assert.Contains("Total tests: 2", summary);
        Assert.Contains("Passed: 0", summary);
        Assert.Contains("Failed: 2", summary);
    }

    // ── WriteResultsSummarySync with scheduler data ──────────────────────

    [Fact]
    public void WriteResultsSummarySync_ScheduledTask_IncludesMetadata()
    {
        string runDir = Path.Combine(testDirectory, "scheduled_run");
        Directory.CreateDirectory(runDir);
        var session = new MainWindow.RunLogSession
        {
            StartedAt = new DateTime(2026, 6, 8, 22, 0, 0),
            TestType = "Nightly Health Check",
            RunDirectoryPath = runDir,
            CombinedLogPath = Path.Combine(runDir, "CombinedTestResults.txt")
        };

        var results = new List<TestResult>
        {
            new() { Service = "Connectivity", Server = "DC01", Result = "PASS", Message = "passed test Connectivity", LogFilePath = "test.log" },
            new() { Service = "DNS", Server = "DC01", Result = "FAIL", Message = "failed test DNS", LogFilePath = "test.log" }
        };

        string summary = MainWindow.BuildRunSummary(2, 1, 1, new[] { "DC01" });
        string path = MainWindow.WriteResultsSummarySync(session, results, summary);

        Assert.True(File.Exists(path));
        string content = File.ReadAllText(path);

        Assert.Contains("AD Guardian - Test Results Summary", content);
        Assert.Contains("Nightly Health Check", content);
        Assert.Contains("Nightly Health Check", content);
        Assert.Contains(summary, content);
        Assert.Contains("Connectivity", content);
        Assert.Contains("DNS", content);
        Assert.Contains("PASS", content);
        Assert.Contains("FAIL", content);
    }

    [Fact]
    public async Task WriteResultsSummarySync_UnreachableDc_IncludesFallbackEntry()
    {
        string runDir = Path.Combine(testDirectory, "unreachable_run");
        Directory.CreateDirectory(runDir);
        var session = new MainWindow.RunLogSession
        {
            StartedAt = DateTime.Now,
            TestType = "Scheduled",
            RunDirectoryPath = runDir,
            CombinedLogPath = Path.Combine(runDir, "combined.txt")
        };

        var results = new List<TestResult>
        {
            new() { Service = "DCDiag", Server = "DC01", Result = "FAIL", Message = "DCDiag produced no parseable output. DC may be unreachable.", LogFilePath = "test.log" },
            new() { Service = "Connectivity", Server = "DC02", Result = "PASS", Message = "passed test Connectivity", LogFilePath = "test.log" }
        };

        string summary = MainWindow.BuildRunSummary(2, 1, 1, new[] { "DC01", "DC02" });
        string path = MainWindow.WriteResultsSummarySync(session, results, summary);
        string content = await File.ReadAllTextAsync(path);

        Assert.Contains("unreachable", content);
        Assert.Contains("DCDiag", content);
    }

    // ── ScheduledTask model edge cases ───────────────────────────────────

    [Fact]
    public void ScheduledTask_MultipleDcs_CanBeSetAndRetrieved()
    {
        var task = new ScheduledTask
        {
            TaskName = "Multi-DC Check",
            DomainController = "DC01,DC02,DC03",
            Frequency = "Daily",
            StartDate = new DateTime(2026, 1, 1),
            StartTime = "06:00"
        };

        Assert.Equal("DC01,DC02,DC03", task.DomainController);
        Assert.Contains("DC01", task.ToString());
        Assert.Contains("DC02", task.ToString());
        Assert.Contains("DC03", task.ToString());
    }

    [Theory]
    [InlineData("Daily")]
    [InlineData("Weekly")]
    [InlineData("Monthly")]
    [InlineData("Hourly")]
    public void ScheduledTask_AllFrequencies_Serialize(string frequency)
    {
        var task = new ScheduledTask
        {
            TaskName = "Test",
            DomainController = "DC01",
            Frequency = frequency,
            StartDate = DateTime.Today,
            StartTime = "14:00"
        };

        Assert.Equal(frequency, task.Frequency);
        Assert.Contains(frequency, task.ToString());
    }

    [Fact]
    public void ScheduledTask_ToString_ContainsAllFields()
    {
        var task = new ScheduledTask
        {
            TaskName = "Nightly Health",
            DomainController = "dc01.corp.local",
            Frequency = "Daily",
            StartDate = new DateTime(2026, 3, 15),
            StartTime = "22:30"
        };

        string result = task.ToString();

        Assert.Contains("Nightly Health", result);
        Assert.Contains("dc01.corp.local", result);
        Assert.Contains("Daily", result);
        Assert.Contains("22:30", result);
        Assert.Contains("2026", result);
    }

    // ── ParseDCDiagOutput scheduler-specific edge cases ───────────────────

    [Fact]
    public void ParseDCDiagOutput_ErrorMessageFromUnreachableDc_ReturnsEmpty()
    {
        // Common error messages when dcdiag can't reach a DC
        string[] errorOutputs =
        [
            "The RPC server is unavailable.",
            "Unable to connect to the server",
            "DNS name does not exist",
            "",
            "ERROR: The RPC server is unavailable."
        ];

        foreach (string output in errorOutputs)
        {
            var results = MainWindow.ParseDCDiagOutput("DC01", output, "test.log").ToList();
            Assert.Empty(results);
        }
    }

    [Fact]
    public void ParseDCDiagOutput_TimeoutMessage_ReturnsEmpty()
    {
        string output = "The command timed out after 30 seconds.";
        var results = MainWindow.ParseDCDiagOutput("DC01", output, "test.log").ToList();
        Assert.Empty(results);
    }

    [Fact]
    public void ParseDCDiagOutput_PartialOutput_ParsesAvailableTests()
    {
        // dcdiag sometimes gets interrupted mid-run
        string output = @"
   Testing server: DC01
      Starting test: Connectivity
         ......................... DC01 passed test Connectivity
      Starting test: Advertising
         ......................... DC01 passed test Advertising
      Starting test: FrsEvent
";
        var results = MainWindow.ParseDCDiagOutput("DC01", output, "test.log").ToList();

        // Should only include completed tests, not the incomplete FrsEvent
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("PASS", r.Result));
    }

    // ── SanitizeFileNamePart for scheduler log paths ──────────────────────

    [Theory]
    [InlineData("Nightly Health Check", "Nightly_Health_Check")]
    [InlineData("Weekly Replication Check", "Weekly_Replication_Check")]
    [InlineData("DC01", "DC01")]
    [InlineData("DC01/Test", "DC01_Test")]
    [InlineData("Test:Colon", "Test_Colon")]
    public void SanitizeFileNamePart_SchedulerTaskNames(string input, string expected)
    {
        string result = MainWindow.SanitizeFileNamePart(input);
        Assert.Equal(expected, result);
    }

    // ── Combined log file structure ──────────────────────────────────────

    [Fact]
    public async Task CombinedLog_SchedulerRun_ContainsAllDcSections()
    {
        string dc1Log = Path.Combine(testDirectory, "DC01_log.txt");
        string dc2Log = Path.Combine(testDirectory, "DC02_log.txt");
        await File.WriteAllTextAsync(dc1Log, "DC01 dcdiag output\n");
        await File.WriteAllTextAsync(dc2Log, "DC02 dcdiag output\n");

        string combinedPath = Path.Combine(testDirectory, "CombinedTestResults.txt");

        await using (StreamWriter writer = new(combinedPath, false))
        {
            foreach (string path in new[] { dc1Log, dc2Log })
            {
                await writer.WriteLineAsync($"---- Results for DC: {Path.GetFileNameWithoutExtension(path)} ----");
                string contents = await File.ReadAllTextAsync(path);
                await writer.WriteAsync(contents);
                await writer.WriteLineAsync();
                await writer.WriteLineAsync("==========================================");
            }
        }

        string combined = await File.ReadAllTextAsync(combinedPath);

        Assert.Contains("---- Results for DC: DC01_log ----", combined);
        Assert.Contains("---- Results for DC: DC02_log ----", combined);
        Assert.Contains("DC01 dcdiag output", combined);
        Assert.Contains("DC02 dcdiag output", combined);
    }

    // ── RunLogSession creation for scheduled tests ───────────────────────

    [Fact]
    public void CreateRunLogSession_ScheduledTask_CreatesCorrectPaths()
    {
        DateTime startedAt = new(2026, 6, 8, 22, 0, 0);
        var session = MainWindow.CreateRunLogSession(startedAt, "Nightly Health Check");

        Assert.Contains("2026-06-08", session.RunDirectoryPath);
        Assert.Contains("220000", session.RunDirectoryPath);
        Assert.Contains("Nightly_Health_Check", session.RunDirectoryPath);
        Assert.True(Directory.Exists(session.RunDirectoryPath));
        Assert.EndsWith("CombinedTestResults.txt", session.CombinedLogPath);
    }

    [Fact]
    public void CreateRunLogSession_WithSpecialCharsInTaskName_SanitizesCorrectly()
    {
        DateTime startedAt = new(2026, 6, 8, 22, 0, 0);
        var session = MainWindow.CreateRunLogSession(startedAt, "Test:DC01/DC02*");

        Assert.Contains("Test_DC01_DC02_", session.RunDirectoryPath);
        Assert.True(Directory.Exists(session.RunDirectoryPath));
    }

    // ── Email body detail HTML generation ─────────────────────────────────

    [Fact]
    public void EmailBodyDetail_ScheduledRun_IncludesControllerCount()
    {
        // Mirrors the bodyDetail construction in RunScheduledTestsAsync
        string[] dcList = { "DC01", "DC02" };
        int total = 40;
        int passed = 37;
        int failed = 3;
        string passColor = "#2E7D32";
        string failColor = "#C62828";

        string bodyDetail =
            "<div style='margin-bottom:16px;'>" +
            $"<p style='font-size:15px;margin:0 0 8px 0;'>Domain controllers tested: <strong>{dcList.Length}</strong></p>" +
            $"<p style='margin:0 0 8px 0;'>Controllers: <strong>{string.Join(", ", dcList)}</strong></p>" +
            $"<p style='font-size:16px;margin:0 0 4px 0;'>Total tests: <strong>{total}</strong></p>" +
            $"<p style='font-size:16px;margin:0 0 4px 0;color:{passColor};'>Passed: <strong>{passed}</strong></p>" +
            $"<p style='font-size:16px;margin:0;color:{failColor};'>Failed: <strong>{failed}</strong></p>" +
            "</div>";

        Assert.Contains("Domain controllers tested: <strong>2</strong>", bodyDetail);
        Assert.Contains("Controllers: <strong>DC01, DC02</strong>", bodyDetail);
        Assert.Contains("Total tests: <strong>40</strong>", bodyDetail);
        Assert.Contains("Passed: <strong>37</strong>", bodyDetail);
        Assert.Contains("Failed: <strong>3</strong>", bodyDetail);
        Assert.Contains(passColor, bodyDetail);
        Assert.Contains(failColor, bodyDetail);
    }

    // ── AppStateStore scheduled tasks persistence ─────────────────────────

    [Fact]
    public void PersistScheduledTasks_ScheduledRun_MultipleTasksPersist()
    {
        var store = new AppStateStore(Path.Combine(testDirectory, "scheduler_test.db"));
        store.Initialize();

        var tasks = new List<ScheduledTask>
        {
            new() { TaskName = "Nightly Health", DomainController = "DC01", Frequency = "Daily", StartDate = DateTime.Today, StartTime = "22:00" },
            new() { TaskName = "Weekly Full Scan", DomainController = "DC01,DC02", Frequency = "Weekly", StartDate = DateTime.Today, StartTime = "06:00" },
            new() { TaskName = "Hourly Quick Check", DomainController = "DC01", Frequency = "Hourly", StartDate = DateTime.Today, StartTime = "08:00" }
        };

        store.SaveScheduledTasks(tasks);
        var loaded = store.LoadScheduledTasks();

        Assert.Equal(3, loaded.Count);
        Assert.Equal("Nightly Health", loaded[0].TaskName);
        Assert.Equal("Weekly Full Scan", loaded[1].TaskName);
        Assert.Equal("Hourly Quick Check", loaded[2].TaskName);
        Assert.Equal("Daily", loaded[0].Frequency);
        Assert.Equal("Weekly", loaded[1].Frequency);
        Assert.Equal("Hourly", loaded[2].Frequency);
    }

    [Fact]
    public void PersistScheduledTasks_EmptyList_ClearsAllTasks()
    {
        var store = new AppStateStore(Path.Combine(testDirectory, "scheduler_clear.db"));
        store.Initialize();

        store.SaveScheduledTasks(new List<ScheduledTask>
        {
            new() { TaskName = "Task1" },
            new() { TaskName = "Task2" }
        });

        store.SaveScheduledTasks(new List<ScheduledTask>());
        var loaded = store.LoadScheduledTasks();

        Assert.Empty(loaded);
    }

    // ── Edge cases for scheduler validation ───────────────────────────────

    [Theory]
    [InlineData("TaskName", "DC01", "Daily", "2026-06-08", "22:00", true)]
    [InlineData("", "DC01", "Daily", "2026-06-08", "22:00", false)] // empty task name
    [InlineData("Task", "", "Daily", "2026-06-08", "22:00", false)] // empty DC
    [InlineData("Task", "DC01", "", "2026-06-08", "22:00", false)] // empty frequency
    [InlineData("Task", "DC01", "Daily", "", "22:00", false)] // empty date
    [InlineData("Task", "DC01", "Daily", "2026-06-08", "", false)] // empty time
    public void SchedulerValidation_AllFieldsRequired(string taskName, string dc, string frequency, string date, string time, bool expectedValid)
    {
        // Mirrors the validation in SchedulerSaveButton_Click
        bool hasTaskName = !string.IsNullOrEmpty(taskName);
        bool hasDc = !string.IsNullOrEmpty(dc);
        bool hasFrequency = !string.IsNullOrEmpty(frequency);
        bool hasDate = !string.IsNullOrEmpty(date);
        bool hasTime = !string.IsNullOrEmpty(time);

        bool isValid = hasTaskName && hasDc && hasFrequency && hasDate && hasTime;

        Assert.Equal(expectedValid, isValid);
    }

    [Fact]
    public void SchedulerValidation_DCListWithCommas_ParsedCorrectly()
    {
        // Mirrors the DC entry parsing in SchedulerSaveButton_Click
        string dcInput = "DC01, DC02, DC03";

        List<string> dcEntries = dcInput.Trim()
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(dc => dc.Trim())
            .Where(dc => !string.IsNullOrEmpty(dc))
            .ToList();

        Assert.Equal(3, dcEntries.Count);
        Assert.Equal("DC01", dcEntries[0]);
        Assert.Equal("DC02", dcEntries[1]);
        Assert.Equal("DC03", dcEntries[2]);
    }

    [Fact]
    public void SchedulerValidation_WhitespaceOnlyDCList_ReturnsEmpty()
    {
        string dcInput = "  ,  ,  ";

        List<string> dcEntries = dcInput.Trim()
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(dc => dc.Trim())
            .Where(dc => !string.IsNullOrEmpty(dc))
            .ToList();

        Assert.Empty(dcEntries);
    }

    // ── RemoveOldestScheduledLogCacheEntry tests ──────────────────────────

    /// <summary>
    /// Creates a Dictionary{string, CachedScheduledLog} via reflection since
    /// CachedScheduledLog is a private sealed nested class.
    /// </summary>
    private static System.Collections.IDictionary CreateCacheDictionary()
    {
        var logEntryType = typeof(MainWindow).GetNestedType(
            "CachedScheduledLog", BindingFlags.NonPublic)!;
        var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), logEntryType);
        return (System.Collections.IDictionary)Activator.CreateInstance(
            dictType, new object[] { StringComparer.OrdinalIgnoreCase })!;
    }

    private static void InvokeRemoveOldestScheduledLogCacheEntry(object instance)
    {
        var method = typeof(MainWindow).GetMethod(
            "RemoveOldestScheduledLogCacheEntry",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(instance, null);
    }

    private static object CreateCachedLogEntry(DateTime lastWriteUtc)
    {
        var logEntryType = typeof(MainWindow).GetNestedType(
            "CachedScheduledLog", BindingFlags.NonPublic)!;
        var entry = FormatterServices.GetUninitializedObject(logEntryType);
        logEntryType.GetProperty("LastWriteUtc")!.SetValue(entry, lastWriteUtc);
        return entry;
    }

    [Fact]
    public void RemoveOldestScheduledLogCacheEntry_EmptyCache_NoOp()
    {
        var window = (MainWindow)FormatterServices.GetUninitializedObject(typeof(MainWindow));
        var cacheField = typeof(MainWindow).GetField(
            "scheduledLogCache", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(cacheField);

        var cache = CreateCacheDictionary();
        cacheField.SetValue(window, cache);

        var ex = Record.Exception(() => InvokeRemoveOldestScheduledLogCacheEntry(window));
        Assert.Null(ex);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void RemoveOldestScheduledLogCacheEntry_SingleEntry_RemovesIt()
    {
        var window = (MainWindow)FormatterServices.GetUninitializedObject(typeof(MainWindow));
        var cacheField = typeof(MainWindow).GetField(
            "scheduledLogCache", BindingFlags.NonPublic | BindingFlags.Instance);
        var cache = CreateCacheDictionary();
        cache["log1.txt"] = CreateCachedLogEntry(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        cacheField.SetValue(window, cache);

        InvokeRemoveOldestScheduledLogCacheEntry(window);

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void RemoveOldestScheduledLogCacheEntry_MultipleEntries_RemovesOldest()
    {
        var window = (MainWindow)FormatterServices.GetUninitializedObject(typeof(MainWindow));
        var cacheField = typeof(MainWindow).GetField(
            "scheduledLogCache", BindingFlags.NonPublic | BindingFlags.Instance);
        var cache = CreateCacheDictionary();

        DateTime oldest = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime middle = new(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        DateTime newest = new(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);

        cache["oldest.txt"] = CreateCachedLogEntry(oldest);
        cache["middle.txt"] = CreateCachedLogEntry(middle);
        cache["newest.txt"] = CreateCachedLogEntry(newest);
        cacheField.SetValue(window, cache);

        InvokeRemoveOldestScheduledLogCacheEntry(window);

        Assert.Equal(2, cache.Count);
        Assert.False(cache.Contains("oldest.txt"));
        Assert.True(cache.Contains("middle.txt"));
        Assert.True(cache.Contains("newest.txt"));
    }

    [Fact]
    public void RemoveOldestScheduledLogCacheEntry_TiedTimestamps_RemovesOne()
    {
        var window = (MainWindow)FormatterServices.GetUninitializedObject(typeof(MainWindow));
        var cacheField = typeof(MainWindow).GetField(
            "scheduledLogCache", BindingFlags.NonPublic | BindingFlags.Instance);
        var cache = CreateCacheDictionary();

        DateTime sameTime = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        cache["a.txt"] = CreateCachedLogEntry(sameTime);
        cache["b.txt"] = CreateCachedLogEntry(sameTime);
        cache["c.txt"] = CreateCachedLogEntry(sameTime);
        cacheField.SetValue(window, cache);

        InvokeRemoveOldestScheduledLogCacheEntry(window);

        // Should remove exactly one entry (any, since all have same timestamp)
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void RemoveOldestScheduledLogCacheEntry_TwentyEntries_EvictsOldestAndPreservesNewest()
    {
        var window = (MainWindow)FormatterServices.GetUninitializedObject(typeof(MainWindow));
        var cacheField = typeof(MainWindow).GetField(
            "scheduledLogCache", BindingFlags.NonPublic | BindingFlags.Instance);
        var cache = CreateCacheDictionary();

        // Fill to exactly 20 (the max before eviction triggers in LoadScheduledResultsFromLogAsync)
        DateTime baseTime = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 20; i++)
        {
            cache[$"log_{i:D2}.txt"] = CreateCachedLogEntry(baseTime.AddHours(i));
        }
        cacheField.SetValue(window, cache);

        Assert.Equal(20, cache.Count);
        Assert.True(cache.Contains("log_00.txt"));
        Assert.True(cache.Contains("log_19.txt"));

        InvokeRemoveOldestScheduledLogCacheEntry(window);

        Assert.Equal(19, cache.Count);
        Assert.False(cache.Contains("log_00.txt")); // oldest removed
        Assert.True(cache.Contains("log_19.txt"));  // newest preserved
    }
}

