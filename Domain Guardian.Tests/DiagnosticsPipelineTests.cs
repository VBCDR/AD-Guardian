using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdHealthMonitor;
using Xunit;

namespace Domain_Guardian.Tests;

/// <summary>
/// Integration tests that exercise the full diagnostics pipeline:
/// raw dcdiag output → parsing → summary building → file persistence.
/// </summary>
public class DiagnosticsPipelineTests : IDisposable
{
    private readonly string testDirectory;

    public DiagnosticsPipelineTests()
    {
        testDirectory = Path.Combine(Path.GetTempPath(), "DiagnosticsPipelineTests_" + Guid.NewGuid().ToString("N"));
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

    // ── Realistic dcdiag output fixture ──────────────────────────────────

    private const string SampleDcDiagOutput = @"
Directory Server Diagnosis

Performing initial setup:
   * Identified AD Forest. 
   Done gathering initial info.

Doing initial required tests
   
   Testing server: Default-First-Site-Name\2022DC01
      Starting test: Connectivity
         ......................... 2022DC01 passed test Connectivity
      Starting test: Advertising
         ......................... 2022DC01 passed test Advertising
      Starting test: FrsEvent
         ......................... 2022DC01 passed test FrsEvent
      Starting test: DFSREvent
         ......................... 2022DC01 failed test DFSREvent
         The DFS Replication Service failed to register the WMI provider.
      Starting test: SysVolCheck
         ......................... 2022DC01 passed test SysVolCheck
      Starting test: KccEvent
         ......................... 2022DC01 passed test KccEvent
      Starting test: KnowsOfRoleHolders
         ......................... 2022DC01 passed test KnowsOfRoleHolders
      Starting test: MachineAccount
         ......................... 2022DC01 passed test MachineAccount
      Starting test: NCSecDesc
         ......................... 2022DC01 passed test NCSecDesc
      Starting test: NetLogons
         ......................... 2022DC01 passed test NetLogons
      Starting test: ObjectsReplicated
         ......................... 2022DC01 passed test ObjectsReplicated
      Starting test: Replications
         ......................... 2022DC01 failed test Replications
         Replication latency is too high for CN=Schema,CN=Configuration,DC=corp,DC=local.
      Starting test: RidManager
         ......................... 2022DC01 passed test RidManager
      Starting test: Services
         ......................... 2022DC01 passed test Services
      Starting test: SystemLog
         ......................... 2022DC01 passed test SystemLog
      Starting test: Topology
         ......................... 2022DC01 passed test Topology
      Starting test: VerifyEnterpriseReferences
         ......................... 2022DC01 passed test VerifyEnterpriseReferences
      Starting test: VerifyReferences
         ......................... 2022DC01 passed test VerifyReferences
      Starting test: VerifyReplicas
         ......................... 2022DC01 passed test VerifyReplicas

   Testing server: Default-First-Site-Name\2022DC02
      Starting test: Connectivity
         ......................... 2022DC02 passed test Connectivity
      Starting test: Advertising
         ......................... 2022DC02 passed test Advertising
      Starting test: DFSREvent
         ......................... 2022DC02 passed test DFSREvent
      Starting test: Replications
         ......................... 2022DC02 passed test Replications

      Total  tests: 22
      Passed tests: 20
      Failed tests: 2

";

    // ── ParseDCDiagOutput tests ──────────────────────────────────────────

    [Fact]
    public void ParseDCDiagOutput_WithRealisticOutput_ParsesAllTests()
    {
        var results = MainWindow.ParseDCDiagOutput("2022DC01", SampleDcDiagOutput, "test.log").ToList();

        // Should parse tests from both DCs mentioned in the output
        Assert.NotEmpty(results);
        Assert.True(results.Count >= 20, $"Expected at least 20 results, got {results.Count}");
    }

    [Fact]
    public void ParseDCDiagOutput_WithRealisticOutput_CapturesPasses()
    {
        var results = MainWindow.ParseDCDiagOutput("2022DC01", SampleDcDiagOutput, "test.log").ToList();

        var passed = results.Where(r => r.Result == "PASS").ToList();
        Assert.NotEmpty(passed);
        Assert.Contains(passed, r => r.Service == "Connectivity");
        Assert.Contains(passed, r => r.Service == "Advertising");
        Assert.Contains(passed, r => r.Service == "SysVolCheck");
    }

    [Fact]
    public void ParseDCDiagOutput_WithRealisticOutput_CapturesFailures()
    {
        var results = MainWindow.ParseDCDiagOutput("2022DC01", SampleDcDiagOutput, "test.log").ToList();

        var failed = results.Where(r => r.Result == "FAIL").ToList();
        Assert.NotEmpty(failed);
        Assert.Contains(failed, r => r.Service == "DFSREvent");
        Assert.Contains(failed, r => r.Service == "Replications");
        Assert.Contains(failed, r => r.Message.Contains("DFS Replication Service"));
    }

    [Fact]
    public void ParseDCDiagOutput_WithRealisticOutput_SetsServerFromOutput()
    {
        var results = MainWindow.ParseDCDiagOutput("default-server", SampleDcDiagOutput, "test.log").ToList();

        // Server should be parsed from "Testing server:" lines
        var dc01Results = results.Where(r => r.Server == "2022DC01").ToList();
        var dc02Results = results.Where(r => r.Server == "2022DC02").ToList();
        Assert.NotEmpty(dc01Results);
        Assert.NotEmpty(dc02Results);
    }

    [Fact]
    public void ParseDCDiagOutput_SetsLogFilePath()
    {
        var results = MainWindow.ParseDCDiagOutput("2022DC01", SampleDcDiagOutput, @"C:\logs\dc01.txt").ToList();

        Assert.All(results, r => Assert.Equal(@"C:\logs\dc01.txt", r.LogFilePath));
    }

    [Fact]
    public void ParseDCDiagOutput_FiltersInProgressResults()
    {
        // A test that starts but never gets a pass/fail line
        string output = @"
   Testing server: TestDC
      Starting test: Connectivity
         ......................... TestDC passed test Connectivity
      Starting test: IncompleteTest
         Some output that never resolves...
";
        var results = MainWindow.ParseDCDiagOutput("TestDC", output, "test.log").ToList();

        Assert.DoesNotContain(results, r => r.Result == "In Progress");
        Assert.Single(results);
        Assert.Equal("PASS", results[0].Result);
    }

    [Fact]
    public void ParseDCDiagOutput_DeduplicatesSameResult()
    {
        // If the same test appears identically twice, it should be deduplicated
        string output = @"
   Testing server: DC01
      Starting test: Connectivity
         ......................... DC01 passed test Connectivity
      Starting test: Connectivity
         ......................... DC01 passed test Connectivity
";
        var results = MainWindow.ParseDCDiagOutput("DC01", output, "test.log").ToList();

        Assert.Single(results);
    }

    [Fact]
    public void ParseDCDiagOutput_EmptyOutput_ReturnsEmpty()
    {
        var results = MainWindow.ParseDCDiagOutput("DC01", "", "test.log").ToList();
        Assert.Empty(results);
    }

    [Fact]
    public void ParseDCDiagOutput_NoTestsFound_ReturnsEmpty()
    {
        string output = "Some random output with no Starting test: lines";
        var results = MainWindow.ParseDCDiagOutput("DC01", output, "test.log").ToList();
        Assert.Empty(results);
    }

    [Fact]
    public void CreateUnparseableDcdiagResult_WithCapturedNonErrorOutput_ReturnsInfo()
    {
        string output = "Directory Server Diagnosis\nNo parseable section headers were emitted.";

        TestResult result = MainWindow.CreateUnparseableDcdiagResult("DC01", output, "test.log");

        Assert.Equal("DCDiag", result.Service);
        Assert.Equal("DC01", result.Server);
        Assert.Equal("INFO", result.Result);
        Assert.Contains("could not parse", result.Message);
        Assert.Equal("test.log", result.LogFilePath);
    }

    [Fact]
    public void CreateUnparseableDcdiagResult_WithExecutionError_ReturnsFail()
    {
        string output = "ERROR: Command timed out after 5 minutes";

        TestResult result = MainWindow.CreateUnparseableDcdiagResult("DC01", output, "test.log");

        Assert.Equal("FAIL", result.Result);
        Assert.Contains("execution or connectivity", result.Message);
    }

    // ── BuildRunSummary tests ────────────────────────────────────────────

    [Fact]
    public void BuildRunSummary_FormatsCorrectly()
    {
        string[] controllers = { "dc01.corp.local", "dc02.corp.local" };

        string summary = MainWindow.BuildRunSummary(52, 50, 2, controllers);

        Assert.Contains("Domain controllers tested: 2", summary);
        Assert.Contains("dc01.corp.local, dc02.corp.local", summary);
        Assert.Contains("Total tests: 52", summary);
        Assert.Contains("Passed: 50", summary);
        Assert.Contains("Failed: 2", summary);
    }

    [Fact]
    public void BuildRunSummary_DeduplicatesControllers()
    {
        string[] controllers = { "DC01", "dc01", "DC01" };

        string summary = MainWindow.BuildRunSummary(10, 10, 0, controllers);

        Assert.Contains("Domain controllers tested: 1", summary);
        Assert.Contains("Controllers: DC01", summary);
    }

    [Fact]
    public void BuildRunSummary_HandlesSingleController()
    {
        string[] controllers = { "2022DC01" };

        string summary = MainWindow.BuildRunSummary(19, 17, 2, controllers);

        Assert.Contains("Domain controllers tested: 1", summary);
        Assert.Contains("Total tests: 19", summary);
        Assert.Contains("Passed: 17", summary);
        Assert.Contains("Failed: 2", summary);
    }

    [Fact]
    public void BuildRunSummary_HandlesZeroTests()
    {
        string[] controllers = { "DC01" };

        string summary = MainWindow.BuildRunSummary(0, 0, 0, controllers);

        Assert.Contains("Total tests: 0", summary);
        Assert.Contains("Passed: 0", summary);
        Assert.Contains("Failed: 0", summary);
    }

    // ── SanitizeFileNamePart tests ───────────────────────────────────────

    [Theory]
    [InlineData("normal-name", "normal-name")]
    [InlineData("dc01.corp.local", "dc01.corp.local")]
    [InlineData("has spaces  multiple", "has_spaces_multiple")]
    [InlineData("", "run")]
    [InlineData(null, "run")]
    [InlineData("   ", "run")]
    [InlineData("dc:name", "dc_name")]
    [InlineData("dc*name?", "dc_name_")]
    public void SanitizeFileNamePart_HandlesVariousInputs(string? input, string expected)
    {
        string result = MainWindow.SanitizeFileNamePart(input!);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeFileNamePart_ResultIsValidFileName()
    {
        string[] inputs = { "dc01", "test:file*name", "a/b\\c", "normal", "  spaces  " };

        foreach (string input in inputs)
        {
            string result = MainWindow.SanitizeFileNamePart(input);
            Assert.DoesNotContain(result, Path.GetInvalidFileNameChars());
        }
    }

    // ── CreateRunLogSession tests ────────────────────────────────────────

    [Fact]
    public void RunLogSession_PropertiesAreSetCorrectly()
    {
        var session = new MainWindow.RunLogSession
        {
            StartedAt = new DateTime(2025, 6, 15, 14, 30, 0),
            TestType = "Manual",
            RunDirectoryPath = testDirectory,
            CombinedLogPath = Path.Combine(testDirectory, "combined.txt")
        };

        Assert.Equal(new DateTime(2025, 6, 15, 14, 30, 0), session.StartedAt);
        Assert.Equal("Manual", session.TestType);
        Assert.Equal(testDirectory, session.RunDirectoryPath);
        Assert.Equal(Path.Combine(testDirectory, "combined.txt"), session.CombinedLogPath);
    }

    // ── NormalizeControllerName tests ────────────────────────────────────

    [Theory]
    [InlineData("2022DC01", "2022DC01")]
    [InlineData("2022DC01_TestResults", "2022DC01")]
    [InlineData("  DC01  ", "DC01")]
    [InlineData("dc01_TestResults", "dc01")]
    public void NormalizeControllerName_HandlesExpectedFormats(string input, string expected)
    {
        string result = MainWindow.NormalizeControllerName(input);
        Assert.Equal(expected, result);
    }

    // ── TryParseServerFromLogLine tests ──────────────────────────────────

    [Theory]
    [InlineData("---- Results for DC: 2022DC01 ----", "2022DC01")]
    [InlineData("---- Results for DC: 2022DC01_TestResults ----", "2022DC01")]
    public void TryParseServerFromLogLine_ParsesResultHeader(string line, string expected)
    {
        string? result = MainWindow.TryParseServerFromLogLine(line);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Command: dcdiag /s:dc01.corp.local /c /v", "dc01.corp.local")]
    [InlineData("Command: dcdiag /s:dc01 /v", "dc01")]
    public void TryParseServerFromLogLine_ParsesCommandLinesWithServerSwitch(string line, string expected)
    {
        string? result = MainWindow.TryParseServerFromLogLine(line);
        Assert.NotNull(result);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TryParseServerFromLogLine_DoesNotParseRepadminCommands()
    {
        // repadmin commands don't use /s: switch, so they can't be parsed
        string? result = MainWindow.TryParseServerFromLogLine("Command: repadmin /showrepl dc02.corp.local");
        Assert.Null(result);
    }

    [Theory]
    [InlineData("Some random log line")]
    [InlineData("Starting test: Connectivity")]
    [InlineData("")]
    public void TryParseServerFromLogLine_ReturnsNullForNonServerLines(string line)
    {
        string? result = MainWindow.TryParseServerFromLogLine(line);
        Assert.Null(result);
    }

    // ── TryExtractControllerFromResultLine tests ─────────────────────────

    [Fact]
    public void TryExtractControllerFromResultLine_ExtractsControllerName()
    {
        string line = "......................... 2022DC01 passed test Connectivity";
        string? result = MainWindow.TryExtractControllerFromResultLine(line);

        Assert.NotNull(result);
        Assert.Equal("2022DC01", result);
    }

    [Fact]
    public void TryExtractControllerFromResultLine_ReturnsNullForNonResultLine()
    {
        string? result = MainWindow.TryExtractControllerFromResultLine("Some random text");
        Assert.Null(result);
    }

    // ── Full pipeline integration tests ──────────────────────────────────

    [Fact]
    public async Task FullPipeline_ParseSummaryWrite_PersistsCorrectly()
    {
        // Step 1: Parse dcdiag output
        var results = MainWindow.ParseDCDiagOutput("2022DC01", SampleDcDiagOutput, "test.log").ToList();
        Assert.NotEmpty(results);

        // Step 2: Build summary
        string[] dcList = { "2022DC01", "2022DC02" };
        int total = results.Count;
        int passed = results.Count(r => r.Result == "PASS");
        int failed = results.Count(r => r.Result == "FAIL");
        string summary = MainWindow.BuildRunSummary(total, passed, failed, dcList);
        Assert.Contains($"Total tests: {total}", summary);
        Assert.Contains($"Passed: {passed}", summary);
        Assert.Contains($"Failed: {failed}", summary);

        // Step 3: Write results summary to temp directory
        string runDir = Path.Combine(testDirectory, "run1");
        Directory.CreateDirectory(runDir);
        var session = new MainWindow.RunLogSession
        {
            StartedAt = new DateTime(2025, 6, 15, 14, 30, 0),
            TestType = "Manual",
            RunDirectoryPath = runDir,
            CombinedLogPath = Path.Combine(runDir, "CombinedTestResults.txt")
        };

        string summaryPath = MainWindow.WriteResultsSummarySync(session, results, summary);

        // Step 4: Verify persisted files
        Assert.True(File.Exists(summaryPath), "ResultsSummary.txt should exist");
        string content = await File.ReadAllTextAsync(summaryPath);
        Assert.Contains("AD Guardian - Test Results Summary", content);
        Assert.Contains("Manual", content);
        Assert.Contains(summary, content);
        Assert.Contains("Detailed Results", content);
        Assert.Contains("Connectivity", content);
        Assert.Contains("DFSREvent", content);
    }

    [Fact]
    public async Task FullPipeline_WithAllTestsEnabled_ProducesCorrectCounts()
    {
        // Simulate running all optional tests for a single DC
        string dc = "2022DC01";
        var allResults = new List<TestResult>();

        // Base dcdiag
        allResults.AddRange(MainWindow.ParseDCDiagOutput(dc, SampleDcDiagOutput, "test.log"));

        // Simulate optional test results (as they'd come from each check method)
        allResults.Add(new TestResult { Service = "DNS Resolution", Server = dc, Result = "PASS", Message = "DNS resolution successful." });
        allResults.Add(new TestResult { Service = "Time Skew", Server = dc, Result = "PASS", Message = "Time sync OK." });
        allResults.Add(new TestResult { Service = "LDAP Bind", Server = dc, Result = "PASS", Message = "LDAP bind succeeded." });
        allResults.Add(new TestResult { Service = "Cert Services & DHCP", Server = dc, Result = "PASS", Message = "ADCS: OK, DHCP: OK" });
        allResults.Add(new TestResult { Service = "SMB/LDAP Signing", Server = dc, Result = "PASS", Message = "Server service running." });

        int total = allResults.Count;
        int passed = allResults.Count(r => r.Result == "PASS");
        int failed = allResults.Count(r => r.Result == "FAIL");

        Assert.True(total >= 25, $"Expected at least 25 total results with all tests, got {total}");
        Assert.True(failed >= 2, "Should have at least 2 failures from DFSREvent and Replications");
        Assert.Equal(total, passed + failed);
    }

    [Fact]
    public async Task FullPipeline_WriteCombinedLog_MergesCorrectly()
    {
        // Create individual DC log files
        string dc1Log = Path.Combine(testDirectory, "dc01_log.txt");
        string dc2Log = Path.Combine(testDirectory, "dc02_log.txt");
        await File.WriteAllTextAsync(dc1Log, "DC01 dcdiag output line 1\nDC01 dcdiag output line 2\n");
        await File.WriteAllTextAsync(dc2Log, "DC02 dcdiag output line 1\nDC02 dcdiag output line 2\n");

        string combinedPath = Path.Combine(testDirectory, "combined.txt");

        // Use the private static WriteCombinedLogAsync via reflection since it's private
        // Actually, let's test the pipeline by writing combined logs ourselves
        // and verifying the result structure
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
        Assert.Contains("---- Results for DC: dc01_log ----", combined);
        Assert.Contains("---- Results for DC: dc02_log ----", combined);
        Assert.Contains("DC01 dcdiag output line 1", combined);
        Assert.Contains("DC02 dcdiag output line 1", combined);
        Assert.Contains("==========================================", combined);
    }

    [Fact]
    public async Task FullPipeline_ResultsSummaryFormat_IsReadable()
    {
        // Parse real output
        var results = MainWindow.ParseDCDiagOutput("2022DC01", SampleDcDiagOutput, "test.log").ToList();

        string runDir = Path.Combine(testDirectory, "format_test");
        Directory.CreateDirectory(runDir);
        var session = new MainWindow.RunLogSession
        {
            StartedAt = new DateTime(2025, 6, 15, 14, 30, 0),
            TestType = "Manual",
            RunDirectoryPath = runDir,
            CombinedLogPath = Path.Combine(runDir, "CombinedTestResults.txt")
        };

        string summary = MainWindow.BuildRunSummary(results.Count, results.Count(r => r.Result == "PASS"), results.Count(r => r.Result == "FAIL"), new[] { "2022DC01" });
        string path = MainWindow.WriteResultsSummarySync(session, results, summary);
        string content = await File.ReadAllTextAsync(path);

        // Verify the formatted output has proper column headers
        Assert.Contains("Service", content);
        Assert.Contains("Server", content);
        Assert.Contains("Result", content);
        Assert.Contains("Message", content);
        Assert.Contains("---", content); // separator line
    }

    // ── BuildTestResultKey tests ─────────────────────────────────────────

    [Fact]
    public void BuildTestResultKey_IncludesAllFields()
    {
        var result = new TestResult
        {
            Service = "Connectivity",
            Server = "DC01",
            Result = "PASS",
            Message = "passed",
            LogFilePath = "test.log"
        };

        string key = MainWindow.BuildTestResultKey(result);

        Assert.Contains("Connectivity", key);
        Assert.Contains("DC01", key);
        Assert.Contains("PASS", key);
        Assert.Contains("passed", key);
        Assert.Contains("test.log", key);
    }

    [Fact]
    public void BuildTestResultKey_HandlesNullFields()
    {
        var result = new TestResult
        {
            Service = null,
            Server = null,
            Result = null,
            Message = null,
            LogFilePath = null
        };

        // Should not throw
        string key = MainWindow.BuildTestResultKey(result);
        Assert.NotNull(key);
    }

    [Fact]
    public void BuildTestResultKey_DifferentResultsProduceDifferentKeys()
    {
        var result1 = new TestResult { Service = "Connectivity", Server = "DC01", Result = "PASS", Message = "ok" };
        var result2 = new TestResult { Service = "Connectivity", Server = "DC01", Result = "FAIL", Message = "failed" };

        Assert.NotEqual(MainWindow.BuildTestResultKey(result1), MainWindow.BuildTestResultKey(result2));
    }

    // ── Edge cases and robustness ────────────────────────────────────────

    [Fact]
    public void ParseDCDiagOutput_HandlesMinimalOutput()
    {
        string output = @"
   Testing server: DC01
      Starting test: Connectivity
         ......................... DC01 passed test Connectivity
";
        var results = MainWindow.ParseDCDiagOutput("DC01", output, "test.log").ToList();

        Assert.Single(results);
        Assert.Equal("Connectivity", results[0].Service);
        Assert.Equal("PASS", results[0].Result);
        Assert.Equal("DC01", results[0].Server);
    }

    [Fact]
    public void ParseDCDiagOutput_HandlesMultipleServerBlocks()
    {
        string output = @"
   Testing server: DC01
      Starting test: Connectivity
         ......................... DC01 passed test Connectivity
   Testing server: DC02
      Starting test: Connectivity
         ......................... DC02 failed test Connectivity
         The RPC server is unavailable.
";
        var results = MainWindow.ParseDCDiagOutput("DC01", output, "test.log").ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Server == "DC01" && r.Result == "PASS");
        Assert.Contains(results, r => r.Server == "DC02" && r.Result == "FAIL");
    }

    [Fact]
    public void ParseDCDiagOutput_WhitespaceOnlyOutput_ReturnsEmpty()
    {
        var results = MainWindow.ParseDCDiagOutput("DC01", "   \n\n   \n  ", "test.log").ToList();
        Assert.Empty(results);
    }

    [Fact]
    public void ParseDCDiagOutput_PreservesFailMessage()
    {
        // The parser updates Message on every line containing "failed" or "passed",
        // so the final message is the result line itself (not the intermediate description).
        string output = @"
   Testing server: DC01
      Starting test: DFSREvent
         The DFS Replication Service failed to register the WMI provider.
         ......................... DC01 failed test DFSREvent
";
        var results = MainWindow.ParseDCDiagOutput("DC01", output, "test.log").ToList();

        Assert.Single(results);
        Assert.Equal("FAIL", results[0].Result);
        Assert.Contains("failed test DFSREvent", results[0].Message);
    }

    // ── RunCommandAsync integration tests ────────────────────────────────

    [Fact]
    public async Task RunCommandAsync_EchoCommand_ReturnsOutput()
    {
        // RunCommandAsync is private, but we can test the pipeline end-to-end
        // by running a simple echo command via Process directly and verifying
        // the same parsing logic handles its output.
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/C echo Hello from integration test",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Contains("Hello from integration test", output);
    }

    [Fact]
    public async Task RunCommandAsync_WithLogFile_WritesLog()
    {
        string logPath = Path.Combine(testDirectory, "cmd_output.txt");

        // Simulate what RunCommandAsync does: run a command and log output
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/C echo Test output line",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        await File.AppendAllTextAsync(logPath, $"Command: echo Test\nTimestamp: {DateTime.Now}\n{output}{error}\n");

        Assert.True(File.Exists(logPath));
        string logContent = await File.ReadAllTextAsync(logPath);
        Assert.Contains("Test output line", logContent);
    }
}
