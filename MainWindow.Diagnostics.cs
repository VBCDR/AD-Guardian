// MainWindow partial class - Diagnostics functionality
// Extracted from MainWindow.xaml.cs during partial class refactoring.

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Navigation;
using System.Windows.Threading;
using Domain_Guardian;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace AdHealthMonitor;

public partial class MainWindow
{
    internal sealed class RunLogSession
    {
        public DateTime StartedAt { get; init; }
        public string TestType { get; init; } = string.Empty;
        public string RunDirectoryPath { get; init; } = string.Empty;
        public string CombinedLogPath { get; init; } = string.Empty;
    }
    internal void StopButton_Click(object sender, RoutedEventArgs e)
    {
        cancellationTokenSource?.Cancel();
        UpdateActionButtonStates();
    }

    private static string GetRunsRootDirectoryPath()
    {
        return Path.Combine(LogDirectoryPath, RunLogsDirectoryName);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _sanitizeCache = new(StringComparer.OrdinalIgnoreCase);

    internal static string SanitizeFileNamePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "run";
        }

        return _sanitizeCache.GetOrAdd(value, static k =>
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            string trimmed = k.Trim();
            char[] sanitized = new char[trimmed.Length];
            for (int i = 0; i < trimmed.Length; i++)
            {
                char ch = trimmed[i];
                bool isInvalid = false;
                for (int ci = 0; ci < invalidChars.Length; ci++)
                {
                    if (invalidChars[ci] == ch)
                    {
                        isInvalid = true;
                        break;
                    }
                }
                sanitized[i] = isInvalid ? '_' : ch;
            }

            string collapsed = string.Join("_", new string(sanitized)
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

            return string.IsNullOrWhiteSpace(collapsed) ? "run" : collapsed;
        });
    }

    internal static RunLogSession CreateRunLogSession(DateTime startedAt, string testType)
    {
        string safeTestType = SanitizeFileNamePart(testType);
        string dateFolder = $"{startedAt:yyyy-MM-dd}";
        string runFolderName = $"{startedAt:HHmmss}_{safeTestType}";
        string runDirectoryPath = Path.Combine(GetRunsRootDirectoryPath(), dateFolder, runFolderName);
        Directory.CreateDirectory(runDirectoryPath);

        return new RunLogSession
        {
            StartedAt = startedAt,
            TestType = testType,
            RunDirectoryPath = runDirectoryPath,
            CombinedLogPath = Path.Combine(runDirectoryPath, "CombinedTestResults.txt")
        };
    }

    internal static string GetControllerLogPath(RunLogSession session, string domainController)
    {
        return Path.Combine(session.RunDirectoryPath, $"{SanitizeFileNamePart(domainController)}_TestResults.txt");
    }

    private static string? GetManagedRunDirectoryPath(string logFilePath)
    {
        if (string.IsNullOrWhiteSpace(logFilePath))
        {
            return null;
        }

        try
        {
            string fullPath = Path.GetFullPath(logFilePath);
            string runsRoot = Path.GetFullPath(GetRunsRootDirectoryPath()) + Path.DirectorySeparatorChar;
            string? parentDirectory = Path.GetDirectoryName(fullPath);

            if (string.IsNullOrWhiteSpace(parentDirectory))
            {
                return null;
            }

            string normalizedParent = Path.GetFullPath(parentDirectory) + Path.DirectorySeparatorChar;
            return normalizedParent.StartsWith(runsRoot, StringComparison.OrdinalIgnoreCase)
                ? parentDirectory
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsManagedRunLogPath(string logFilePath)
    {
        return GetManagedRunDirectoryPath(logFilePath) != null;
    }

    internal async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (isRunInProgress)
        {
            return;
        }

        // The app.manifest requires requireAdministrator, so the process is always elevated.

        isRunInProgress = true;
        UpdateActionButtonStates();
        Stopwatch runStopwatch = Stopwatch.StartNew();
        DateTime runStartedAt = DateTime.Now;
        await EnsureStartupInitializedAsync().ConfigureAwait(true);
        if (string.IsNullOrEmpty(domainControllers))
        {
            SetRunInProgress(false);
            NotificationService.Show(this, "Error", "No domain controllers specified. Please configure settings first.", isError: true);
            return;
        }

        if (domainControllers.IndexOf("lottery", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            string lotteryMessage = $"Good find congrats!\n\nYour Lucky numbers are:\n {GenerateLotteryNumbers()}\n\nGood Luck! 🤞";
            ShowLotteryPopup(lotteryMessage);
            SendEmailWithAttachment("Lottery Notification", lotteryMessage, string.Empty);
            SetRunInProgress(false);
            return;
        }

        cancellationTokenSource?.Dispose();
        cancellationTokenSource = new CancellationTokenSource();
        CancellationToken token = cancellationTokenSource.Token;
        SetRunInProgress(true);
        isLogContentReady = false;
        allResults.Clear();
        List<string> logFilePaths = new();
        RunLogSession runSession = CreateRunLogSession(runStartedAt, "Manual");
        // Manual split+trim+filter: avoids LINQ Select/Where/ToArray allocations
        string[] dcParts = domainControllers.Split(',');
        List<string> dcListTemp = new(dcParts.Length);
        for (int i = 0; i < dcParts.Length; i++)
        {
            string trimmed = dcParts[i].Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                dcListTemp.Add(trimmed);
        }
        string[] dcList = dcListTemp.ToArray();

        int optionalTestCount = 0;
        if (testDnsCheck) optionalTestCount++;
        if (testReplication) optionalTestCount++;
        if (testTimeSkew) optionalTestCount++;
        if (testLdapBind) optionalTestCount++;
        if (testCertDhcp) optionalTestCount++;
        if (testSmbLdapSigning) optionalTestCount++;

        const int baseStepsPerController = 1;
        int stepsPerController = baseStepsPerController + optionalTestCount;
        const int supplementalSteps = 2;
        int totalSteps = (dcList.Length * stepsPerController) + supplementalSteps;
        int completedSteps = 0;
        bool wasCancelled = false;
        ShowRunProgress(
            "Running domain controller diagnostics",
            $"Starting validation across {dcList.Length} domain controller(s).",
            completedSteps,
            totalSteps);
        try
        {
            for (int index = 0; index < dcList.Length; index++)
            {
                string dc = dcList[index];

                if (token.IsCancellationRequested)
                {
                    wasCancelled = true;
                    NotificationService.Show(this, "Stopped", "Test execution stopped by user.");
                    break;
                }

                ShowRunProgress(
                    "Running domain controller diagnostics",
                    $"[{dc}] Running DCDiag checks ({index + 1} of {dcList.Length} controllers).",
                    completedSteps,
                    totalSteps);

                string logFilePath = GetControllerLogPath(runSession, dc);

                string dcdiagResult = await RunCommandAsync($"dcdiag /s:{dc} /c /v", logFilePath, token);
                completedSteps += 1;
                ShowRunProgress(
                    "Running domain controller diagnostics",
                    $"[{dc}] DCDiag complete. Running replication checks next.",
                    completedSteps,
                    totalSteps);

                List<TestResult> dcDiagResults = ParseDCDiagOutput(dc, dcdiagResult, logFilePath);
                if (dcDiagResults.Count == 0)
                {
                    dcDiagResults.Add(new TestResult
                    {
                        Service = "DCDiag",
                        Server = dc,
                        Result = "FAIL",
                        Message = "DCDiag produced no parseable output. DC may be unreachable.",
                        LogFilePath = logFilePath
                    });
                }
                allResults.AddRange(dcDiagResults);
                logFilePaths.Add(logFilePath);

                if (testDnsCheck)
                {
                    allResults.AddRange(await RunDnsCheckAsync(dc, logFilePath, token));
                    completedSteps += 1;
                    ShowRunProgress("Running optional tests", $"[{dc}] DNS check complete.", completedSteps, totalSteps);
                }

                if (testReplication)
                {
                    string replOutput = await RunCommandAsync($"repadmin /showrepl {dc}", logFilePath, token);
                    allResults.AddRange(ParseDCDiagOutput(dc, replOutput, logFilePath));
                    completedSteps += 1;
                    ShowRunProgress("Running optional tests", $"[{dc}] Replication check complete.", completedSteps, totalSteps);
                }

                if (testTimeSkew)
                {
                    allResults.AddRange(await RunTimeSkewCheckAsync(dc, logFilePath, token));
                    completedSteps += 1;
                    ShowRunProgress("Running optional tests", $"[{dc}] Time skew check complete.", completedSteps, totalSteps);
                }

                if (testLdapBind)
                {
                    allResults.AddRange(await RunLdapBindCheckAsync(dc, logFilePath, token));
                    completedSteps += 1;
                    ShowRunProgress("Running optional tests", $"[{dc}] LDAP bind check complete.", completedSteps, totalSteps);
                }

                if (testCertDhcp)
                {
                    allResults.AddRange(await RunCertDhcpCheckAsync(dc, logFilePath, token));
                    completedSteps += 1;
                    ShowRunProgress("Running optional tests", $"[{dc}] Cert/DHCP check complete.", completedSteps, totalSteps);
                }

                if (testSmbLdapSigning)
                {
                    allResults.AddRange(await RunSmbLdapSigningCheckAsync(dc, logFilePath, token));
                    completedSteps += 1;
                    ShowRunProgress("Running optional tests", $"[{dc}] SMB/LDAP signing check complete.", completedSteps, totalSteps);
                }
            }

            if (wasCancelled)
            {
                return;
            }

            ShowRunProgress(
                "Collecting supplemental data",
                "Refreshing inventory and telemetry collectors before the dashboard is updated.",
                completedSteps,
                totalSteps);
            await CollectSupplementalDataAsync(token, detail =>
            {
                completedSteps += 1;
                ShowRunProgress(
                    "Collecting supplemental data",
                    detail,
                    completedSteps,
                    totalSteps);
            });
            RebuildFindings();
            SyncResultItems();
            ShowRunProgress(
                "Finalizing results",
                "Supplemental collection complete. Updating findings, grids, and dashboard summaries.",
                completedSteps,
                totalSteps);

            if (allResults.Count == 0)
            {
                return;
            }

            string combinedLogPath = runSession.CombinedLogPath;
            await WriteCombinedLogAsync(logFilePaths, combinedLogPath, token);
            latestLogsFilePath = combinedLogPath;
            latestLogsText = File.Exists(combinedLogPath) ? await File.ReadAllTextAsync(combinedLogPath, token).ConfigureAwait(true) : string.Empty;

        int total = allResults.Count;
        int passed = 0, failed = 0;
        for (int i = 0; i < total; i++)
        {
            if (allResults[i].Result.Equals("PASS", StringComparison.OrdinalIgnoreCase)) passed++;
            else if (allResults[i].Result.Equals("FAIL", StringComparison.OrdinalIgnoreCase)) failed++;
        }
            string summary = BuildRunSummary(total, passed, failed, dcList);
            DisplayTestResults(summary);
            ForceRefreshDashboard();
    
            string passColor = "#2E7D32";
            string failColor = "#C62828";
            string bodyDetail =
                "<div style='margin-bottom:16px;'>" +
                $"<p style='font-size:15px;margin:0 0 8px 0;'>Domain controllers tested: <strong>{dcList.Length}</strong></p>" +
                $"<p style='margin:0 0 8px 0;'>Controllers: <strong>{string.Join(", ", dcList)}</strong></p>" +
                $"<p style='font-size:16px;margin:0 0 4px 0;'>Total tests: <strong>{total}</strong></p>" +
                $"<p style='font-size:16px;margin:0 0 4px 0;color:{passColor};'>Passed: <strong>{passed}</strong></p>" +
                $"<p style='font-size:16px;margin:0 0 4px 0;color:{failColor};'>Failed: <strong>{failed}</strong></p>" +
                (failed > 0 ? FormatTestResultTable(allResults, dcList, passColor, failColor) : string.Empty) +
                "</div>";

            string subject = failed > 0 ? "(FAILED) Test Completed - ADG Test Results" : "Test Completed - ADG Test Results";
            string emailAttachment = WriteResultsSummarySync(runSession, allResults, summary);
            if (sendEmailManual && !string.IsNullOrWhiteSpace(recipientEmail))
            {
                SendEmailWithAttachment(subject, bodyDetail, emailAttachment);
            }

            await SaveTestHistoryAsync(new TestHistoryEntry
            {
                RunDate = DateTime.Now,
                Total = total,
                Passed = passed,
                Failed = failed,
                Details = summary,
                LogFilePath = combinedLogPath,
                TestType = runSession.TestType
            });
            _ = CleanupLogFilesAsync();
            Debug.WriteLine($"Manual run completed in {runStopwatch.ElapsedMilliseconds}ms.");
            new SuccessNotification("Test Complete", $"Tests completed. {passed} passed, {failed} failed out of {total} total.").ShowDialog();
        }
        finally
        {
            HideRunProgress();
            SetRunInProgress(false);
        }
    }

    private async Task CollectSupplementalDataAsync(CancellationToken token, Action<string>? stageCompleted = null)
    {
        try
        {
            CancellationToken effectiveToken = token;
            CancellationTokenSource? timeoutCts = null;
            if (isScheduledLaunch)
            {
                timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(ScheduledCollectorTimeout);
                effectiveToken = timeoutCts.Token;
            }

            using (timeoutCts)
            {
                Task<AdInventorySnapshot> inventoryTask = inventoryCollector.CollectAsync(effectiveToken);
                Task<TelemetrySnapshot> telemetryTask = telemetryCollector.CollectAsync(effectiveToken);
                List<Task> pendingTasks = new() { inventoryTask, telemetryTask };

                while (pendingTasks.Count > 0)
                {
                    Task completedTask = await Task.WhenAny(pendingTasks);
                    pendingTasks.Remove(completedTask);

                    if (completedTask == inventoryTask)
                    {
                        latestInventory = await inventoryTask;
                        stageCompleted?.Invoke("Active Directory inventory snapshot collected.");
                    }
                    else if (completedTask == telemetryTask)
                    {
                        latestTelemetry = await telemetryTask;
                        stageCompleted?.Invoke("Windows telemetry snapshot collected.");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (isScheduledLaunch)
            {
                allFindings.Add(new AdHealthFinding
                {
                    Category = "Collector",
                    Severity = "Medium",
                    Source = "Supplemental Collection",
                    Target = "Inventory / telemetry",
                    Summary = "Supplemental collection timed out.",
                    Details = $"Background supplemental collection exceeded {ScheduledCollectorTimeout.TotalSeconds:0} seconds.",
                    Evidence = "Collector timeout during scheduled launch.",
                    Remediation = "Review RSAT availability, PowerShell responsiveness, and local machine load.",
                    Status = "Timed out"
                });
            }
        }
        catch (Exception ex)
        {
            latestInventory = AdInventorySnapshot.Empty;
            latestTelemetry = TelemetrySnapshot.Empty;
            allFindings.Add(new AdHealthFinding
            {
                Category = "Collector",
                Severity = "Medium",
                Source = "Supplemental Collection",
                Target = "Inventory / telemetry",
                Summary = "Supplemental collection failed.",
                Details = ex.Message,
                Evidence = ex.ToString(),
                Remediation = "Verify PowerShell access, RSAT tooling, and local permissions.",
                Status = "Failed"
            });
        }
    }

    private static async Task WriteCombinedLogAsync(IEnumerable<string> logFilePaths, string combinedLogPath, CancellationToken token)
    {
        await using StreamWriter writer = new(combinedLogPath, false);
        foreach (string path in logFilePaths)
        {
            if (!File.Exists(path))
                continue;
            token.ThrowIfCancellationRequested();
            await writer.WriteLineAsync($"---- Results for DC: {Path.GetFileNameWithoutExtension(path)} ----");
            string contents = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
            await writer.WriteAsync(contents);
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("==========================================");
        }
    }

    internal static string BuildRunSummary(int total, int passed, int failed, IEnumerable<string> controllers)
    {
        // Manual trim+distinct: avoids LINQ Where/Select/Distinct/ToArray allocations
        List<string> dcListTemp = new();
        HashSet<string> seenDc = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? controller in controllers)
        {
            if (string.IsNullOrWhiteSpace(controller)) continue;
            string trimmed = controller.Trim();
            if (seenDc.Add(trimmed))
                dcListTemp.Add(trimmed);
        }
        string[] dcList = dcListTemp.ToArray();

        return string.Join(
            Environment.NewLine,
            [
                $"Domain controllers tested: {dcList.Length}",
                $"Controllers: {string.Join(", ", dcList)}",
                $"Total tests: {total}",
                $"Passed: {passed}",
                $"Failed: {failed}"
            ]);
    }        private static string FormatTestResultTable(List<TestResult> results, string[] dcList, string passColor, string failColor)
    {
        // Group by server without LINQ to reduce allocations in the email path.
        Dictionary<string, List<TestResult>> byServer = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < results.Count; i++)
        {
            // Manual first-or-default: avoids LINQ FirstOrDefault delegate allocation
            string? firstDc = dcList.Length > 0 ? dcList[0] : null;
            string key = results[i].Server ?? firstDc ?? "Unknown";
            if (!byServer.TryGetValue(key, out List<TestResult>? list))
            {
                list = new List<TestResult>();
                byServer[key] = list;
            }
            list.Add(results[i]);
        }

        StringBuilder sb = new();
        sb.Append("<div class='table-wrap'>");
        sb.Append("<table style='border-collapse:collapse;margin:10px 0;font-size:13px;width:100%;'>");
        sb.Append("<tr style='background:#f5f5f5;'><th style='padding:6px 10px;text-align:left;border:1px solid #ddd;'>Test</th>" +
                  "<th style='padding:6px 10px;text-align:left;border:1px solid #ddd;'>Status</th>" +
                  "<th style='padding:6px 10px;text-align:left;border:1px solid #ddd;'>Details</th></tr>");

        // Iterate the actual server groups found in results, not just the user-entered
        // dcList. Parsed server names (from dcdiag output) may differ from short names
        // (e.g. "DC01.corp.local" vs "DC01"), and exact-match lookups would silently
        // drop results for DCs with name mismatches.
        List<string> sortedKeys = new(byServer.Keys);
        // Sort: user-listed DCs first (by partial match), then alphabetically.
        sortedKeys.Sort((a, b) =>
        {
            bool aMatchesUser = false, bMatchesUser = false;
            for (int j = 0; j < dcList.Length; j++)
            {
                if (a.StartsWith(dcList[j], StringComparison.OrdinalIgnoreCase) ||
                    dcList[j].StartsWith(a, StringComparison.OrdinalIgnoreCase))
                    aMatchesUser = true;
                if (b.StartsWith(dcList[j], StringComparison.OrdinalIgnoreCase) ||
                    dcList[j].StartsWith(b, StringComparison.OrdinalIgnoreCase))
                    bMatchesUser = true;
            }
            if (aMatchesUser != bMatchesUser) return aMatchesUser ? -1 : 1;
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });

        foreach (string serverKey in sortedKeys)
        {
            List<TestResult> dcResults = byServer[serverKey];
            if (byServer.Count > 1)
            {
                sb.Append($"<tr style='background:#e8eaf6;'><td colspan='3' style='padding:6px 10px;border:1px solid #ddd;font-weight:bold;color:#283593;'>DC: {System.Net.WebUtility.HtmlEncode(serverKey)}</td></tr>");
            }

            foreach (TestResult r in dcResults)
            {
                bool isPass = string.Equals(r.Result, "PASS", StringComparison.OrdinalIgnoreCase);
                string bgColor = isPass ? "#f0fdf0" : "#fef2f2";
                string textColor = isPass ? passColor : failColor;
                string label = isPass ? "✓ Pass" : "✗ Fail";
                string display = (r.Server != null && !string.Equals(r.Server, serverKey, StringComparison.OrdinalIgnoreCase))
                    ? $"{r.Service} ({r.Server})"
                    : r.Service ?? "";
                string detail = (r.Message ?? "").Length > 80 ? r.Message![..80] + "…" : r.Message ?? "";
                sb.Append($"<tr style='background:{bgColor};'><td style='padding:4px 10px;border:1px solid #ddd;'>{System.Net.WebUtility.HtmlEncode(display)}</td>" +
                          $"<td style='padding:4px 10px;border:1px solid #ddd;color:{textColor};font-weight:bold;'>{label}</td>" +
                          $"<td style='padding:4px 10px;border:1px solid #ddd;'>{System.Net.WebUtility.HtmlEncode(detail)}</td></tr>");
            }
        }

        sb.Append("</table>");
        sb.Append("</div>");
        sb.Append("<p style='font-size:12px;color:#666;margin:4px 0 0 0;'>→ Full details in attached ResultsSummary.txt</p>");
        return sb.ToString();
    }

    internal static string WriteResultsSummarySync(RunLogSession session, List<TestResult> results, string summary)
    {
        string path = Path.Combine(session.RunDirectoryPath, "ResultsSummary.txt");
        try
        {
        int serviceWidth = 12, serverWidth = 10;
        for (int i = 0; i < results.Count; i++)
        {
            serviceWidth = Math.Max(serviceWidth, results[i].Service?.Length ?? 0);
            serverWidth = Math.Max(serverWidth, results[i].Server?.Length ?? 0);
        }
            using StreamWriter writer = new(path, false);
            writer.WriteLine("AD Guardian - Test Results Summary");
            writer.WriteLine($"Run: {session.StartedAt:dd MMM yyyy HH:mm}");
            writer.WriteLine($"Type: {session.TestType}");
            writer.WriteLine();
            writer.WriteLine(summary);
            writer.WriteLine();
            writer.WriteLine("--- Detailed Results ---");
            writer.WriteLine($"{PadRight("Service", serviceWidth)}  {PadRight("Server", serverWidth)}  Result  Message");
            writer.WriteLine($"{new string('-', serviceWidth)}  {new string('-', serverWidth)}  ------  -------");
            foreach (TestResult r in results)
            {
                writer.WriteLine($"{PadRight(r.Service, serviceWidth)}  {PadRight(r.Server, serverWidth)}  {PadRight(r.Result, 6)}  {r.Message}");
            }
        }
        catch { }
        return path;
    }

    private static async Task<string> WriteResultsSummaryAsync(RunLogSession session, List<TestResult> results, string summary, CancellationToken token)
    {
        string path = Path.Combine(session.RunDirectoryPath, "ResultsSummary.txt");
        try
        {
        int serviceWidth = 12, serverWidth = 10;
        for (int i = 0; i < results.Count; i++)
        {
            serviceWidth = Math.Max(serviceWidth, results[i].Service?.Length ?? 0);
            serverWidth = Math.Max(serverWidth, results[i].Server?.Length ?? 0);
        }
            await using StreamWriter writer = new(path, false);
            await writer.WriteLineAsync("AD Guardian - Test Results Summary");
            await writer.WriteLineAsync($"Run: {session.StartedAt:dd MMM yyyy HH:mm}");
            await writer.WriteLineAsync($"Type: {session.TestType}");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync(summary);
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("--- Detailed Results ---");
            await writer.WriteLineAsync($"{PadRight("Service", serviceWidth)}  {PadRight("Server", serverWidth)}  Result  Message");
            await writer.WriteLineAsync($"{new string('-', serviceWidth)}  {new string('-', serverWidth)}  ------  -------");
            foreach (TestResult r in results)
            {
                await writer.WriteLineAsync($"{PadRight(r.Service, serviceWidth)}  {PadRight(r.Server, serverWidth)}  {PadRight(r.Result, 6)}  {r.Message}");
            }
        }
        catch { }
        return path;
    }

    internal static List<TestResult> ParseDCDiagOutput(string server, string output, string logFilePath)
    {
        List<TestResult> results = new();
        string currentServer = server;
        TestResult? lastTestResult = null;
        using StringReader reader = new(output);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string trimmedLine = line.Trim();
            string? parsedServer = TryParseServerFromLogLine(trimmedLine);
            if (!string.IsNullOrWhiteSpace(parsedServer))
            {
                currentServer = parsedServer;
            }

            if (trimmedLine.Contains("Starting test:", StringComparison.OrdinalIgnoreCase))
            {
                string testName = trimmedLine.Replace("Starting test:", string.Empty).Trim();
                lastTestResult = new TestResult
                {
                    Service = testName,
                    Server = currentServer,
                    Result = "In Progress",
                    Message = "Awaiting result...",
                    LogFilePath = logFilePath
                };
                results.Add(lastTestResult);
            }
            else if ((trimmedLine.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                      trimmedLine.Contains("passed", StringComparison.OrdinalIgnoreCase)) &&
                     lastTestResult != null)
            {
                string effectiveServer = TryExtractControllerFromResultLine(trimmedLine) ?? currentServer;
                lastTestResult.Server = effectiveServer;
                lastTestResult.Result = trimmedLine.Contains("failed", StringComparison.OrdinalIgnoreCase) ? "FAIL" : "PASS";
                lastTestResult.Message = trimmedLine;
            }
        }

        // Deduplicate without LINQ to reduce allocations on every test completion.
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<TestResult> deduped = new();
        for (int i = 0; i < results.Count; i++)
        {
            TestResult r = results[i];
            if (string.Equals(r.Result, "In Progress", StringComparison.OrdinalIgnoreCase))
                continue;
            string key = BuildTestResultKey(r);
            if (seen.Add(key))
            {
                deduped.Add(r);
            }
        }
        return deduped;
    }

    internal static string BuildTestResultKey(TestResult result)
    {
        return string.Join("|",
            result.Service ?? string.Empty,
            result.Server ?? string.Empty,
            result.Result ?? string.Empty,
            result.Message ?? string.Empty,
            result.LogFilePath ?? string.Empty);
    }

    internal static string? TryParseServerFromLogLine(string line)
    {
        if (line.StartsWith("---- Results for DC:", StringComparison.OrdinalIgnoreCase))
        {
            string value = line
                .Replace("---- Results for DC:", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("----", string.Empty)
                .Trim();

            return NormalizeControllerName(value);
        }

        if (line.StartsWith("Command:", StringComparison.OrdinalIgnoreCase))
        {
            int serverSwitchIndex = line.IndexOf("/s:", StringComparison.OrdinalIgnoreCase);
            if (serverSwitchIndex >= 0)
            {
                string remainder = line[(serverSwitchIndex + 3)..].Trim();
                int endIndex = remainder.IndexOfAny([' ', '/', '\\', '\t']);
                string candidate = endIndex >= 0 ? remainder[..endIndex] : remainder;
                return NormalizeControllerName(candidate);
            }
        }

        return null;
    }

    internal static string? TryExtractControllerFromResultLine(string line)
    {
        string[] tokens = line
            .Split([' ', '\t', ',', ';', ':'], StringSplitOptions.RemoveEmptyEntries);

        foreach (string token in tokens)
        {
            if (token.EndsWith("$", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Manual digit/letter check: avoids LINQ Any delegate allocations
            bool hasDigit = false, hasLetter = false;
            for (int ci = 0; ci < token.Length; ci++)
            {
                char c = token[ci];
                if (char.IsDigit(c)) hasDigit = true;
                else if (char.IsLetter(c)) hasLetter = true;
                if (hasDigit && hasLetter) break;
            }
            if (hasDigit && hasLetter &&
                !token.Contains(".", StringComparison.OrdinalIgnoreCase) &&
                token.Length >= 4)
            {
                return NormalizeControllerName(token);
            }
        }

        return null;
    }

    internal static string NormalizeControllerName(string value)
    {
        string normalized = value.Trim();
        if (normalized.EndsWith("_TestResults", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^"_TestResults".Length];
        }

        return normalized.Trim();
    }

    private async Task<string> RunCommandAsync(string command, string logFilePath, CancellationToken token)
    {
        try
        {
            using Process process = new();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/C {command}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(token);
            try
            {
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                string output = await outputTask.ConfigureAwait(false);
                string error = await errorTask.ConfigureAwait(false);
                await AppendToLogWithRetryAsync(logFilePath, $"Command: {command}\nTimestamp: {DateTime.Now}\n{output}{error}\n", token).ConfigureAwait(false);
                return output + error;
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
                return "Execution Cancelled";
            }
        }
        catch (Exception ex)
        {
            await AppendToLogWithRetryAsync(logFilePath, $"Error running command: {ex.Message}\n", CancellationToken.None).ConfigureAwait(false);
            return "ERROR: " + ex.Message;
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static async Task AppendToLogWithRetryAsync(string logFilePath, string contents, CancellationToken token)
    {
        const int maxRetries = 5;
        const int delayMs = 200;
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                await File.AppendAllTextAsync(logFilePath, contents, token).ConfigureAwait(false);
                return;
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                await Task.Delay(delayMs, token).ConfigureAwait(false);
            }
        }
    }

    private async Task<string> RunPowerShellScriptAsync(string script, string logFilePath, CancellationToken token)
    {
        try
        {
            using Process process = new();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.Start();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(token);

            try
            {
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                string output = await outputTask.ConfigureAwait(false);
                string error = await errorTask.ConfigureAwait(false);
                await AppendToLogWithRetryAsync(logFilePath, $"[PowerShell] {script}\n{output}\n{error}\n", token).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(output) ? error : output.Trim();
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
                return "Execution Cancelled";
            }
        }
        catch (Exception ex)
        {
            await AppendToLogWithRetryAsync(logFilePath, $"[PowerShell Error] {script}\n{ex.Message}\n", CancellationToken.None).ConfigureAwait(false);
            return "ERROR: " + ex.Message;
        }
    }

    private async Task<List<TestResult>> RunDnsCheckAsync(string dc, string logFilePath, CancellationToken token)
    {
        List<TestResult> results = new();
        try
        {
            string output = await RunCommandAsync($"nslookup {dc}", logFilePath, token);
            bool passed = output.Contains("Name:") && !output.Contains("server can't find") && !output.Contains("Non-existent domain");
            results.Add(new TestResult { Service = "DNS Resolution", Server = dc, Result = passed ? "PASS" : "FAIL", Message = passed ? "DNS resolution successful." : "DNS resolution failed.", LogFilePath = logFilePath });
        }
        catch { results.Add(new TestResult { Service = "DNS Resolution", Server = dc, Result = "FAIL", Message = "DNS check threw exception.", LogFilePath = logFilePath }); }
        return results;
    }

    internal static bool EvaluateTimeSkewResult(string output)
    {
        bool hasFailed = output.Contains("FAILED", StringComparison.OrdinalIgnoreCase);
        bool hasErrorCode = output.Contains("error code", StringComparison.OrdinalIgnoreCase);
        bool hasLastError = output.Contains("last error", StringComparison.OrdinalIgnoreCase);
        bool hasActualError = output.Contains("has an error", StringComparison.OrdinalIgnoreCase);
        bool hasSpecificError = hasFailed || hasErrorCode || hasLastError || hasActualError;

        return !hasSpecificError &&
               (output.Contains("offset", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("RefID", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<TestResult>> RunTimeSkewCheckAsync(string dc, string logFilePath, CancellationToken token)
    {
        List<TestResult> results = new();
        try
        {
            string output = await RunCommandAsync($"w32tm /monitor /computers:{dc}", logFilePath, token);
            bool passed = EvaluateTimeSkewResult(output);
            results.Add(new TestResult { Service = "Time Skew", Server = dc, Result = passed ? "PASS" : "FAIL", Message = passed ? "Time sync OK." : "Time skew detected or w32tm error.", LogFilePath = logFilePath });
        }
        catch { results.Add(new TestResult { Service = "Time Skew", Server = dc, Result = "FAIL", Message = "Time sync check failed.", LogFilePath = logFilePath }); }
        return results;
    }

    internal static bool EvaluateLdapBindResult(string output)
    {
        return output.Contains("LDAP_OK", StringComparison.OrdinalIgnoreCase) &&
               !output.Contains("LDAP_FAIL", StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildLdapBindMessage(string output, bool passed)
    {
        if (passed) return "LDAP bind succeeded.";
        return output.Contains("LDAP_FAIL", StringComparison.OrdinalIgnoreCase)
            ? output.Trim()
            : "LDAP bind failed.";
    }

    private async Task<List<TestResult>> RunLdapBindCheckAsync(string dc, string logFilePath, CancellationToken token)
    {
        List<TestResult> results = new();
        try
        {
            string script =
                "$OutputEncoding = [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
                $"try {{ $root = [ADSI]\"LDAP://{dc}\"; $dn = $root.distinguishedName; Write-Output \"LDAP_OK: $dn\" }} catch {{ Write-Output \"LDAP_FAIL: $($_.Exception.Message)\" }}";
            string output = await RunPowerShellScriptAsync(script, logFilePath, token);
            bool passed = EvaluateLdapBindResult(output);
            string message = BuildLdapBindMessage(output, passed);
            results.Add(new TestResult { Service = "LDAP Bind", Server = dc, Result = passed ? "PASS" : "FAIL", Message = message, LogFilePath = logFilePath });
        }
        catch { results.Add(new TestResult { Service = "LDAP Bind", Server = dc, Result = "FAIL", Message = "LDAP bind threw exception.", LogFilePath = logFilePath }); }
        return results;
    }

    private async Task<List<TestResult>> RunCertDhcpCheckAsync(string dc, string logFilePath, CancellationToken token)
    {
        List<TestResult> results = new();
        try
        {
            string script = "@('ADCS','DhcpServer') | ForEach-Object { $s = Get-Service $_ -ErrorAction SilentlyContinue; if ($s) { \"$($s.Name)=$($s.Status)\" } else { \"$_=NotInstalled\" } }";
            string output = await RunPowerShellScriptAsync(script, logFilePath, token);
            bool certOk = output.Contains("ADCS=Running") || !output.Contains("ADCS");
            bool dhcpOk = output.Contains("DhcpServer=Running") || !output.Contains("DhcpServer");
            results.Add(new TestResult { Service = "Cert Services & DHCP", Server = dc, Result = (certOk && dhcpOk) ? "PASS" : "FAIL", Message = $"ADCS: {(certOk ? "OK" : "Issue")}, DHCP: {(dhcpOk ? "OK" : "Issue")}", LogFilePath = logFilePath });
        }
        catch { results.Add(new TestResult { Service = "Cert Services & DHCP", Server = dc, Result = "FAIL", Message = "Service check threw exception.", LogFilePath = logFilePath }); }
        return results;
    }

    internal static bool EvaluateSmbResult(string output)
    {
        return output.Contains("SMB_OK", StringComparison.OrdinalIgnoreCase) &&
               !output.Contains("SMB_FAIL", StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildSmbMessage(string output, bool passed)
    {
        if (passed) return "Server service running.";
        return output.Contains("SMB_FAIL", StringComparison.OrdinalIgnoreCase)
            ? output.Trim()
            : "Server service not running.";
    }

    private async Task<List<TestResult>> RunSmbLdapSigningCheckAsync(string dc, string logFilePath, CancellationToken token)
    {
        List<TestResult> results = new();
        try
        {
            string script =
                $"try {{ $s = Get-Service -ComputerName {dc} -Name LanmanServer -ErrorAction Stop; if ($s.Status -eq 'Running') {{ 'SMB_OK' }} else {{ 'SMB_STATUS=' + $s.Status }} }} catch {{ Write-Output \"SMB_FAIL: $($_.Exception.Message)\" }}";
            string output = await RunPowerShellScriptAsync(script, logFilePath, token);
            bool passed = EvaluateSmbResult(output);
            string message = BuildSmbMessage(output, passed);
            results.Add(new TestResult { Service = "SMB/LDAP Signing", Server = dc, Result = passed ? "PASS" : "FAIL", Message = message, LogFilePath = logFilePath });
        }
        catch { results.Add(new TestResult { Service = "SMB/LDAP Signing", Server = dc, Result = "FAIL", Message = "Signing check threw exception.", LogFilePath = logFilePath }); }
        return results;
    }

    internal void ViewSelectedLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            List<TestResult> selected = new();
            foreach (object item in testResultsGrid.SelectedItems)
            {
                if (item is TestResult tr)
                    selected.Add(tr);
            }
            if (selected.Count == 0)
            {
                new SuccessNotification("No Selection", "No results selected. Check the checkbox(es) to select result(s) to view.", isError: true).ShowDialog();
                return;
            }

            List<TestResult> validResults = new();
            for (int i = 0; i < selected.Count; i++)
            {
                TestResult r = selected[i];
                if (!string.IsNullOrWhiteSpace(r.LogFilePath) && File.Exists(r.LogFilePath))
                    validResults.Add(r);
            }
            if (validResults.Count == 0)
            {
                if (isRunInProgress || !isLogContentReady)
                {
                    NotificationService.Show(this, "Logs Still Loading", "Log files are still being written. Please wait for the test run to complete, then try again.");
                }
                else
                {
                    NotificationService.Show(this, "Log Not Found", "No log files found for the selected results.", isError: true);
                }
                return;
            }

            // Manual distinct: avoids LINQ Select/Distinct/ToList allocations
            List<string> uniqueLogFiles = new();
            HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < validResults.Count; i++)
            {
                if (seenPaths.Add(validResults[i].LogFilePath))
                    uniqueLogFiles.Add(validResults[i].LogFilePath);
            }

            if (uniqueLogFiles.Count > 1)
            {
                latestLogsText = BuildMergedLogText(uniqueLogFiles);
                latestLogsFilePath = uniqueLogFiles[0];
                RefreshLogSectionEntries(latestLogsText, latestLogsFilePath);
                LogsFileNameText.Text = $"Combined full logs from {uniqueLogFiles.Count} files";
                logsTextPending = false;
                NavigateToSection(5);
                return;
            }

            HashSet<string> processedFiles = new();
            List<string> extractedSections = new();

            foreach (TestResult result in validResults)
            {
                string key = $"{result.LogFilePath}|{result.Service ?? ""}";
                if (!processedFiles.Add(key)) continue;

                List<string> section = new();
                bool capture = false;

                foreach (string line in File.ReadLines(result.LogFilePath))
                {
                    if (!capture)
                    {
                        if (line.Trim().StartsWith("Starting test:", StringComparison.OrdinalIgnoreCase) &&
                            line.IndexOf(result.Service ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            capture = true;
                            section.Add(line);
                        }
                        continue;
                    }

                    if (line.Trim().StartsWith("Starting test:", StringComparison.OrdinalIgnoreCase))
                        break;

                    section.Add(line);
                    if (line.IndexOf("End of test", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        line.IndexOf("Test completed", StringComparison.OrdinalIgnoreCase) >= 0)
                        break;
                }

                if (section.Count > 0)
                {
                    extractedSections.Add($"--- {result.Service} ({Path.GetFileName(result.LogFilePath)}) ---");
                    extractedSections.AddRange(section);
                    extractedSections.Add("");
                }
            }

            if (extractedSections.Count == 0)
            {
                NotificationService.Show(this, "Log Not Found", "Could not extract any log sections for the selected results.", isError: true);
                return;
            }

            string combinedText = string.Join("\r\n", extractedSections);
            latestLogsText = combinedText;
            latestLogsFilePath = validResults[0].LogFilePath;
            RefreshLogSectionEntries(combinedText, latestLogsFilePath);
            HashSet<string> fileNamesSeen = new(StringComparer.OrdinalIgnoreCase);
            List<string> fileNamesList = new();
            for (int i = 0; i < validResults.Count; i++)
            {
                string fn = Path.GetFileName(validResults[i].LogFilePath);
                if (fileNamesSeen.Add(fn)) fileNamesList.Add(fn);
            }
            string fileSources = string.Join(", ", fileNamesList);
            HashSet<string> svcSeen = new(StringComparer.OrdinalIgnoreCase);
            List<string> svcList = new();
            for (int i = 0; i < selected.Count; i++)
            {
                string? svc = selected[i].Service;
                if (!string.IsNullOrWhiteSpace(svc) && svcSeen.Add(svc))
                    svcList.Add(svc);
            }
            string serviceSources = string.Join(", ", svcList);
            LogsFileNameText.Text = $"({validResults.Count} result(s)) {serviceSources} — {fileSources}";
            logsTextPending = false;
            NavigateToSection(5);
        }
        catch (Exception ex)
        {
            NotificationService.Show(this, "Error", $"Error loading log: {ex.Message}", isError: true);
        }
    }

    private static string BuildMergedLogText(IEnumerable<string> logFilePaths)
    {
        StringBuilder builder = new();
        foreach (string logFilePath in logFilePaths)
        {
            builder.AppendLine($"--- {Path.GetFileName(logFilePath)} ---");
            foreach (string line in File.ReadLines(logFilePath))
            {
                builder.AppendLine(line);
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

}
