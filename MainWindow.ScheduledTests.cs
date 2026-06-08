// MainWindow partial class - ScheduledTests functionality
// Extracted from MainWindow.xaml.cs during partial class refactoring.

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
    private async Task RunScheduledTestsAsync(string scheduledTaskName)
    {
        Stopwatch runStopwatch = Stopwatch.StartNew();
        DateTime runStartedAt = DateTime.Now;
        cancellationTokenSource?.Dispose();
        cancellationTokenSource = new CancellationTokenSource();
        CancellationToken token = cancellationTokenSource.Token;
        allResults.Clear();

        try
        {
            string[] dcList = domainControllers
                .Split(',')
                .Select(dc => dc.Trim())
                .Where(dc => !string.IsNullOrWhiteSpace(dc))
                .ToArray();

            List<string> logFilePaths = new();
            RunLogSession runSession = CreateRunLogSession(runStartedAt, scheduledTaskName);

            foreach (string dc in dcList)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                string logFilePath = GetControllerLogPath(runSession, dc);

                string dcdiagResult = await RunCommandAsync($"dcdiag /s:{dc} /c /v", logFilePath, token);
                allResults.AddRange(ParseDCDiagOutput(dc, dcdiagResult, logFilePath));
                logFilePaths.Add(logFilePath);

                if (testDnsCheck)
                    allResults.AddRange(await RunDnsCheckAsync(dc, logFilePath, token));

                if (testReplication)
                {
                    string replOutput = await RunCommandAsync($"repadmin /showrepl {dc}", logFilePath, token);
                    allResults.AddRange(ParseDCDiagOutput(dc, replOutput, logFilePath));
                }

                if (testTimeSkew)
                    allResults.AddRange(await RunTimeSkewCheckAsync(dc, logFilePath, token));

                if (testLdapBind)
                    allResults.AddRange(await RunLdapBindCheckAsync(dc, logFilePath, token));

                if (testCertDhcp)
                    allResults.AddRange(await RunCertDhcpCheckAsync(dc, logFilePath, token));

                if (testSmbLdapSigning)
                    allResults.AddRange(await RunSmbLdapSigningCheckAsync(dc, logFilePath, token));
            }

            await CollectSupplementalDataAsync(token);
            RebuildFindings();

        string combinedLogPath = runSession.CombinedLogPath;
        await WriteCombinedLogAsync(logFilePaths, combinedLogPath, token);
        latestLogsFilePath = combinedLogPath;
        latestLogsText = File.Exists(combinedLogPath) ? await File.ReadAllTextAsync(combinedLogPath, token).ConfigureAwait(true) : string.Empty;
        isLogContentReady = true;

        if (!allResults.Any())
        {
            return;
        }

        int total = allResults.Count;
        int passed = allResults.Count(r => r.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase));
        int failed = allResults.Count(r => r.Result.Equals("FAIL", StringComparison.OrdinalIgnoreCase));
        string summary = BuildRunSummary(total, passed, failed, dcList);
        string emailAttachment = await WriteResultsSummaryAsync(runSession, allResults, summary, token).ConfigureAwait(true);

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

            string subject = failed > 0
                ? $"[FAILED] Scheduled Test Completed - {scheduledTaskName}"
                : $"Scheduled Test Completed - {scheduledTaskName}";

            if (sendEmailScheduled && !string.IsNullOrWhiteSpace(recipientEmail))
            {
                await SendScheduledEmailSafelyAsync(subject, bodyDetail, emailAttachment).ConfigureAwait(true);
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
            Debug.WriteLine($"Scheduled run '{scheduledTaskName}' completed in {runStopwatch.ElapsedMilliseconds}ms.");
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"Scheduled run '{scheduledTaskName}' was cancelled.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Scheduled run '{scheduledTaskName}' failed: {ex}");
        }
        finally
        {
            Application.Current.Dispatcher.Invoke(Application.Current.Shutdown);
        }
    }

    public async Task ShowScheduledResultsAsync(string logFilePath)
    {
        Visibility = Visibility.Visible;
        ShowInTaskbar = true;
        Show();
        Activate();

        if (!string.IsNullOrWhiteSpace(logFilePath) && File.Exists(logFilePath))
        {
            await RunWithLoadingWindowAsync(
                "Loading run results",
                "Parsing the selected run and preparing the dashboard.",
                () => LoadScheduledResultsFromLogAsync(logFilePath)).ConfigureAwait(true);
        }
    }

    public async Task LoadScheduledResultsFromLogAsync(string logFilePath)
    {
        if (string.IsNullOrWhiteSpace(logFilePath) || !File.Exists(logFilePath))
        {
            NotificationService.Show(this, "View Results", "Scheduled log file not found.", isError: true);
            return;
        }

        Stopwatch loadStopwatch = Stopwatch.StartNew();
        DateTime lastWriteUtc = File.GetLastWriteTimeUtc(logFilePath);
        string output;
        List<TestResult> parsedResults;
        if (scheduledLogCache.TryGetValue(logFilePath, out CachedScheduledLog? cachedLog) &&
            cachedLog.LastWriteUtc == lastWriteUtc)
        {
            output = cachedLog.Text;
            parsedResults = cachedLog.Results;
        }
        else
        {
            output = await File.ReadAllTextAsync(logFilePath).ConfigureAwait(true);
            parsedResults = await Task.Run(
                () => ParseDCDiagOutput("Scheduled", output, logFilePath).ToList()).ConfigureAwait(true);
            if (scheduledLogCache.Count >= 20)
            {
                RemoveOldestScheduledLogCacheEntry();
            }
            scheduledLogCache[logFilePath] = new CachedScheduledLog
            {
                LastWriteUtc = lastWriteUtc,
                Text = output,
                Results = parsedResults
            };
        }

        allResults.Clear();
        allResults.AddRange(parsedResults);
        RebuildFindings();
        SyncResultItems();
        latestLogsFilePath = logFilePath;
        latestLogsText = output;
        isLogContentReady = true;
        DisplayTestResults(output);
        ForceRefreshDashboard();
        Debug.WriteLine($"Scheduled log '{Path.GetFileName(logFilePath)}' loaded in {loadStopwatch.ElapsedMilliseconds}ms.");
        Activate();
    }

    private async void ScheduleTestsButton_Click(object sender, RoutedEventArgs e)
    {
        await NavigateToSectionAsync(8).ConfigureAwait(true);
    }

    private void LoadSchedulerTasks()
    {
        try
        {
            scheduledTasks.Clear();
            scheduledTasks.AddRange(appStateStore.LoadScheduledTasks());
        }
        catch { }
    }

    private void SaveSchedulerTasks()
    {
        try
        {
            appStateStore.SaveScheduledTasks(scheduledTasks);
        }
        catch { }
    }

    private Task PersistScheduledTasksAsync()
    {
        List<ScheduledTask> snapshot = scheduledTasks.ToList();
        return Task.Run(() => appStateStore.SaveScheduledTasks(snapshot));
    }

    private void RefreshSchedulerTaskList()
    {
        if (!schedulerTasksLoaded || _SchedulerTab == null)
        {
            return;
        }

        SchedulerTaskList.Items.Refresh();
        UpdateSelectAllCheckbox(SchedulerTaskList);
    }

    private void ClearSchedulerInputFields()
    {
        SchedulerTaskName.Text = string.Empty;
        SchedulerDomainControllers.Text = string.Empty;
        SchedulerFrequency.SelectedIndex = -1;
        SchedulerStartDate.SelectedDate = null;
        SchedulerStartTime.Text = string.Empty;
        SchedulerTaskList.SelectedIndex = -1;
        schedulerSelectedTaskIndex = -1;
    }

    internal void SchedulerTaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectAllCheckbox(SchedulerTaskList);

        if (SchedulerTaskList.SelectedIndex >= 0)
        {
            ScheduledTask task = scheduledTasks[SchedulerTaskList.SelectedIndex];
            SchedulerTaskName.Text = task.TaskName;
            SchedulerDomainControllers.Text = task.DomainController;

            foreach (ComboBoxItem item in SchedulerFrequency.Items)
            {
                if (string.Equals(item.Content?.ToString(), task.Frequency, StringComparison.OrdinalIgnoreCase))
                {
                    SchedulerFrequency.SelectedItem = item;
                    break;
                }
            }

            SchedulerStartDate.SelectedDate = task.StartDate;
            SchedulerStartTime.Text = task.StartTime;
            schedulerSelectedTaskIndex = SchedulerTaskList.SelectedIndex;
        }
        else
        {
            schedulerSelectedTaskIndex = -1;
        }
    }

    private bool CreateWindowsScheduledTask(ScheduledTask task)
    {
        try
        {
            WindowsTaskSchedulerInterop.CreateOrUpdateTask(task);
            return true;
        }
        catch (Exception ex)
        {
            NotificationService.Show(this, "Task Scheduler Error", "Failed to create Windows scheduled task: " + ex.Message, isError: true);
            return false;
        }
    }

    private static void CreateWindowsScheduledTaskCore(ScheduledTask task)
    {
        WindowsTaskSchedulerInterop.CreateOrUpdateTask(task);
    }

    private bool RemoveWindowsScheduledTask(ScheduledTask task)
    {
        try
        {
            WindowsTaskSchedulerInterop.DeleteTask(task.TaskName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveWindowsScheduledTaskCore(ScheduledTask task)
    {
        WindowsTaskSchedulerInterop.DeleteTask(task.TaskName);
    }

    internal async void SchedulerSaveButton_Click(object sender, RoutedEventArgs e)
    {
        string taskName = SchedulerTaskName.Text.Trim();
        List<string> dcEntries = SchedulerDomainControllers.Text.Trim()
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(dc => dc.Trim())
            .Where(dc => !string.IsNullOrEmpty(dc))
            .ToList();

        if (dcEntries.Count == 0)
        {
            NotificationService.Show(this, "Validation Error", "Please enter at least one domain controller.", isError: true);
            return;
        }

        string domainControllers = string.Join(", ", dcEntries);
        string frequency = (SchedulerFrequency.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
        DateTime? startDate = SchedulerStartDate.SelectedDate;
        string startTime = SchedulerStartTime.Text.Trim();

        if (string.IsNullOrEmpty(taskName) || string.IsNullOrEmpty(domainControllers) || string.IsNullOrEmpty(frequency) || !startDate.HasValue || string.IsNullOrEmpty(startTime))
        {
            NotificationService.Show(this, "Validation Error", "Please fill in all fields.", isError: true);
            return;
        }

        ScheduledTask newTask = new()
        {
            TaskName = taskName,
            DomainController = domainControllers,
            Frequency = frequency,
            StartDate = startDate.Value,
            StartTime = startTime
        };

        try
        {
            if (schedulerSelectedTaskIndex >= 0)
            {
                ScheduledTask oldTask = scheduledTasks[schedulerSelectedTaskIndex];
                await RunWithLoadingWindowAsync(
                    "Updating scheduled task",
                    "Updating the Windows scheduled task and saved app state.",
                    async () =>
                    {
                        await Task.Run(() => RemoveWindowsScheduledTaskCore(oldTask)).ConfigureAwait(true);
                        await Task.Run(() => CreateWindowsScheduledTaskCore(newTask)).ConfigureAwait(true);
                    }).ConfigureAwait(true);

                scheduledTasks[schedulerSelectedTaskIndex] = newTask;
                schedulerSelectedTaskIndex = -1;
            }
            else
            {
                await RunWithLoadingWindowAsync(
                    "Saving scheduled task",
                    "Creating the Windows scheduled task and saving it locally.",
                    () => Task.Run(() => CreateWindowsScheduledTaskCore(newTask))).ConfigureAwait(true);
                scheduledTasks.Add(newTask);
            }
        }
        catch (Exception ex)
        {
            NotificationService.Show(this, "Task Scheduler Error", "Failed to save the Windows scheduled task: " + ex.Message, isError: true);
            return;
        }

        await PersistScheduledTasksAsync().ConfigureAwait(true);
        RefreshSchedulerTaskList();
        ClearSchedulerInputFields();
    }

    internal async void SchedulerRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (SchedulerTaskList.SelectedIndex >= 0)
        {
            ScheduledTask task = scheduledTasks[SchedulerTaskList.SelectedIndex];
            try
            {
                await RunWithLoadingWindowAsync(
                    "Removing scheduled task",
                    "Deleting the Windows scheduled task and updating saved state.",
                    () => Task.Run(() => RemoveWindowsScheduledTaskCore(task))).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                NotificationService.Show(this, "Task Scheduler Error", "Failed to remove the Windows scheduled task: " + ex.Message, isError: true);
                return;
            }

            scheduledTasks.RemoveAt(SchedulerTaskList.SelectedIndex);
            await PersistScheduledTasksAsync().ConfigureAwait(true);
            RefreshSchedulerTaskList();
            ClearSchedulerInputFields();
        }
        else
        {
            NotificationService.Show(this, "Remove Task", "Please select a task to remove.", isError: true);
        }
    }

}
