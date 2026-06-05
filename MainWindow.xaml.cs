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

[SupportedOSPlatform("windows")]
public partial class MainWindow : Window, IDisposable
{
    private bool isSidebarCollapsed;
    private bool _disposed;

    public class GitHubRelease
    {
        public string tag_name { get; set; } = string.Empty;
        public string html_url { get; set; } = string.Empty;
        public GitHubAsset[] assets { get; set; } = Array.Empty<GitHubAsset>();
    }

    public class GitHubAsset
    {
        public string name { get; set; } = string.Empty;
        public string browser_download_url { get; set; } = string.Empty;
    }

    private sealed class CachedScheduledLog
    {
        public DateTime LastWriteUtc { get; init; }
        public string Text { get; init; } = string.Empty;
        public List<TestResult> Results { get; init; } = new();
    }

    private sealed class ParsedLogSection
    {
        public string Service { get; set; } = string.Empty;
        public string Server { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
        public List<string> Lines { get; } = new();
    }

    private static bool scheduledRunStarted;
    private readonly bool isScheduledLaunch;

    private string domainControllers = string.Empty;
    private string recipientEmail = string.Empty;
    private bool testDnsCheck = true;
    private bool testReplication = true;
    private bool testTimeSkew = true;
    private bool testLdapBind = true;
    private bool testCertDhcp = true;
    private bool testSmbLdapSigning = true;
    private bool sendEmailManual = true;
    private bool sendEmailScheduled = true;
    private CancellationTokenSource cancellationTokenSource;
    private readonly List<TestResult> allResults = new();
    private readonly List<AdHealthFinding> allFindings = new();
    private readonly ObservableCollection<TestResult> resultItems = new();
    private readonly ObservableCollection<TestResult> logResultItems = new();
    private readonly ObservableCollection<TestHistoryEntry> historyItems = new();
    private readonly ObservableCollection<AdHealthFinding> findingItems = new();
    private readonly ObservableCollection<AdHealthFinding> securityFindingItems = new();
    private readonly AdReconStyleCollector inventoryCollector = new();
    private readonly WindowsTelemetryCollector telemetryCollector = new();
    private readonly AppStateStore appStateStore;
    private List<TestHistoryEntry> historyEntries = new();
    private AdInventorySnapshot latestInventory = AdInventorySnapshot.Empty;
    private TelemetrySnapshot latestTelemetry = TelemetrySnapshot.Empty;
    private DashboardSnapshot? cachedDashboardSnapshot;
    private const string LogDirectoryPath = @"C:\ADCheckLogs";
    private const string RunLogsDirectoryName = "runs";

    private readonly List<ScheduledTask> scheduledTasks = new();
    private static readonly Brush ActiveNavBgBrush = FrozenBrush(Color.FromRgb(26, 115, 232));
    private static readonly Brush InactiveNavFgBrush = FrozenBrush(Color.FromRgb(176, 188, 201));
    private static Brush FrozenBrush(Color color) { var b = new SolidColorBrush(color); b.Freeze(); return b; }
    private int schedulerSelectedTaskIndex = -1;
    private readonly DispatcherTimer dashboardRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private ICollectionView? resultItemsView;
    private ICollectionView? logResultItemsView;
    private ICollectionView? historyItemsView;
    private ICollectionView? findingItemsView;
    private string latestRunDetailsText = string.Empty;
    private string latestLogsText = string.Empty;
    private string latestLogsFilePath = string.Empty;
    private bool logsTextPending;
    private bool suppressLogsFilterEvents;
    private bool suppressLogsWorkspaceRefresh;
    private bool schedulerTasksLoaded;
    private bool healthPageBound;
    private bool findingsPageBound;
    private bool historyPageBound;
    private bool logsPageBound;
    private bool securityPageBound;
    private bool schedulerPageBound;
    private bool healthDetailsTextPending;
    private Task? startupInitializationTask;
    private int cachedLogLinesHash;
    private int cachedLogLinesLength = -1;
    private IReadOnlyList<LogLine> cachedLogLines = Array.Empty<LogLine>();
    private readonly Dictionary<string, CachedScheduledLog> scheduledLogCache = new(StringComparer.OrdinalIgnoreCase);
    private DateTime lastProgressUiUpdateUtc = DateTime.MinValue;
    private int lastProgressCompletedSteps = -1;
    private string lastProgressTitle = string.Empty;
    private string lastProgressDetail = string.Empty;
    private bool isRunInProgress;
    private bool isLogContentReady;
    private string lastEmailFingerprint = string.Empty;
    private DateTime lastEmailSentUtc = DateTime.MinValue;
    private static readonly TimeSpan ScheduledCollectorTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ScheduledEmailTimeout = TimeSpan.FromSeconds(15);
    private int currentVisibleLogLineCount;
    private int currentVisibleSectionCount;
    private int currentVisibleControllerCount;

    private sealed class RunLogSession
    {
        public DateTime StartedAt { get; init; }
        public string TestType { get; init; } = string.Empty;
        public string RunDirectoryPath { get; init; } = string.Empty;
        public string CombinedLogPath { get; init; } = string.Empty;
    }

    public MainWindow()
    {
        string[] args = Environment.GetCommandLineArgs();
        isScheduledLaunch = args.Length > 1 && args[1].Equals("-scheduled", StringComparison.OrdinalIgnoreCase);
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-GB");
        Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-GB");
        appStateStore = new AppStateStore(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdHealthMonitor", "AppState.db"));

        InitializeComponent();
        SetupAdminWarningBanner();
        Stopwatch startupStopwatch = Stopwatch.StartNew();
        dashboardRefreshTimer.Tick += DashboardRefreshTimer_Tick;
        UpdateActionButtonStates();
        InitializeBoundViews();
        cancellationTokenSource = new CancellationTokenSource();
        UpdateNavigationState();
        startupInitializationTask = isScheduledLaunch
            ? InitializeAppStateAsync(startupStopwatch)
            : DeferStartupInitializationAsync(startupStopwatch);

        if (isScheduledLaunch)
        {
            if (scheduledRunStarted) return;
            scheduledRunStarted = true;
            string scheduledTaskName = args.Length > 2
                ? string.Join(" ", args.Skip(2)).Trim().Trim('"')
                : "Scheduled Task Completed";
            Visibility = Visibility.Hidden;
            ShowInTaskbar = false;
            _ = Dispatcher.BeginInvoke(async () =>
            {
                if (startupInitializationTask != null)
                {
                    await startupInitializationTask.ConfigureAwait(true);
                }

                await RunScheduledTestsAsync(scheduledTaskName).ConfigureAwait(true);
            }, DispatcherPriority.Background);
        }
    }

    private void SetupAdminWarningBanner()
    {
        if (App.IsRunningAsAdmin || isScheduledLaunch)
        {
            return;
        }

        Grid? mainGrid = ProgressPanel.Parent as Grid;
        if (mainGrid == null)
        {
            return;
        }

        mainGrid.RowDefinitions.Insert(1, new RowDefinition { Height = GridLength.Auto });

        foreach (UIElement child in mainGrid.Children)
        {
            int currentRow = Grid.GetRow(child);
            if (currentRow >= 1)
            {
                Grid.SetRow(child, currentRow + 1);
            }
        }

        Border banner = new()
        {
            Background = new SolidColorBrush(Color.FromRgb(255, 248, 230)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(255, 183, 77)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 12)
        };

        StackPanel panel = new() { Orientation = Orientation.Horizontal };

        TextBlock icon = new()
        {
            Text = "\uE7BA",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 18,
            Foreground = new SolidColorBrush(Color.FromRgb(230, 126, 34)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };

        TextBlock message = new()
        {
            Text = "Running without administrator privileges.  Diagnostic tests will require elevation.",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(102, 60, 0)),
            VerticalAlignment = VerticalAlignment.Center
        };

        Button elevateButton = new()
        {
            Content = "Relaunch as Admin",
            Style = (Style)FindResource("RoundedButtonStyle"),
            Background = new SolidColorBrush(Color.FromRgb(255, 152, 0)),
            Height = 30,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(16, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        elevateButton.Click += (_, _) =>
        {
            if (App.TryRelaunchAsAdmin())
            {
                Application.Current.Shutdown();
            }
            else
            {
                NotificationService.Show(this, "Elevation Failed", "Could not relaunch as administrator.", isError: true);
            }
        };

        panel.Children.Add(icon);
        panel.Children.Add(message);
        panel.Children.Add(elevateButton);
        banner.Child = panel;

        Grid.SetRow(banner, 1);
        mainGrid.Children.Add(banner);
    }

    private async Task DeferStartupInitializationAsync(Stopwatch startupStopwatch)
    {
        await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
        await InitializeAppStateAsync(startupStopwatch).ConfigureAwait(true);
        await PrewarmPriorityPagesAsync().ConfigureAwait(true);
    }

    private async Task PrewarmPriorityPagesAsync()
    {
        await Dispatcher.InvokeAsync(() =>
        {
            EnsurePageBindings(1);
        }, DispatcherPriority.ApplicationIdle);
    }

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
        TestResult lastResult = allResults.Last();
        string bodyDetail =
            $"<p><strong>Service:</strong> {lastResult.Service}</p>\r\n" +
            $"<p><strong>Server:</strong> {lastResult.Server}</p>\r\n" +
            $"<p><strong>Result:</strong> {lastResult.Result}</p>\r\n" +
            $"<p><strong>Message:</strong> {lastResult.Message}</p>" +
            "<br/><br/><strong>Summary:</strong><br/>" + summary.Replace("\n", "<br/>");

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

    public void DisplayTestResults(string results)
    {
        latestRunDetailsText = results;
        if (string.IsNullOrWhiteSpace(latestLogsText) && string.IsNullOrWhiteSpace(latestLogsFilePath))
        {
            latestLogsText = results;
        }

        healthDetailsTextPending = MainTabControl.SelectedIndex != 1;
        if (healthDetailsTextPending)
        {
            ResultsTextBox.Clear();
        }
        else
        {
            ResultsTextBox.Text = results;
        }

        if (MainTabControl.SelectedIndex == 5)
        {
            LogsListBox.ItemsSource = GetCachedLogLines(results);
            logsTextPending = false;
        }
        else
        {
            logsTextPending = true;
        }

        DetailsPanel.Visibility = string.IsNullOrWhiteSpace(results) ? Visibility.Collapsed : Visibility.Visible;
        UpdateHealthResultsLayout();
        UpdateHealthSummaryText();
        Activate();
    }

    private void EnsureHealthDetailsTextLoaded()
    {
        if (!healthDetailsTextPending)
        {
            return;
        }

        ResultsTextBox.Text = latestRunDetailsText;
        healthDetailsTextPending = false;
        UpdateHealthResultsLayout();
    }

    private void UpdateHealthResultsLayout()
    {
        bool showDetails = DetailsPanel.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(latestRunDetailsText);
        DetailsPanel.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateHealthSummaryText()
    {
        if (HealthSummaryText == null)
        {
            return;
        }

        int configuredControllers = CountConfiguredDomainControllers();
        int visibleControllers = allResults
            .Select(result => result.Server)
            .Where(server => !string.IsNullOrWhiteSpace(server))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        int total = allResults.Count;
        int passed = allResults.Count(r => r.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase));
        int failed = allResults.Count(r => r.Result.Equals("FAIL", StringComparison.OrdinalIgnoreCase));
        int activeFindings = GetActiveFindings().Count();

        if (total == 0)
        {
            HealthSummaryText.Text = "No run data loaded yet.";
            return;
        }

        HealthSummaryText.Text =
            $"Current view shows {total} test result(s) across {visibleControllers} of {configuredControllers} configured domain controller(s). " +
            $"{passed} passed, {failed} failed, and {activeFindings} actionable finding(s) are open. Each result row represents one test section, not a whole controller run.";
    }

    private void SetRunInProgress(bool inProgress)
    {
        isRunInProgress = inProgress;
        Mouse.OverrideCursor = inProgress ? Cursors.Wait : null;
        if (ProgressPanel != null)
        {
            ProgressPanel.IsEnabled = true;
        }

        UpdateActionButtonStates();
    }

    private void ShowRunProgress(string title, string detail, int completedSteps, int totalSteps)
    {
        DateTime now = DateTime.UtcNow;
        bool shouldRender =
            completedSteps != lastProgressCompletedSteps ||
            !string.Equals(title, lastProgressTitle, StringComparison.Ordinal) ||
            !string.Equals(detail, lastProgressDetail, StringComparison.Ordinal) ||
            (now - lastProgressUiUpdateUtc) >= TimeSpan.FromMilliseconds(100);

        if (!shouldRender)
        {
            return;
        }

        ProgressPanel.Visibility = Visibility.Visible;
        RunProgressTitleText.Text = title;
        RunProgressDetailText.Text = detail;
        int safeTotalSteps = totalSteps <= 0 ? 1 : totalSteps;
        double percentage = Math.Clamp((double)completedSteps / safeTotalSteps, 0d, 1d) * 100d;
        RunProgressPercentText.Text = $"{percentage:0}%";
        RunProgressCountText.Text = $"{Math.Min(completedSteps, safeTotalSteps)} / {safeTotalSteps} steps completed";
        TestProgressBar.Maximum = 100;
        TestProgressBar.IsIndeterminate = completedSteps <= 0;
        TestProgressBar.Value = percentage;
        lastProgressUiUpdateUtc = now;
        lastProgressCompletedSteps = completedSteps;
        lastProgressTitle = title;
        lastProgressDetail = detail;
    }

    private void HideRunProgress()
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        RunProgressTitleText.Text = "Running domain controller diagnostics";
        RunProgressDetailText.Text = "Preparing to run diagnostics.";
        RunProgressPercentText.Text = "0%";
        RunProgressCountText.Text = "0 / 0 steps completed";
        TestProgressBar.IsIndeterminate = false;
        TestProgressBar.Value = 0;
        TestProgressBar.Maximum = 100;
        lastProgressCompletedSteps = -1;
        lastProgressTitle = string.Empty;
        lastProgressDetail = string.Empty;
        lastProgressUiUpdateUtc = DateTime.MinValue;
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

    private void LoadSettings()
    {
        PersistedAppSettings settings = appStateStore.LoadSettings();
        domainControllers = settings.DomainControllers;
        recipientEmail = settings.RecipientEmail;
        testDnsCheck = settings.TestDnsCheck;
        testReplication = settings.TestReplication;
        testTimeSkew = settings.TestTimeSkew;
        testLdapBind = settings.TestLdapBind;
        testCertDhcp = settings.TestCertDhcp;
        testSmbLdapSigning = settings.TestSmbLdapSigning;
        sendEmailManual = settings.SendEmailManual;
        sendEmailScheduled = settings.SendEmailScheduled;
        RefreshDashboard();
    }

    private async Task InitializeAppStateAsync(Stopwatch startupStopwatch)
    {
        try
        {
            await Task.Run(appStateStore.Initialize).ConfigureAwait(true);

            AppStartupState startupState = await Task.Run(appStateStore.LoadStartupState).ConfigureAwait(true);

            PersistedAppSettings settings = startupState.Settings;
            domainControllers = settings.DomainControllers;
            recipientEmail = settings.RecipientEmail;
            testDnsCheck = settings.TestDnsCheck;
            testReplication = settings.TestReplication;
            testTimeSkew = settings.TestTimeSkew;
            testLdapBind = settings.TestLdapBind;
            testCertDhcp = settings.TestCertDhcp;
            testSmbLdapSigning = settings.TestSmbLdapSigning;
            sendEmailManual = settings.SendEmailManual;
            sendEmailScheduled = settings.SendEmailScheduled;

            cachedDashboardSnapshot = startupState.DashboardSnapshot;
            historyEntries = startupState.History
                .OrderByDescending(x => x.RunDate)
                .ToList();
            SyncHistoryItems(historyEntries);
            ReplaceScheduledTasks(startupState.ScheduledTasks);
            schedulerTasksLoaded = true;

            if (MainTabControl.SelectedIndex == 7)
            {
                LoadSettingsIntoPage();
            }
            else if (MainTabControl.SelectedIndex == 8)
            {
                RefreshSchedulerTaskList();
            }

            RefreshDashboardNow();
            _ = CleanupLogFilesAsync();
            Debug.WriteLine($"Startup initialization completed in {startupStopwatch.ElapsedMilliseconds}ms.");
        }
        catch (Exception ex)
        {
            if (isScheduledLaunch)
            {
                Debug.WriteLine("Error loading application state during scheduled launch: " + ex);
            }
            else
            {
                NotificationService.Show(this, "Startup Error", "Error loading application state: " + ex.Message, isError: true);
            }
        }
    }

    private void LoadCachedDashboardSnapshot()
    {
        try
        {
            cachedDashboardSnapshot = appStateStore.LoadDashboardSnapshot();
        }
        catch
        {
            cachedDashboardSnapshot = null;
        }
    }

    private void SaveSettings()
    {
        appStateStore.SaveSettings(new PersistedAppSettings
        {
            DomainControllers = domainControllers,
            RecipientEmail = recipientEmail,
            TestDnsCheck = testDnsCheck,
            TestReplication = testReplication,
            TestTimeSkew = testTimeSkew,
            TestLdapBind = testLdapBind,
            TestCertDhcp = testCertDhcp,
            TestSmbLdapSigning = testSmbLdapSigning,
            SendEmailManual = sendEmailManual,
            SendEmailScheduled = sendEmailScheduled
        });
    }

    private async Task SaveTestHistoryAsync(TestHistoryEntry entry)
    {
        try
        {
            TestHistoryEntry? existingDuplicate = historyEntries.FirstOrDefault(existing =>
                existing.Total == entry.Total &&
                existing.Passed == entry.Passed &&
                existing.Failed == entry.Failed &&
                string.Equals(existing.TestType, entry.TestType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.Details, entry.Details, StringComparison.Ordinal) &&
                string.Equals(existing.LogFilePath, entry.LogFilePath, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs((existing.RunDate - entry.RunDate).TotalMinutes) < 2);

            if (existingDuplicate != null)
            {
                return;
            }

            historyEntries.Add(entry);
            historyEntries = historyEntries
                .OrderByDescending(x => x.RunDate)
                .ToList();
            if (!isScheduledLaunch)
            {
                SyncHistoryItems(historyEntries);
                RefreshDashboard();
            }
            await PersistDashboardSnapshotAsync().ConfigureAwait(true);
            await PersistHistoryAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (isScheduledLaunch)
            {
                Debug.WriteLine("Error saving test history during scheduled launch: " + ex);
            }
            else
            {
                NotificationService.Show(this, "Error", "Error saving test history: " + ex.Message, isError: true);
            }
        }
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            List<TestHistoryEntry> loadedHistory = await Task.Run(appStateStore.LoadHistory).ConfigureAwait(true);

            historyEntries = loadedHistory
                .OrderByDescending(x => x.RunDate)
                .ToList();
            SyncHistoryItems(historyEntries);
            RefreshDashboard();
        }
        catch (Exception ex)
        {
            NotificationService.Show(this, "Error", "Error loading test history: " + ex.Message, isError: true);
        }
    }

    internal void dpHistoryFilter_SelectedDateChanged(object sender, SelectionChangedEventArgs e) => ApplyHistoryFilter();

    internal void txtHistorySearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyHistoryFilter();

    internal void ClearHistoryFilters_Click(object sender, RoutedEventArgs e)
    {
        dpHistoryFilter.SelectedDate = null;
        txtHistorySearch.Text = string.Empty;
        historyItemsView?.Refresh();
    }

    private void ApplyHistoryFilter() => historyItemsView?.Refresh();

    internal async void ViewSelectedHistoryRun_Click(object sender, RoutedEventArgs e)
    {
        TestHistoryEntry? entry = dgTestHistory.SelectedItems
            .OfType<TestHistoryEntry>()
            .FirstOrDefault() ?? dgTestHistory.SelectedItem as TestHistoryEntry;

        if (entry != null)
        {
            string logFilePath = entry.LogFilePath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(logFilePath))
            {
                NotificationService.Show(this, "View Selected", "The selected run does not have a log file path.");
                return;
            }

            await NavigateToSectionAsync(1).ConfigureAwait(true);
            await ShowScheduledResultsAsync(logFilePath).ConfigureAwait(true);
        }
        else
        {
            NotificationService.Show(this, "View Selected", "Please select exactly one history run to open.");
        }
    }

    internal async void DeleteSelectedHistory_Click(object sender, RoutedEventArgs e)
    {
        List<TestHistoryEntry> selectedEntries = dgTestHistory.SelectedItems
            .OfType<TestHistoryEntry>()
            .ToList();

        if (selectedEntries.Count == 0 && dgTestHistory.SelectedItem is TestHistoryEntry singleEntry)
        {
            selectedEntries.Add(singleEntry);
        }

        if (selectedEntries.Count > 0)
        {
            List<TestHistoryEntry> previousEntries = historyEntries.ToList();
            HashSet<string> removalKeys = selectedEntries
                .Select(BuildHistoryEntryKey)
                .ToHashSet(StringComparer.Ordinal);

            historyEntries = historyEntries
                .Where(entry => !removalKeys.Contains(BuildHistoryEntryKey(entry)))
                .OrderByDescending(entry => entry.RunDate)
                .ToList();

            SyncHistoryItems(historyEntries);
            RefreshDashboard();
            await PersistHistoryAsync().ConfigureAwait(true);
            await DeleteHistoryLogsAsync(selectedEntries, historyEntries, previousEntries).ConfigureAwait(true);
        }
        else
        {
            NotificationService.Show(this, "Delete History", "Please select a test history entry to delete.");
        }
    }

    internal void dgTestHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CompareRunsButton.IsEnabled = dgTestHistory.SelectedItems.Count == 2;
        ViewSelectedRunButton.IsEnabled = dgTestHistory.SelectedItems.Count == 1 ||
                                          (dgTestHistory.SelectedItems.Count == 0 && dgTestHistory.SelectedItem is TestHistoryEntry);
        UpdateSelectAllCheckbox(dgTestHistory);
    }

    private static string BuildHistoryEntryKey(TestHistoryEntry entry)
    {
        return string.Join("|",
            entry.RunDate.Ticks.ToString(CultureInfo.InvariantCulture),
            entry.TestType ?? string.Empty,
            entry.LogFilePath ?? string.Empty,
            entry.Total.ToString(CultureInfo.InvariantCulture),
            entry.Passed.ToString(CultureInfo.InvariantCulture),
            entry.Failed.ToString(CultureInfo.InvariantCulture),
            entry.Details ?? string.Empty);
    }

    private async Task DeleteHistoryLogsAsync(
        IReadOnlyCollection<TestHistoryEntry> deletedEntries,
        IReadOnlyCollection<TestHistoryEntry> remainingEntries,
        IReadOnlyCollection<TestHistoryEntry> previousEntries)
    {
        try
        {
            foreach (string deletedLogPath in deletedEntries
                .Select(entry => entry.LogFilePath)
                .Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                scheduledLogCache.Remove(Path.GetFullPath(deletedLogPath));
            }

            await Task.Run(() =>
            {
                HashSet<string> remainingLogPaths = remainingEntries
                    .Select(entry => entry.LogFilePath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(Path.GetFullPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                HashSet<string> previousLogPaths = previousEntries
                    .Select(entry => entry.LogFilePath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(Path.GetFullPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (TestHistoryEntry entry in deletedEntries)
                {
                    if (string.IsNullOrWhiteSpace(entry.LogFilePath))
                    {
                        continue;
                    }

                    string fullLogPath = Path.GetFullPath(entry.LogFilePath);
                    string? runDirectory = GetManagedRunDirectoryPath(fullLogPath);
                    if (!string.IsNullOrWhiteSpace(runDirectory))
                    {
                        bool directoryStillReferenced = remainingLogPaths
                            .Any(path => string.Equals(GetManagedRunDirectoryPath(path), runDirectory, StringComparison.OrdinalIgnoreCase));

                        if (!directoryStillReferenced && Directory.Exists(runDirectory))
                        {
                            Directory.Delete(runDirectory, true);
                        }

                        continue;
                    }

                    bool logStillReferenced = remainingLogPaths.Contains(fullLogPath);
                    if (!logStillReferenced && previousLogPaths.Contains(fullLogPath) && File.Exists(fullLogPath))
                    {
                        File.Delete(fullLogPath);
                    }
                }
            }).ConfigureAwait(true);
        }
        catch
        {
        }
    }

    internal void DataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectAllCheckbox((DataGrid)sender);
        UpdateActionButtonStates();
    }

    private static void UpdateSelectAllCheckbox(DataGrid dg)
    {
        if (dg.Columns.Count == 0 || dg.Columns[0] is not DataGridTemplateColumn col) return;
        if (col.Header is not CheckBox headerCb) return;

        headerCb.IsChecked = dg.SelectedItems.Count > 0 && dg.SelectedItems.Count == dg.Items.Count;
    }

    internal void SelectAllCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb)
        {
            DataGrid? dg = FindVisualParent<DataGrid>(cb);
            dg?.SelectAll();
        }
    }

    internal void SelectAllCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb)
        {
            DataGrid? dg = FindVisualParent<DataGrid>(cb);
            dg?.UnselectAll();
        }
    }

    internal void RowCheckBox_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is CheckBox cb)
        {
            e.Handled = true;
            DataGridRow? row = FindVisualParent<DataGridRow>(cb);
            if (row != null)
            {
                row.IsSelected = !row.IsSelected;
            }
        }
    }

    internal void RowCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb)
        {
            DataGridRow? row = FindVisualParent<DataGridRow>(cb);
            if (row != null) row.IsSelected = true;
        }
    }

    internal void RowCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb)
        {
            DataGridRow? row = FindVisualParent<DataGridRow>(cb);
            if (row != null)
            {
                row.IsSelected = false;
                DataGrid? dg = FindVisualParent<DataGrid>(cb);
                if (dg != null)
                {
                    dg.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (row.IsSelected) row.IsSelected = false;
                    }), System.Windows.Threading.DispatcherPriority.Input);
                }
            }
        }
    }

    internal void DataGrid_CellPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        if (source == null) return;

        if (FindVisualParent<CheckBox>(source) != null) return;

        DataGridCell? cell = FindVisualParent<DataGridCell>(source);
        if (cell != null) e.Handled = true;
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? parent = GetParentObject(child);
        while (parent != null && parent is not T)
        {
            parent = GetParentObject(parent);
        }

        return parent as T;
    }

    private static DependencyObject? GetParentObject(DependencyObject child)
    {
        return child switch
        {
            null => null,
            Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(child),
            FrameworkContentElement frameworkContentElement => frameworkContentElement.Parent,
            ContentElement contentElement => ContentOperations.GetParent(contentElement) ??
                                            (contentElement as FrameworkContentElement)?.Parent,
            _ => LogicalTreeHelper.GetParent(child)
        };
    }

    private void InitializeBoundViews()
    {
        resultItemsView = CollectionViewSource.GetDefaultView(resultItems);
        resultItemsView.Filter = ResultItemsFilter;
        logResultItemsView = CollectionViewSource.GetDefaultView(logResultItems);
        logResultItemsView.Filter = LogResultItemsFilter;
        historyItemsView = CollectionViewSource.GetDefaultView(historyItems);
        historyItemsView.Filter = HistoryItemsFilter;
        findingItemsView = CollectionViewSource.GetDefaultView(findingItems);
        findingItemsView.Filter = FindingItemsFilter;

        InitializeLogsFilters();
    }

    private void EnsurePageBindings(int pageIndex)
    {
        switch (pageIndex)
        {
            case 1 when !healthPageBound:
                testResultsGrid.ItemsSource = resultItemsView;
                healthPageBound = true;
                break;
            case 2 when !findingsPageBound:
                dgFindings.ItemsSource = findingItemsView;
                findingsPageBound = true;
                break;
            case 4 when !historyPageBound:
                dgTestHistory.ItemsSource = historyItemsView;
                historyPageBound = true;
                break;
            case 5 when !logsPageBound:
                dgLogsEntries.ItemsSource = logResultItemsView;
                logsPageBound = true;
                break;
            case 6 when !securityPageBound:
                dgSecurityFindings.ItemsSource = securityFindingItems;
                securityPageBound = true;
                break;
            case 8 when !schedulerPageBound:
                SchedulerTaskList.ItemsSource = scheduledTasks;
                schedulerPageBound = true;
                break;
        }
    }

    private async void DashboardRefreshTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            dashboardRefreshTimer.Stop();
            await Dispatcher.InvokeAsync(RefreshDashboardCore, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Dashboard refresh failed: {ex}");
        }
    }

    private void RefreshDashboard()
    {
        dashboardRefreshTimer.Stop();
        dashboardRefreshTimer.Start();
    }

    private void RefreshDashboardNow()
    {
        dashboardRefreshTimer.Stop();
        RefreshDashboardCore();
    }

    private void ForceRefreshDashboard()
    {
        _dashboardHash = null;
        RefreshDashboardNow();
    }

    private async Task PersistHistoryAsync()
    {
        try
        {
            List<TestHistoryEntry> snapshot = historyEntries.ToList();
            await Task.Run(() => appStateStore.SaveHistory(snapshot)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (isScheduledLaunch)
            {
                Debug.WriteLine("Error persisting history during scheduled launch: " + ex);
            }
            else
            {
                NotificationService.Show(this, "Error", "Error saving test history: " + ex.Message, isError: true);
            }
        }
    }

    private async Task PersistDashboardSnapshotAsync()
    {
        try
        {
            DashboardSnapshot snapshot = BuildDashboardSnapshot();
            cachedDashboardSnapshot = snapshot;
            await Task.Run(() => appStateStore.SaveDashboardSnapshot(snapshot)).ConfigureAwait(true);
        }
        catch
        {
        }
    }

    private void SyncResultItems()
    {
        ReplaceCollection(resultItems, allResults);
        using (resultItemsView?.DeferRefresh())
        {
        }
        ReplaceCollection(logResultItems, allResults);
        using (logResultItemsView?.DeferRefresh())
        {
        }
        if (MainTabControl.SelectedIndex == 5)
        {
            RefreshLogsWorkspace();
        }
        UpdateActionButtonStates();
    }

    private void UpdateActionButtonStates()
    {
        bool hasResults = allResults.Count > 0;
        RunButton.IsEnabled = !isRunInProgress;
        StopButton.IsEnabled = isRunInProgress;
        ExportButton.IsEnabled = hasResults;
        ExecutiveSummaryButton.IsEnabled = hasResults;
        SearchBox.IsEnabled = hasResults;
        SearchButton.IsEnabled = hasResults;
        ViewSelectedLogButton.IsEnabled = testResultsGrid.SelectedItems.Count > 0;
        OpenFindingLogButton.IsEnabled = dgFindings?.SelectedItem is AdHealthFinding finding &&
                                         !string.IsNullOrWhiteSpace(finding.LogFilePath) &&
                                         File.Exists(finding.LogFilePath);
    }

    private void SyncHistoryItems(IEnumerable<TestHistoryEntry> items)
    {
        ReplaceCollection(historyItems, items);
        using (historyItemsView?.DeferRefresh())
        {
        }
    }

    private void SyncFindingItems()
    {
        ReplaceCollection(findingItems, allFindings);
        using (findingItemsView?.DeferRefresh())
        {
        }
    }

    private void ReplaceScheduledTasks(IEnumerable<ScheduledTask> items)
    {
        scheduledTasks.Clear();
        scheduledTasks.AddRange(items);
        SchedulerTaskList?.Items.Refresh();
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        if (source is not IList<T> sourceItems)
        {
            sourceItems = source.ToList();
        }
        int index = 0;

        while (index < sourceItems.Count && index < target.Count)
        {
            if (!EqualityComparer<T>.Default.Equals(target[index], sourceItems[index]))
            {
                target[index] = sourceItems[index];
            }

            index++;
        }

        while (target.Count > sourceItems.Count)
        {
            target.RemoveAt(target.Count - 1);
        }

        while (index < sourceItems.Count)
        {
            target.Add(sourceItems[index]);
            index++;
        }
    }

    private void RemoveOldestScheduledLogCacheEntry()
    {
        if (scheduledLogCache.Count == 0)
        {
            return;
        }

        string? oldestKey = null;
        DateTime oldestWriteUtc = DateTime.MaxValue;
        foreach ((string key, CachedScheduledLog value) in scheduledLogCache)
        {
            if (value.LastWriteUtc < oldestWriteUtc)
            {
                oldestWriteUtc = value.LastWriteUtc;
                oldestKey = key;
            }
        }

        if (!string.IsNullOrWhiteSpace(oldestKey))
        {
            scheduledLogCache.Remove(oldestKey);
        }
    }

    private bool ResultItemsFilter(object item)
    {
        if (item is not TestResult result)
        {
            return false;
        }

        string searchText = SearchBox?.Text?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        return (result.Service?.ToLowerInvariant().Contains(searchText) ?? false) ||
               (result.Server?.ToLowerInvariant().Contains(searchText) ?? false) ||
               (result.Result?.ToLowerInvariant().Contains(searchText) ?? false) ||
               (result.Message?.ToLowerInvariant().Contains(searchText) ?? false);
    }

    private bool HistoryItemsFilter(object item)
    {
        if (item is not TestHistoryEntry entry)
        {
            return false;
        }

        DateTime? selectedDate = dpHistoryFilter?.SelectedDate;
        string searchText = txtHistorySearch?.Text?.Trim().ToLowerInvariant() ?? string.Empty;
        if (selectedDate.HasValue && entry.RunDate.Date != selectedDate.Value.Date)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        return (entry.Details?.ToLowerInvariant().Contains(searchText) ?? false) ||
               entry.Total.ToString(CultureInfo.InvariantCulture).Contains(searchText) ||
               entry.Passed.ToString(CultureInfo.InvariantCulture).Contains(searchText) ||
               entry.Failed.ToString(CultureInfo.InvariantCulture).Contains(searchText) ||
               (entry.TestType?.ToLowerInvariant().Contains(searchText) ?? false);
    }

    private bool FindingItemsFilter(object item)
    {
        if (item is not AdHealthFinding finding)
        {
            return false;
        }

        string searchText = FindingsSearchBox?.Text?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        return finding.Category.ToLowerInvariant().Contains(searchText) ||
               finding.Severity.ToLowerInvariant().Contains(searchText) ||
               finding.Source.ToLowerInvariant().Contains(searchText) ||
               finding.Target.ToLowerInvariant().Contains(searchText) ||
               finding.Summary.ToLowerInvariant().Contains(searchText) ||
               finding.Remediation.ToLowerInvariant().Contains(searchText);
    }

    private bool LogResultItemsFilter(object item)
    {
        if (item is not TestResult result)
        {
            return false;
        }

        string searchText = LogsSearchBox?.Text?.Trim().ToLowerInvariant() ?? string.Empty;
        string selectedDc = LogsDcFilter?.SelectedItem as string ?? "All domain controllers";
        string selectedResult = LogsResultFilter?.SelectedItem as string ?? "All Results";
        string selectedSection = LogsSectionFilter?.SelectedItem as string ?? "All test sections";

        if (!selectedDc.Equals("All domain controllers", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(result.Server, selectedDc, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (selectedResult.Equals("Failures", StringComparison.OrdinalIgnoreCase) &&
            !result.Result.Equals("FAIL", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (selectedResult.Equals("Passes", StringComparison.OrdinalIgnoreCase) &&
            !result.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!selectedSection.Equals("All test sections", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(result.Service, selectedSection, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        return (result.Service?.ToLowerInvariant().Contains(searchText) ?? false) ||
               (result.Server?.ToLowerInvariant().Contains(searchText) ?? false) ||
               (result.Result?.ToLowerInvariant().Contains(searchText) ?? false) ||
               (result.Message?.ToLowerInvariant().Contains(searchText) ?? false);
    }

    internal void CompareRuns_Click(object sender, RoutedEventArgs e)
    {
        if (dgTestHistory.SelectedItems.Count != 2)
        {
            NotificationService.Show(this, "Compare Runs", "Select exactly two history runs to compare.");
            return;
        }

        if (dgTestHistory.SelectedItems[0] is TestHistoryEntry a && dgTestHistory.SelectedItems[1] is TestHistoryEntry b)
        {
            FlowDocument doc = new() { FontFamily = new FontFamily("Consolas"), FontSize = 13 };

            Paragraph header = new(new Run("Run Comparison"))
            {
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(26, 115, 232)),
                Margin = new Thickness(0, 0, 0, 12)
            };
            doc.Blocks.Add(header);

            doc.Blocks.Add(new Paragraph(new Run($"Run A: {a.RunDate:dd MMM yyyy HH:mm} ({a.TestType})")) { FontWeight = FontWeights.SemiBold });
            doc.Blocks.Add(new Paragraph(new Run($"Run B: {b.RunDate:dd MMM yyyy HH:mm} ({b.TestType})")) { FontWeight = FontWeights.SemiBold });
            doc.Blocks.Add(new Paragraph(new Run(" ")));

            Table table = new() { CellSpacing = 0 };
            table.Columns.Add(new TableColumn { Width = new GridLength(120) });
            table.Columns.Add(new TableColumn { Width = new GridLength(80) });
            table.Columns.Add(new TableColumn { Width = new GridLength(80) });

            TableRowGroup group = new();
            TableRow headerRow = new();
            headerRow.Cells.Add(new TableCell(new Paragraph(new Run("Metric")) { FontWeight = FontWeights.Bold }));
            headerRow.Cells.Add(new TableCell(new Paragraph(new Run($"Run A ({a.RunDate:HH:mm})")) { FontWeight = FontWeights.Bold }));
            headerRow.Cells.Add(new TableCell(new Paragraph(new Run($"Run B ({b.RunDate:HH:mm})")) { FontWeight = FontWeights.Bold }));
            group.Rows.Add(headerRow);

            void AddRow(string metric, string valA, string valB)
            {
                TableRow row = new();
                row.Cells.Add(new TableCell(new Paragraph(new Run(metric))));
                row.Cells.Add(new TableCell(new Paragraph(new Run(valA))));
                row.Cells.Add(new TableCell(new Paragraph(new Run(valB))));
                group.Rows.Add(row);
            }

            AddRow("Total Tests", a.Total.ToString(), b.Total.ToString());
            AddRow("Passed", a.Passed.ToString(), b.Passed.ToString());
            AddRow("Failed", a.Failed.ToString(), b.Failed.ToString());

            int aPassRate = a.Total > 0 ? (int)((double)a.Passed / a.Total * 100) : 0;
            int bPassRate = b.Total > 0 ? (int)((double)b.Passed / b.Total * 100) : 0;
            AddRow("Pass Rate", $"{aPassRate}%", $"{bPassRate}%");

            string trend = bPassRate > aPassRate ? "▲ Improving" : bPassRate < aPassRate ? "▼ Declining" : "— Stable";
            AddRow("Trend", "", trend);

            table.RowGroups.Add(group);
            doc.Blocks.Add(table);

            RichTextBox rtb = new() { Document = doc, IsReadOnly = true, Margin = new Thickness(10) };
            Window w = new()
            {
                Title = "Compare Runs",
                Content = rtb,
                Owner = this,
                Width = 500,
                Height = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            w.ShowDialog();
        }
    }

    internal void ClearResults_Click(object sender, RoutedEventArgs e)
    {
        allResults.Clear();
        allFindings.Clear();
        latestInventory = AdInventorySnapshot.Empty;
        latestTelemetry = TelemetrySnapshot.Empty;
        SyncResultItems();
        SyncFindingItems();
        ResultsTextBox.Clear();
        healthDetailsTextPending = false;
        LogsListBox.ItemsSource = null;
        latestRunDetailsText = string.Empty;
        latestLogsText = string.Empty;
        latestLogsFilePath = string.Empty;
        isLogContentReady = false;
        cachedLogLinesLength = -1;
        cachedLogLinesHash = 0;
        cachedLogLines = Array.Empty<LogLine>();
        logsTextPending = false;
        dgLogsEntries.SelectedItem = null;
        DetailsPanel.Visibility = Visibility.Collapsed;
        if (healthPageBound) testResultsGrid.ItemsSource = resultItemsView;
        if (logsPageBound) dgLogsEntries.ItemsSource = logResultItemsView;
        if (historyPageBound) dgTestHistory.ItemsSource = historyItemsView;
        if (findingsPageBound) dgFindings.ItemsSource = findingItemsView;
        if (securityPageBound) dgSecurityFindings.ItemsSource = securityFindingItems;
        UpdateHealthResultsLayout();
        UpdateHealthSummaryText();
        HideRunProgress();
        RefreshDashboard();
    }

    internal async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (allResults.Count == 0)
        {
            new SuccessNotification("No Results", "No test results available to export.", isError: true).ShowDialog();
            return;
        }

        SaveFileDialog saveFileDialog = new()
        {
            Filter = "CSV Files (*.csv)|*.csv|HTML Files (*.html)|*.html",
            FileName = "ADG_Test_Results"
        };

        if (saveFileDialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            List<TestResult> exportResults = allResults.ToList();
            await RunWithLoadingWindowAsync(
                "Exporting results",
                "Writing the selected export file.",
                () => Task.Run(() => ExportResultsToFile(saveFileDialog.FileName, exportResults))).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            new SuccessNotification("Export Failed", $"Failed to export:\n{ex.Message}", isError: true).ShowDialog();
        }
    }

    internal async void ExecutiveSummary_Click(object sender, RoutedEventArgs e)
    {
        if (allResults.Count == 0)
        {
            new SuccessNotification("No Results", "No test results available. Run tests first.", isError: true).ShowDialog();
            return;
        }

        int total = allResults.Count;
        int passed = allResults.Count(r => r.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase));
        int failed = allResults.Count(r => r.Result.Equals("FAIL", StringComparison.OrdinalIgnoreCase));
        int passRate = total > 0 ? (int)((double)passed / total * 100) : 0;
        int healthScore = CalculateHealthScore();
        string scoreColor = healthScore >= 80 ? "#2E7D32" : healthScore >= 50 ? "#F57F17" : "#C62828";

        var failures = allResults.Where(r => r.Result.Equals("FAIL", StringComparison.OrdinalIgnoreCase)).ToList();
        var findings = allFindings.Where(f => f.Severity == "Critical" || f.Severity == "High").ToList();

        SaveFileDialog sfd = new()
        {
            Filter = "HTML Files (*.html)|*.html",
            FileName = $"ADG_Executive_Summary_{DateTime.Now:yyyyMMdd_HHmm}.html"
        };

        if (sfd.ShowDialog() != true) return;

        try
        {
            List<TestResult> resultSnapshot = allResults.ToList();
            List<AdHealthFinding> findingSnapshot = allFindings.ToList();
            await RunWithLoadingWindowAsync(
                "Building executive summary",
                "Generating the HTML summary report.",
                () => Task.Run(() => WriteExecutiveSummaryFile(
                    sfd.FileName,
                    domainControllers,
                    total,
                    passed,
                    failed,
                    passRate,
                    healthScore,
                    scoreColor,
                    resultSnapshot,
                    findingSnapshot))).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            new SuccessNotification("Summary Failed", $"Failed to build executive summary:\n{ex.Message}", isError: true).ShowDialog();
            return;
        }

        try { Process.Start(new ProcessStartInfo(sfd.FileName) { UseShellExecute = true }); }
        catch { new SuccessNotification("Summary Saved", $"Summary saved to:\n{sfd.FileName}").ShowDialog(); }
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
        if (!schedulerTasksLoaded)
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

    private async void HomeRunTests_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        await NavigateToSectionAsync(1).ConfigureAwait(true);
        RunButton_Click(sender, e);
    }

    private async void HomeViewFindings_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        await NavigateToSectionAsync(2).ConfigureAwait(true);
    }

    private async void HomeViewHistory_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        await NavigateToSectionAsync(4).ConfigureAwait(true);
    }

    private void HomeCard_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Border card && card.Parent is Grid parentGrid &&
            parentGrid.Children.Count > 0 && parentGrid.Children[0] is Border overlay)
        {
            overlay.Opacity = 0.08;
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 4,
                Opacity = 0.25,
                Color = System.Windows.Media.Colors.Black
            };
            parentGrid.RenderTransform = new System.Windows.Media.TranslateTransform(0, -3);
            parentGrid.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        }
    }

    private void HomeCard_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Border card && card.Parent is Grid parentGrid &&
            parentGrid.Children.Count > 0 && parentGrid.Children[0] is Border overlay)
        {
            overlay.Opacity = 0;
            card.Effect = null;
            parentGrid.RenderTransform = null;
        }
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await NavigateToSectionAsync(7).ConfigureAwait(true);
    }

    private void LoadSettingsIntoPage()
    {
        SettingsDcTextBox.Text = domainControllers;
        SettingsEmailTextBox.Text = recipientEmail;
        SettingsChkDns.IsChecked = testDnsCheck;
        SettingsChkReplication.IsChecked = testReplication;
        SettingsChkTimeSkew.IsChecked = testTimeSkew;
        SettingsChkLdapBind.IsChecked = testLdapBind;
        SettingsChkCertDhcp.IsChecked = testCertDhcp;
        SettingsChkSmbSigning.IsChecked = testSmbLdapSigning;
        SettingsChkEmailManual.IsChecked = sendEmailManual;
        SettingsChkEmailScheduled.IsChecked = sendEmailScheduled;
    }

    internal void SettingsSaveButton_Click(object sender, RoutedEventArgs e)
    {
        domainControllers = SettingsDcTextBox.Text.Trim();
        recipientEmail = SettingsEmailTextBox.Text.Trim();
        testDnsCheck = SettingsChkDns.IsChecked ?? true;
        testReplication = SettingsChkReplication.IsChecked ?? true;
        testTimeSkew = SettingsChkTimeSkew.IsChecked ?? true;
        testLdapBind = SettingsChkLdapBind.IsChecked ?? true;
        testCertDhcp = SettingsChkCertDhcp.IsChecked ?? true;
        testSmbLdapSigning = SettingsChkSmbSigning.IsChecked ?? true;
        sendEmailManual = SettingsChkEmailManual.IsChecked ?? true;
        sendEmailScheduled = SettingsChkEmailScheduled.IsChecked ?? true;
        try
        {
            SaveSettings();
            RefreshDashboard();
            new SuccessNotification("Settings Saved", "Your settings have been saved successfully.").ShowDialog();
        }
        catch (Exception ex)
        {
            new SuccessNotification("Settings Error", $"Failed to save settings:\n{ex.Message}", isError: true).ShowDialog();
        }
    }

    internal async void TestEmailButton_Click(object sender, RoutedEventArgs e)
    {
        string recipient = SettingsEmailTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(recipient))
        {
            new SuccessNotification("Email Required", "Please enter a recipient email address first.", isError: true).ShowDialog();
            return;
        }

        SettingsTestEmailButton.IsEnabled = false;
        SettingsTestEmailButton.Content = "Sending...";

        try
        {
            await RunWithLoadingWindowAsync(
                "Sending test email",
                "Connecting to SMTP and sending the verification email.",
                () => SendConfiguredTestEmailAsync(recipient)).ConfigureAwait(true);

            new SuccessNotification("Email Sent", "Test email sent successfully!").ShowDialog();
        }
        catch (Exception ex)
        {
            new SuccessNotification("Email Failed", $"Failed to send test email:\n{ex.Message}", isError: true).ShowDialog();
        }
        finally
        {
            SettingsTestEmailButton.IsEnabled = true;
            SettingsTestEmailButton.Content = "Send Test Email";
        }
    }

    internal void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySearchFilter();

    internal void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        SearchPlaceholder.Visibility = Visibility.Collapsed;
    }

    internal void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            SearchPlaceholder.Visibility = Visibility.Visible;
        }
    }

    internal void SearchButton_Click(object sender, RoutedEventArgs e) => ApplySearchFilter();

    private async Task NavigateToSectionAsync(int index)
    {
        if (index < 0 || index >= MainTabControl.Items.Count)
        {
            return;
        }

        UpdateNavigationState(index);
        await Dispatcher.Yield(DispatcherPriority.Render);

        if (MainTabControl.SelectedIndex != index)
        {
            MainTabControl.SelectedIndex = index;
        }
    }

    internal void FindingsSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFindingsFilter();

    private async void NavSectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } &&
            int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) &&
            index >= 0 &&
            index < MainTabControl.Items.Count)
        {
            await NavigateToSectionAsync(index).ConfigureAwait(true);
        }
    }

    private void GroupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle && toggle.Tag is string panelName)
        {
            if (FindName(panelName) is FrameworkElement panel)
            {
                bool expanded = toggle.IsChecked == true;
                panel.Visibility = Visibility.Visible;

                if (expanded)
                {
                    double current = panel.ActualHeight > 0 ? panel.ActualHeight : panel.MaxHeight;
                    DoubleAnimation expandAnim = new()
                    {
                        From = 0,
                        To = 500,
                        Duration = TimeSpan.FromMilliseconds(200),
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
                    };
                    panel.BeginAnimation(FrameworkElement.MaxHeightProperty, expandAnim);
                }
                else
                {
                    double startH = panel.ActualHeight > 0 ? panel.ActualHeight : 100;
                    DoubleAnimation collapseAnim = new()
                    {
                        From = startH,
                        To = 0,
                        Duration = TimeSpan.FromMilliseconds(200),
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
                    };
                    panel.BeginAnimation(FrameworkElement.MaxHeightProperty, collapseAnim);
                }
            }

            // Update arrow direction
            string arrow = toggle.IsChecked == true ? "\uE70D" : "\uE76C";
            if (toggle.Name == "MonitoringToggle" && MonitoringArrow != null)
                MonitoringArrow.Text = arrow;
            else if (toggle.Name == "DataToggle" && DataArrow != null)
                DataArrow.Text = arrow;
        }
    }

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        int selectedIndex = MainTabControl.SelectedIndex;
        EnsurePageBindings(selectedIndex);
        UpdateNavigationState();
        if (selectedIndex == 1)
        {
            EnsureHealthDetailsTextLoaded();
        }
        else if (selectedIndex == 7)
        {
            LoadSettingsIntoPage();
        }
        else if (selectedIndex == 5 && logsTextPending)
        {
            _ = LoadLogsTabContentAsync();
        }
        else if (selectedIndex == 6)
        {
            UpdateSecurityGrid();
        }
        else if (selectedIndex == 8)
        {
            RefreshSchedulerTaskList();
        }

        if (MainTabControl.SelectedContent is UIElement content)
        {
            content.Opacity = 0.75;
            DoubleAnimation fadeIn = new()
            {
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            content.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }

        if (selectedIndex == 5)
        {
            RefreshLogsWorkspace();
        }
    }

    private void MainTabControl_Loaded(object sender, RoutedEventArgs e)
    {
        MainTabControl.SelectedIndex = 0;
        UpdateNavigationState();
    }

    private void SidebarToggleButton_Click(object sender, RoutedEventArgs e)
    {
        isSidebarCollapsed = !isSidebarCollapsed;
        SidebarToggleButton.IsChecked = isSidebarCollapsed;
        SidebarToggleButton.ToolTip = isSidebarCollapsed ? "Expand sidebar" : "Collapse sidebar";
        double targetWidth = isSidebarCollapsed ? 74 : 220;

        SidebarPanel.BeginAnimation(FrameworkElement.WidthProperty, null);
        double fromW = SidebarPanel.ActualWidth > 0 ? SidebarPanel.ActualWidth : (isSidebarCollapsed ? 220 : 74);

        DoubleAnimation widthAnim = new()
        {
            From = fromW,
            To = targetWidth,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new CircleEase { EasingMode = EasingMode.EaseInOut }
        };
        SidebarPanel.BeginAnimation(FrameworkElement.WidthProperty, widthAnim);

        double targetOpacity = isSidebarCollapsed ? 0.0 : 1.0;
        void AnimateLabel(TextBlock label)
        {
            label.BeginAnimation(UIElement.OpacityProperty, null);
            double fromO = label.Opacity;
            DoubleAnimation fadeAnim = new()
            {
                From = fromO,
                To = targetOpacity,
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CircleEase { EasingMode = EasingMode.EaseInOut }
            };
            label.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
        }

        SidebarHeaderContent.BeginAnimation(UIElement.OpacityProperty, null);
        double fromO2 = SidebarHeaderContent.Opacity;
        DoubleAnimation fadeContent = new()
        {
            From = fromO2,
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new CircleEase { EasingMode = EasingMode.EaseInOut }
        };
        SidebarHeaderContent.BeginAnimation(UIElement.OpacityProperty, fadeContent);
        AnimateLabel(HomeNavLabel);
        AnimateLabel(HealthNavLabel);
        AnimateLabel(FindingsNavLabel);
        AnimateLabel(InfrastructureNavLabel);
        AnimateLabel(HistoryNavLabel);
        AnimateLabel(LogsNavLabel);
        AnimateLabel(SecurityNavLabel);
        AnimateLabel(SchedulerNavLabel);
        AnimateLabel(SettingsNavLabel);
        if (MonitoringNavLabel != null) AnimateLabel(MonitoringNavLabel);
        if (MonitoringArrow != null) AnimateLabel(MonitoringArrow);
        if (DataNavLabel != null) AnimateLabel(DataNavLabel);
        if (DataArrow != null) AnimateLabel(DataArrow);
    }

    private void UpdateNavigationState()
    {
        UpdateNavigationState(MainTabControl.SelectedIndex);
    }

    private void UpdateNavigationState(int index)
    {
        WorkspaceSectionTitleText.Text = index switch
        {
            0 => "Home",
            1 => "Health",
            2 => "Findings",
            3 => "Infrastructure",
            4 => "History",
            5 => "Logs",
            6 => "Security",
            7 => "Settings",
            8 => "Scheduler",
            _ => "AD Guardian"
        };

        WorkspaceSectionIconText.Text = index switch
        {
            0 => "\uE80F",
            1 => "\uE9D9",
            2 => "\uE9D2",
            3 => "\uE968",
            4 => "\uE81C",
            5 => "\uE8A5",
            6 => "\uE72E",
            7 => "\uE713",
            8 => "\uE823",
            _ => "\uE9D9"
        };

        WorkspaceSectionSubtitleText.Text = index switch
        {
            0 => "Dashboard overview and quick actions",
            1 => "Domain controller health overview",
            2 => "Issues and remediation guidance",
            3 => "Active Directory infrastructure details",
            4 => "Past run records and comparisons",
            5 => "Diagnostic log output",
            6 => "Security posture and compliance",
            7 => "Application configuration",
            8 => "Automated health check scheduling",
            _ => "Active Directory monitoring console"
        };

        SetNavButtonState(HomeNavButton, index == 0);
        SetNavButtonState(HealthNavButton, index == 1);
        SetNavButtonState(FindingsNavButton, index == 2);
        SetNavButtonState(InfrastructureNavButton, index == 3);
        SetNavButtonState(HistoryNavButton, index == 4);
        SetNavButtonState(LogsNavButton, index == 5);
        SetNavButtonState(SecurityNavButton, index == 6);
        if (SettingsNavButton != null) SetNavButtonState(SettingsNavButton, index == 7);
        if (SchedulerNavButton != null) SetNavButtonState(SchedulerNavButton, index == 8);
    }

    private static void SetNavButtonState(Button button, bool isActive)
    {
        button.Background = isActive ? ActiveNavBgBrush : Brushes.Transparent;
        button.Foreground = isActive ? Brushes.White : InactiveNavFgBrush;
    }

    private void ApplySearchFilter()
    {
        resultItemsView?.Refresh();
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

    private static string SanitizeFileNamePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "run";
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        char[] sanitized = value
            .Trim()
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray();

        string collapsed = string.Join("_", new string(sanitized)
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

        return string.IsNullOrWhiteSpace(collapsed) ? "run" : collapsed;
    }

    private static RunLogSession CreateRunLogSession(DateTime startedAt, string testType)
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

    private static string GetControllerLogPath(RunLogSession session, string domainController)
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

        if (!App.IsRunningAsAdmin)
        {
            MessageBoxResult elevationChoice = MessageBox.Show(
                this,
                "Running diagnostic tests requires administrator privileges.\n\n" +
                "Would you like to relaunch AD Guardian as administrator?",
                "Administrator Required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (elevationChoice == MessageBoxResult.Yes)
            {
                if (App.TryRelaunchAsAdmin())
                {
                    Application.Current.Shutdown();
                    return;
                }

                NotificationService.Show(this, "Elevation Failed", "Could not relaunch as administrator. Please start AD Guardian manually using 'Run as administrator'.", isError: true);
            }

            return;
        }

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
        string[] dcList = domainControllers
            .Split(',')
            .Select(dc => dc.Trim())
            .Where(dc => !string.IsNullOrWhiteSpace(dc))
            .ToArray();

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

                allResults.AddRange(ParseDCDiagOutput(dc, dcdiagResult, logFilePath));
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

            if (!allResults.Any())
            {
                return;
            }

            string combinedLogPath = runSession.CombinedLogPath;
            await WriteCombinedLogAsync(logFilePaths, combinedLogPath, token);
            latestLogsFilePath = combinedLogPath;
            latestLogsText = File.Exists(combinedLogPath) ? await File.ReadAllTextAsync(combinedLogPath, token).ConfigureAwait(true) : string.Empty;

            int total = allResults.Count;
            int passed = allResults.Count(r => r.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase));
            int failed = allResults.Count(r => r.Result.Equals("FAIL", StringComparison.OrdinalIgnoreCase));
            string summary = BuildRunSummary(total, passed, failed, dcList);
            DisplayTestResults(summary);
            ForceRefreshDashboard();
            TestResult lastResult = allResults.Last();
            string bodyDetail =
                $"<p><strong>Service:</strong> {lastResult.Service}</p>\r\n" +
                $"<p><strong>Server:</strong> {lastResult.Server}</p>\r\n" +
                $"<p><strong>Result:</strong> {lastResult.Result}</p>\r\n" +
                $"<p><strong>Message:</strong> {lastResult.Message}</p>" +
                "<br/><br/><strong>Summary:</strong><br/>" + summary.Replace(Environment.NewLine, "<br/>");

            string subject = failed > 0 ? "[FAILED] Test Completed - ADG Test Results" : "Test Completed - ADG Test Results";
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

    private async Task SendScheduledEmailSafelyAsync(string subject, string bodyDetail, string attachmentPath)
    {
        Task emailTask = Task.Run(() => SendEmailWithAttachment(subject, bodyDetail, attachmentPath));
        Task completedTask = await Task.WhenAny(emailTask, Task.Delay(ScheduledEmailTimeout)).ConfigureAwait(true);

        if (completedTask == emailTask)
        {
            await emailTask.ConfigureAwait(true);
            return;
        }

        Debug.WriteLine($"Scheduled email send timed out after {ScheduledEmailTimeout.TotalSeconds:0} seconds.");
    }

    private static async Task WriteCombinedLogAsync(IEnumerable<string> logFilePaths, string combinedLogPath, CancellationToken token)
    {
        await using StreamWriter writer = new(combinedLogPath, false);
        foreach (string path in logFilePaths.Where(File.Exists))
        {
            token.ThrowIfCancellationRequested();
            await writer.WriteLineAsync($"---- Results for DC: {Path.GetFileNameWithoutExtension(path)} ----");
            string contents = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
            await writer.WriteAsync(contents);
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("==========================================");
        }
    }

    private static string BuildRunSummary(int total, int passed, int failed, IEnumerable<string> controllers)
    {
        string[] dcList = controllers
            .Where(controller => !string.IsNullOrWhiteSpace(controller))
            .Select(controller => controller.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return string.Join(
            Environment.NewLine,
            [
                $"Domain controllers tested: {dcList.Length}",
                $"Controllers: {string.Join(", ", dcList)}",
                $"Total tests: {total}",
                $"Passed: {passed}",
                $"Failed: {failed}"
            ]);
    }

    private static string WriteResultsSummarySync(RunLogSession session, List<TestResult> results, string summary)
    {
        string path = Path.Combine(session.RunDirectoryPath, "ResultsSummary.txt");
        try
        {
            int serviceWidth = Math.Max(12, results.Any() ? results.Max(r => r.Service?.Length ?? 0) : 0);
            int serverWidth = Math.Max(10, results.Any() ? results.Max(r => r.Server?.Length ?? 0) : 0);
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
            int serviceWidth = Math.Max(12, results.Any() ? results.Max(r => r.Service?.Length ?? 0) : 0);
            int serverWidth = Math.Max(10, results.Any() ? results.Max(r => r.Server?.Length ?? 0) : 0);
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

    private static string PadRight(string? value, int width)
    {
        return (value ?? string.Empty).PadRight(width);
    }

    private string? _dashboardHash;

    private void RefreshDashboardCore()
    {
        string newHash = string.Join("|",
            allResults.Count,
            allResults.Count(r => r.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase)),
            allResults.Count(r => r.Result.Equals("FAIL", StringComparison.OrdinalIgnoreCase)),
            allFindings.Count,
            allFindings.Count(f => f.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase)),
            allFindings.Count(f => f.Severity.Equals("High", StringComparison.OrdinalIgnoreCase)),
            allFindings.Count(f => f.Severity.Equals("Medium", StringComparison.OrdinalIgnoreCase)),
            historyEntries.Count,
            historyEntries.FirstOrDefault()?.RunDate.Ticks ?? 0,
            latestInventory.ForestName,
            latestInventory.DomainControllerCount,
            latestTelemetry.TotalServices);
        if (_dashboardHash == newHash) return;
        _dashboardHash = newHash;

        if (!HasLiveDashboardData() && cachedDashboardSnapshot != null)
        {
            ApplyCachedDashboardSnapshot(cachedDashboardSnapshot);
            return;
        }

        int configuredControllers = CountConfiguredDomainControllers();
        int passingTests = allResults.Count(r => r.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase));
        List<AdHealthFinding> activeFindings = GetActiveFindings().ToList();
        int criticalFindings = activeFindings.Count(f => f.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase));
        int healthScore = CalculateHealthScore();
        int highOrAboveFindings = activeFindings.Count(f =>
            f.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase) ||
            f.Severity.Equals("High", StringComparison.OrdinalIgnoreCase));

        HealthScoreText.Text = healthScore.ToString(CultureInfo.InvariantCulture);
        CriticalFindingsText.Text = criticalFindings.ToString(CultureInfo.InvariantCulture);
        PassingTestsText.Text = passingTests.ToString(CultureInfo.InvariantCulture);
        DomainControllerCountText.Text = configuredControllers.ToString(CultureInfo.InvariantCulture);

        HomeHealthScoreText.Text = healthScore.ToString(CultureInfo.InvariantCulture);
        HomeCriticalText.Text = criticalFindings.ToString(CultureInfo.InvariantCulture);
        HomePassingText.Text = passingTests.ToString(CultureInfo.InvariantCulture);
        HomePassRateText.Text = allResults.Count > 0
            ? $"{passingTests * 100 / Math.Max(1, allResults.Count)}%"
            : "--";
        HomeTotalRunsText.Text = historyEntries.Count.ToString(CultureInfo.InvariantCulture);
        HomeLastRunText.Text = historyEntries.Count > 0
            ? BuildLastRunSummary()
            : "No runs yet";

        ForestNameText.Text = latestInventory.ForestName;
        DomainNameText.Text = latestInventory.DomainName;
        DomainModeText.Text = latestInventory.DomainMode;
        InfrastructureSummaryText.Text = latestInventory.DomainControllerCount > 0
            ? $"Collected breadth for {latestInventory.DomainControllerCount} domain controller(s), {latestInventory.UserCount} users, and {latestInventory.ComputerCount} computers."
            : "Run a collection to populate infrastructure breadth.";
        OuCountText.Text = latestInventory.OrganizationalUnitCount.ToString(CultureInfo.InvariantCulture);
        GpoCountText.Text = latestInventory.GroupPolicyCount.ToString(CultureInfo.InvariantCulture);
        TrustCountText.Text = latestInventory.TrustCount.ToString(CultureInfo.InvariantCulture);
        UserCountText.Text = latestInventory.UserCount.ToString(CultureInfo.InvariantCulture);
        ComputerCountText.Text = latestInventory.ComputerCount.ToString(CultureInfo.InvariantCulture);
        InfrastructureDcCountText.Text = latestInventory.DomainControllerCount.ToString(CultureInfo.InvariantCulture);

        PrivilegedInsightsText.Text = BuildPrivilegeInsightSummary();
        SecuritySummaryText.Text = latestTelemetry.TotalServices > 0
            ? $"Security view includes {allFindings.Count(f => IsSecurityFinding(f))} security-oriented finding(s) across privilege and telemetry signals."
            : "Security findings are derived from privileged group breadth, failing directory tests, and service telemetry.";
        FindingsOpenCountText.Text = activeFindings.Count.ToString(CultureInfo.InvariantCulture);
        FindingsHighCountText.Text = highOrAboveFindings.ToString(CultureInfo.InvariantCulture);
        FindingsCriticalCountText.Text = criticalFindings.ToString(CultureInfo.InvariantCulture);

        if (findingsPageBound)
        {
            findingItemsView?.Refresh();
        }
        if (MainTabControl.SelectedIndex == 5)
        {
            RefreshLogsView();
            UpdateLogsWorkspaceSummary();
        }
        UpdateHealthSummaryText();
        if (securityPageBound || MainTabControl.SelectedIndex == 6)
        {
            UpdateSecurityGrid();
        }
        RefreshHomeFindingsSummary();
        if (MainTabControl.SelectedIndex == 0)
        {
            RefreshHomeRunHistoryBars();
            RefreshHomeTrendPolyline();
        }
    }

    private int CountConfiguredDomainControllers()
    {
        return domainControllers
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(dc => dc.Trim())
            .Count(dc => !string.IsNullOrWhiteSpace(dc));
    }

    private string BuildLastRunSummary()
    {
        if (historyEntries.Count == 0)
        {
            return "No completed runs recorded yet.";
        }

        TestHistoryEntry lastRun = historyEntries[0];
        return $"Last run {lastRun.RunDate:dd MMM yyyy HH:mm} ({lastRun.TestType}, {lastRun.Passed} passed / {lastRun.Failed} failed).";
    }

    private void RefreshHomeRunHistoryBars()
    {
        HomeRunHistoryBars.Children.Clear();
        var recent = historyEntries.OrderByDescending(h => h.RunDate).Take(6).Reverse().ToList();
        if (recent.Count == 0) return;

        double maxCount = Math.Max(1, recent.Max(h => Math.Max(h.Passed, h.Failed)));
        double barMaxHeight = 90;

        foreach (var entry in recent)
        {
            double passHeight = entry.Passed * barMaxHeight / maxCount;
            double failHeight = entry.Failed * barMaxHeight / maxCount;

            Border col = new()
            {
                Width = 28,
                Margin = new Thickness(3, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Bottom
            };

            StackPanel stack = new() { VerticalAlignment = VerticalAlignment.Bottom };
            if (entry.Failed > 0)
            {
                stack.Children.Add(new Border
                {
                    Height = Math.Max(2, failHeight),
                    Background = new SolidColorBrush(Color.FromRgb(211, 47, 47)),
                    CornerRadius = new CornerRadius(3, 3, 0, 0),
                    Margin = new Thickness(0, 1, 0, 0)
                });
            }
            if (entry.Passed > 0)
            {
                stack.Children.Add(new Border
                {
                    Height = Math.Max(2, passHeight),
                    Background = new SolidColorBrush(Color.FromRgb(46, 125, 50)),
                    CornerRadius = new CornerRadius(3, 3, 0, 0),
                    Margin = new Thickness(0, 1, 0, 0)
                });
            }
            if (entry.Passed == 0 && entry.Failed == 0)
            {
                stack.Children.Add(new Border
                {
                    Height = 2,
                    Background = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(0, 1, 0, 0)
                });
            }
            col.Child = stack;
            HomeRunHistoryBars.Children.Add(col);
        }
    }

    private void RefreshHomeTrendPolyline()
    {
        var recent = historyEntries.OrderByDescending(h => h.RunDate).Take(10).Reverse().ToList();
        if (recent.Count < 1) return;

        PointCollection points = new();
        double w = 120;
        double h = 60;
        int n = recent.Count;
        for (int i = 0; i < n; i++)
        {
            double x = i * w / Math.Max(1, n - 1);
            double rate = recent[i].Total > 0
                ? (double)recent[i].Passed / recent[i].Total
                : 0.5;
            double y = h - (rate * h * 0.8 + h * 0.1);
            points.Add(new Point(x, y));
        }
        HomeTrendPolyline.Points = points;
    }

    private void RefreshHomeFindingsSummary()
    {
        HomeFindingsSummary.Children.Clear();

        int crit = allFindings.Count(f => f.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase));
        int high = allFindings.Count(f => f.Severity.Equals("High", StringComparison.OrdinalIgnoreCase));
        int med = allFindings.Count(f => f.Severity.Equals("Medium", StringComparison.OrdinalIgnoreCase));
        int low = allFindings.Count(f => f.Severity.Equals("Low", StringComparison.OrdinalIgnoreCase));

        void AddRow(string label, int count, Brush color)
        {
            Border row = new()
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(0, 0, 0, 6),
                BorderBrush = new SolidColorBrush(Color.FromRgb(230, 236, 242)),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            Grid g = new();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock labelTb = new()
            {
                Text = label,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(79, 100, 121)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(labelTb, 0);
            g.Children.Add(labelTb);

            Border countBadge = new()
            {
                Background = color,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 2),
                MinWidth = 28,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            TextBlock countTb = new()
            {
                Text = count.ToString(CultureInfo.InvariantCulture),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center
            };
            countBadge.Child = countTb;
            Grid.SetColumn(countBadge, 1);
            g.Children.Add(countBadge);

            row.Child = g;
            HomeFindingsSummary.Children.Add(row);
        }

        if (crit + high + med + low == 0)
        {
            HomeFindingsSummary.Children.Add(new TextBlock
            {
                Text = "No actionable findings detected.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(79, 100, 121))
            });
            return;
        }

        AddRow("Critical", crit, new SolidColorBrush(Color.FromRgb(211, 47, 47)));
        AddRow("High", high, new SolidColorBrush(Color.FromRgb(239, 108, 0)));
        AddRow("Medium", med, new SolidColorBrush(Color.FromRgb(245, 166, 35)));
        AddRow("Low", low, new SolidColorBrush(Color.FromRgb(100, 100, 100)));
    }

    private bool HasLiveDashboardData()
    {
        return historyEntries.Count > 0 ||
               allResults.Count > 0 ||
               allFindings.Count > 0 ||
               latestInventory != AdInventorySnapshot.Empty ||
               latestTelemetry != TelemetrySnapshot.Empty;
    }

    private DashboardSnapshot BuildDashboardSnapshot()
    {
        List<AdHealthFinding> activeFindings = GetActiveFindings().ToList();
        int passingTests = allResults.Count(r => r.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase));
        TestHistoryEntry? latestRun = historyEntries.FirstOrDefault();

        return new DashboardSnapshot
        {
            CapturedAtUtc = DateTime.UtcNow,
            HealthScore = CalculateHealthScore(),
            CriticalFindings = activeFindings.Count(f => f.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase)),
            PassingTests = passingTests,
            ConfiguredDomainControllers = CountConfiguredDomainControllers(),
            TotalRuns = historyEntries.Count,
            LastRunSummary = historyEntries.Count > 0 ? BuildLastRunSummary() : "No runs yet",
            FindingsCriticalCount = activeFindings.Count(f => f.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase)),
            FindingsHighCount = activeFindings.Count(f => f.Severity.Equals("High", StringComparison.OrdinalIgnoreCase)),
            FindingsMediumCount = activeFindings.Count(f => f.Severity.Equals("Medium", StringComparison.OrdinalIgnoreCase)),
            FindingsLowCount = activeFindings.Count(f => f.Severity.Equals("Low", StringComparison.OrdinalIgnoreCase)),
            LastRunPassed = latestRun?.Passed ?? 0,
            LastRunFailed = latestRun?.Failed ?? 0,
            LastRunTotal = latestRun?.Total ?? 0
        };
    }

    private void ApplyCachedDashboardSnapshot(DashboardSnapshot snapshot)
    {
        HealthScoreText.Text = snapshot.HealthScore.ToString(CultureInfo.InvariantCulture);
        CriticalFindingsText.Text = snapshot.CriticalFindings.ToString(CultureInfo.InvariantCulture);
        PassingTestsText.Text = snapshot.PassingTests.ToString(CultureInfo.InvariantCulture);
        DomainControllerCountText.Text = snapshot.ConfiguredDomainControllers.ToString(CultureInfo.InvariantCulture);

        HomeHealthScoreText.Text = snapshot.HealthScore.ToString(CultureInfo.InvariantCulture);
        HomeCriticalText.Text = snapshot.CriticalFindings.ToString(CultureInfo.InvariantCulture);
        HomePassingText.Text = snapshot.PassingTests.ToString(CultureInfo.InvariantCulture);
        HomePassRateText.Text = snapshot.LastRunTotal > 0
            ? $"{snapshot.LastRunPassed * 100 / Math.Max(1, snapshot.LastRunTotal)}%"
            : "--";
        HomeTotalRunsText.Text = snapshot.TotalRuns.ToString(CultureInfo.InvariantCulture);
        HomeLastRunText.Text = snapshot.LastRunSummary;

        FindingsOpenCountText.Text = (snapshot.FindingsCriticalCount + snapshot.FindingsHighCount + snapshot.FindingsMediumCount + snapshot.FindingsLowCount)
            .ToString(CultureInfo.InvariantCulture);
        FindingsHighCountText.Text = (snapshot.FindingsCriticalCount + snapshot.FindingsHighCount).ToString(CultureInfo.InvariantCulture);
        FindingsCriticalCountText.Text = snapshot.FindingsCriticalCount.ToString(CultureInfo.InvariantCulture);

        RenderHomeFindingsSummary(
            snapshot.FindingsCriticalCount,
            snapshot.FindingsHighCount,
            snapshot.FindingsMediumCount,
            snapshot.FindingsLowCount,
            "No actionable findings cached yet.");
    }

    private void RenderHomeFindingsSummary(int crit, int high, int med, int low, string emptyMessage)
    {
        HomeFindingsSummary.Children.Clear();

        void AddRow(string label, int count, Brush color)
        {
            Border row = new()
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(0, 0, 0, 6),
                BorderBrush = new SolidColorBrush(Color.FromRgb(230, 236, 242)),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            Grid g = new();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock labelTb = new()
            {
                Text = label,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(79, 100, 121)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(labelTb, 0);
            g.Children.Add(labelTb);

            Border countBadge = new()
            {
                Background = color,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 2),
                MinWidth = 28,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            TextBlock countTb = new()
            {
                Text = count.ToString(CultureInfo.InvariantCulture),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center
            };
            countBadge.Child = countTb;
            Grid.SetColumn(countBadge, 1);
            g.Children.Add(countBadge);

            row.Child = g;
            HomeFindingsSummary.Children.Add(row);
        }

        if (crit + high + med + low == 0)
        {
            HomeFindingsSummary.Children.Add(new TextBlock
            {
                Text = emptyMessage,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(79, 100, 121))
            });
            return;
        }

        AddRow("Critical", crit, new SolidColorBrush(Color.FromRgb(211, 47, 47)));
        AddRow("High", high, new SolidColorBrush(Color.FromRgb(239, 108, 0)));
        AddRow("Medium", med, new SolidColorBrush(Color.FromRgb(245, 166, 35)));
        AddRow("Low", low, new SolidColorBrush(Color.FromRgb(100, 100, 100)));
    }

    private async Task CleanupLogFilesAsync()
    {
        try
        {
            if (!Directory.Exists(LogDirectoryPath))
            {
                return;
            }

            await Task.Run(() =>
            {
                HashSet<string> protectedFiles = historyEntries
                    .Select(entry => entry.LogFilePath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(Path.GetFullPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                HashSet<string> protectedRunDirectories = protectedFiles
                    .Select(GetManagedRunDirectoryPath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => Path.GetFullPath(path!))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(latestLogsFilePath))
                {
                    protectedFiles.Add(Path.GetFullPath(latestLogsFilePath));
                    string? latestRunDirectory = GetManagedRunDirectoryPath(latestLogsFilePath);
                    if (!string.IsNullOrWhiteSpace(latestRunDirectory))
                    {
                        protectedRunDirectories.Add(Path.GetFullPath(latestRunDirectory));
                    }
                }

                DateTime cutoffUtc = DateTime.UtcNow.AddDays(-14);
                string runsRootDirectory = GetRunsRootDirectoryPath();
                if (Directory.Exists(runsRootDirectory))
                {
                    List<DirectoryInfo> runDirectories = new();
                    foreach (DirectoryInfo dateDir in new DirectoryInfo(runsRootDirectory).GetDirectories("*", SearchOption.TopDirectoryOnly))
                    {
                        runDirectories.AddRange(dateDir.GetDirectories("*", SearchOption.TopDirectoryOnly));
                    }

                    runDirectories = runDirectories.OrderByDescending(d => d.LastWriteTimeUtc).ToList();

                    foreach (DirectoryInfo runDirectory in runDirectories)
                    {
                        string fullRunDirectory = runDirectory.FullName;
                        if (protectedRunDirectories.Contains(fullRunDirectory))
                        {
                            continue;
                        }

                        if (runDirectory.LastWriteTimeUtc < cutoffUtc)
                        {
                            runDirectory.Delete(true);
                        }
                    }

                    List<DirectoryInfo> allRunDirs = new();
                    foreach (DirectoryInfo dateDir in new DirectoryInfo(runsRootDirectory).GetDirectories("*", SearchOption.TopDirectoryOnly))
                    {
                        allRunDirs.AddRange(dateDir.GetDirectories("*", SearchOption.TopDirectoryOnly));
                    }

                    foreach (DirectoryInfo extraDirectory in allRunDirs
                        .OrderByDescending(d => d.LastWriteTimeUtc)
                        .Skip(100)
                        .Where(d => !protectedRunDirectories.Contains(d.FullName)))
                    {
                        extraDirectory.Delete(true);
                    }

                    foreach (DirectoryInfo dateDir in new DirectoryInfo(runsRootDirectory).GetDirectories("*", SearchOption.TopDirectoryOnly))
                    {
                        if (dateDir.GetDirectories().Length == 0 && dateDir.GetFiles().Length == 0)
                        {
                            dateDir.Delete();
                        }
                    }
                }

                FileInfo[] legacyFlatFiles = new DirectoryInfo(LogDirectoryPath)
                    .GetFiles("*.txt", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ToArray();

                foreach (FileInfo file in legacyFlatFiles)
                {
                    string fullPath = file.FullName;
                    if (protectedFiles.Contains(fullPath))
                    {
                        continue;
                    }

                    if (file.LastWriteTimeUtc < cutoffUtc)
                    {
                        file.Delete();
                    }
                }

                foreach (FileInfo extraFile in new DirectoryInfo(LogDirectoryPath)
                    .GetFiles("*.txt", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Skip(25)
                    .Where(file => !protectedFiles.Contains(file.FullName)))
                {
                    extraFile.Delete();
                }
            }).ConfigureAwait(true);
        }
        catch
        {
        }
    }

    private void RebuildFindings()
    {
        allFindings.Clear();
        foreach (TestResult result in allResults)
        {
            if (result.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            allFindings.Add(new AdHealthFinding
            {
                Category = InferCategory(result.Service),
                Severity = InferSeverity(result),
                Source = "DCDiag / Repadmin",
                Target = string.IsNullOrWhiteSpace(result.Server) ? result.Service : $"{result.Server} - {result.Service}",
                Summary = BuildFindingSummary(result),
                Details = result.Message,
                Evidence = result.Message,
                Remediation = SuggestRemediation(result),
                Status = result.Result,
                LogFilePath = result.LogFilePath
            });
        }

        allFindings.AddRange(latestInventory.Findings);
        allFindings.AddRange(latestTelemetry.Findings);
        List<AdHealthFinding> deduplicatedFindings = allFindings
            .GroupBy(BuildFindingKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(finding => SeverityRank(finding.Severity))
            .ThenBy(finding => finding.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.Target, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.Summary, StringComparer.OrdinalIgnoreCase)
            .ToList();
        allFindings.Clear();
        allFindings.AddRange(deduplicatedFindings);
        SyncFindingItems();
    }

    private static string BuildFindingKey(AdHealthFinding finding)
    {
        return string.Join("|",
            finding.Category ?? string.Empty,
            finding.Severity ?? string.Empty,
            finding.Source ?? string.Empty,
            finding.Target ?? string.Empty,
            finding.Summary ?? string.Empty,
            finding.Status ?? string.Empty,
            finding.LogFilePath ?? string.Empty);
    }

    private IEnumerable<AdHealthFinding> GetActiveFindings()
    {
        return allFindings.Where(f => !f.Severity.Equals("Info", StringComparison.OrdinalIgnoreCase));
    }

    private int CalculateHealthScore()
    {
        double currentPassRate = 100;
        TestHistoryEntry? latestRun = historyEntries.FirstOrDefault();
        if (latestRun != null && latestRun.Total > 0)
        {
            currentPassRate = (double)latestRun.Passed / latestRun.Total * 100;
        }

        double trendAvg = 100;
        var recentRuns = historyEntries.Take(5).Where(h => h.Total > 0).ToList();
        if (recentRuns.Count > 0)
        {
            trendAvg = recentRuns.Average(h => (double)h.Passed / h.Total * 100);
        }

        int critical = allFindings.Count(f => f.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase));
        int high = allFindings.Count(f => f.Severity.Equals("High", StringComparison.OrdinalIgnoreCase));
        int medium = allFindings.Count(f => f.Severity.Equals("Medium", StringComparison.OrdinalIgnoreCase));

        double findingsPenalty = Math.Min(30, critical * 10 + high * 5 + medium * 2);
        double findingsRatio = (100 - findingsPenalty) / 100;

        double score = (currentPassRate * 0.6 + trendAvg * 0.4) * findingsRatio;
        return Math.Max(0, Math.Min(100, (int)Math.Round(score)));
    }

    private static string InferCategory(string service)
    {
        if (service.Contains("DNS", StringComparison.OrdinalIgnoreCase))
        {
            return "DNS";
        }

        if (service.Contains("Rep", StringComparison.OrdinalIgnoreCase))
        {
            return "Replication";
        }

        if (service.Contains("NetLogons", StringComparison.OrdinalIgnoreCase) ||
            service.Contains("Advertising", StringComparison.OrdinalIgnoreCase) ||
            service.Contains("Locator", StringComparison.OrdinalIgnoreCase))
        {
            return "Domain Services";
        }

        if (service.Contains("Frs", StringComparison.OrdinalIgnoreCase) ||
            service.Contains("SysVol", StringComparison.OrdinalIgnoreCase))
        {
            return "SYSVOL";
        }

        if (service.Contains("MachineAccount", StringComparison.OrdinalIgnoreCase) ||
            service.Contains("Services", StringComparison.OrdinalIgnoreCase))
        {
            return "Configuration";
        }

        return "Infrastructure";
    }

    private static string InferSeverity(TestResult result)
    {
        if (result.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase))
        {
            return "Info";
        }

        if (result.Service.Contains("DNS", StringComparison.OrdinalIgnoreCase) ||
            result.Service.Contains("Rep", StringComparison.OrdinalIgnoreCase) ||
            result.Service.Contains("Advertising", StringComparison.OrdinalIgnoreCase))
        {
            return "Critical";
        }

        if (result.Service.Contains("NetLogons", StringComparison.OrdinalIgnoreCase) ||
            result.Service.Contains("Services", StringComparison.OrdinalIgnoreCase) ||
            result.Service.Contains("SystemLog", StringComparison.OrdinalIgnoreCase))
        {
            return "High";
        }

        return "Medium";
    }

    private static string BuildFindingSummary(TestResult result)
    {
        if (result.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase))
        {
            return $"{result.Service} passed on {result.Server}.";
        }

        return $"{result.Service} failed on {result.Server}.";
    }

    private static string SuggestRemediation(TestResult result)
    {
        if (result.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase))
        {
            return "No action required.";
        }

        if (result.Service.Contains("DNS", StringComparison.OrdinalIgnoreCase))
        {
            return "Review DC DNS client settings, zone health, and record registration before rerunning diagnostics.";
        }

        if (result.Service.Contains("Rep", StringComparison.OrdinalIgnoreCase))
        {
            return "Inspect replication links, AD Sites and Services, and recent repadmin output for backlog or topology errors.";
        }

        if (result.Service.Contains("NetLogons", StringComparison.OrdinalIgnoreCase) ||
            result.Service.Contains("Advertising", StringComparison.OrdinalIgnoreCase))
        {
            return "Confirm Netlogon, AD DS, and related DC services are healthy and that the controller can advertise correctly.";
        }

        return "Open the linked log section, capture the failing evidence, and verify dependent DC services before rerunning.";
    }

    private async Task EnsureStartupInitializedAsync()
    {
        if (startupInitializationTask != null)
        {
            await startupInitializationTask.ConfigureAwait(true);
            startupInitializationTask = null;
        }
    }

    private void ApplyFindingsFilter() => findingItemsView?.Refresh();

    private void UpdateSecurityGrid()
    {
        if (dgSecurityFindings == null)
        {
            return;
        }

        List<AdHealthFinding> securityFindings = allFindings
            .Where(IsSecurityFinding)
            .OrderByDescending(f => SeverityRank(f.Severity))
            .ToList();
        ReplaceCollection(securityFindingItems, securityFindings);

        int critical = securityFindings.Count(f => f.Severity == "Critical");
        int high = securityFindings.Count(f => f.Severity == "High");
        int totalPrivGroups = latestInventory?.PrivilegedGroupCounts?.Values?.Sum() ?? 0;

        SecurityTotalFindingsText.Text = securityFindings.Count.ToString(CultureInfo.InvariantCulture);
        SecurityCriticalText.Text = critical.ToString(CultureInfo.InvariantCulture);
        SecurityHighText.Text = high.ToString(CultureInfo.InvariantCulture);
        SecurityPrivGroupCountText.Text = totalPrivGroups.ToString(CultureInfo.InvariantCulture);
    }

    private async Task LoadLogsTabContentAsync()
    {
        try
        {
            string logsText = latestLogsText;
            if (string.IsNullOrWhiteSpace(logsText) &&
                !string.IsNullOrWhiteSpace(latestLogsFilePath) &&
                File.Exists(latestLogsFilePath))
            {
                logsText = await File.ReadAllTextAsync(latestLogsFilePath).ConfigureAwait(true);
                latestLogsText = logsText;
            }

            if (string.IsNullOrWhiteSpace(logsText) && (isRunInProgress || !isLogContentReady))
            {
                LogsListBox.ItemsSource = new List<LogLine>
                {
                    new() { Text = "Logs are still being generated. Please wait for the test run to complete...", Foreground = Brushes.Gray, FontWeight = FontWeights.SemiBold }
                };
                LogsFileNameText.Text = "Waiting for logs...";
            }
            else
            {
                LogsListBox.ItemsSource = GetCachedLogLines(logsText);
                LogsFileNameText.Text = string.IsNullOrWhiteSpace(latestLogsFilePath)
                    ? "Current summary view"
                    : Path.GetFileName(latestLogsFilePath);
                RefreshLogSectionEntries(logsText, latestLogsFilePath);
            }
            logsTextPending = false;
        }
        catch (Exception ex)
        {
            LogsListBox.ItemsSource = new List<LogLine>
            {
                new() { Text = $"Unable to load log output.\n{ex.Message}", Foreground = Brushes.Red, FontWeight = FontWeights.Bold }
            };
            LogsFileNameText.Text = "Unable to load log";
            logsTextPending = false;
        }
    }

    private IReadOnlyList<LogLine> GetCachedLogLines(string text)
    {
        int textHash = text.GetHashCode(StringComparison.Ordinal);
        if (cachedLogLinesLength == text.Length && cachedLogLinesHash == textHash)
        {
            return cachedLogLines;
        }

        cachedLogLines = BuildLogLines(text);
        cachedLogLinesLength = text.Length;
        cachedLogLinesHash = textHash;
        return cachedLogLines;
    }

    private static readonly Brush LogNormalBrush = FrozenBrush(Color.FromRgb(52, 73, 94));
    private static readonly Brush LogFailBrush = FrozenBrush(Color.FromRgb(211, 47, 47));
    private static readonly Brush LogPassBrush = FrozenBrush(Color.FromRgb(46, 125, 50));

    private static List<LogLine> BuildLogLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<LogLine>();

        List<LogLine> logLines = new();
        using StringReader reader = new(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            Brush fg = LogNormalBrush;
            FontWeight fw = FontWeights.Normal;

            if (line.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                fg = LogFailBrush;
                fw = FontWeights.SemiBold;
            }
            else if (line.IndexOf("pass", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                fg = LogPassBrush;
                fw = FontWeights.SemiBold;
            }

            logLines.Add(new LogLine { Text = line, Foreground = fg, FontWeight = fw });
        }
        return logLines;
    }

    public void ShowLogFileInLogsTab(string logFilePath)
    {
        try
        {
            latestLogsText = File.ReadAllText(logFilePath);
            latestLogsFilePath = logFilePath;
            LogsListBox.ItemsSource = GetCachedLogLines(latestLogsText);
            LogsFileNameText.Text = Path.GetFileName(logFilePath);
            RefreshLogSectionEntries(latestLogsText, latestLogsFilePath);
            logsTextPending = false;
            _ = NavigateToSectionAsync(5);
        }
        catch (Exception ex)
        {
            NotificationService.Show(this, "Error", $"Failed to load log file: {ex.Message}", isError: true);
        }
    }

    private async Task RunWithLoadingWindowAsync(string title, string message, Func<Task> operation)
    {
        const int loadingWindowDelayMs = 100;
        bool previousEnabledState = IsEnabled;
        LoadingWindow? loadingWindow = null;
        try
        {
            Task operationTask = operation();
            Task delayTask = Task.Delay(loadingWindowDelayMs);
            Task completedTask = await Task.WhenAny(operationTask, delayTask).ConfigureAwait(true);
            if (completedTask != operationTask)
            {
                loadingWindow = new(title, message)
                {
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                IsEnabled = false;
                loadingWindow.Show();
                await Dispatcher.Yield(DispatcherPriority.Render);
            }

            await operationTask.ConfigureAwait(true);
        }
        finally
        {
            if (loadingWindow != null && loadingWindow.IsVisible)
            {
                loadingWindow.Close();
            }

            IsEnabled = previousEnabledState;
            Activate();
        }
    }

    private void RefreshLogSectionEntries(string logText, string logFilePath)
    {
        IReadOnlyList<TestResult> sectionResults = Array.Empty<TestResult>();
        if (!string.IsNullOrWhiteSpace(logText))
        {
            sectionResults = ParseDCDiagOutput("Log", logText, logFilePath).ToList();
        }

        if (sectionResults.Count == 0 && allResults.Count > 0)
        {
            sectionResults = allResults;
        }

        ReplaceCollection(logResultItems, sectionResults);
        using (logResultItemsView?.DeferRefresh())
        {
        }

        RefreshLogsFilterOptions();
        RefreshLogsView();
        UpdateLogsWorkspaceSummary();
    }

    private static void ExportResultsToFile(string filePath, IReadOnlyCollection<TestResult> results)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".csv")
        {
            using StreamWriter writer = new(filePath, false);
            writer.WriteLine("Service,Server,Result,Message,LogFilePath");
            foreach (TestResult result in results)
            {
                string escapedMsg = result.Message?.Replace("\"", "\"\"") ?? string.Empty;
                writer.WriteLine($"{result.Service},{result.Server},{result.Result},\"{escapedMsg}\",{result.LogFilePath}");
            }

            return;
        }

        int total = results.Count;
        int passed = results.Count(r => r.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase));
        int failed = results.Count(r => r.Result.Equals("FAIL", StringComparison.OrdinalIgnoreCase));

        using StreamWriter htmlWriter = new(filePath, false);
        htmlWriter.WriteLine("<!DOCTYPE html><html><head><meta charset='utf-8'>");
        htmlWriter.WriteLine("<title>AD Guardian Test Results</title>");
        htmlWriter.WriteLine("<style>body{font-family:'Segoe UI',Arial,sans-serif;margin:20px}");
        htmlWriter.WriteLine("h1{color:#1A73E8}table{border-collapse:collapse;width:100%}");
        htmlWriter.WriteLine("th,td{border:1px solid #ddd;padding:8px;text-align:left}");
        htmlWriter.WriteLine("th{background-color:#1A73E8;color:white}");
        htmlWriter.WriteLine(".PASS{color:green;font-weight:bold}.FAIL{color:red;font-weight:bold}");
        htmlWriter.WriteLine(".summary{background:#f0f8ff;padding:15px;border-radius:8px;margin:10px 0}");
        htmlWriter.WriteLine("</style></head><body>");
        htmlWriter.WriteLine("<h1>AD Guardian Test Results</h1>");
        htmlWriter.WriteLine($"<div class='summary'><strong>Total:</strong> {total} | <strong>Passed:</strong> {passed} | <strong>Failed:</strong> {failed}</div>");
        htmlWriter.WriteLine("<table><tr><th>Service</th><th>Server</th><th>Result</th><th>Message</th><th>Log File</th></tr>");
        foreach (TestResult result in results)
        {
            htmlWriter.WriteLine($"<tr><td>{result.Service}</td><td>{result.Server}</td>");
            htmlWriter.WriteLine($"<td class='{result.Result}'>{result.Result}</td>");
            htmlWriter.WriteLine($"<td>{result.Message}</td><td>{result.LogFilePath}</td></tr>");
        }

        htmlWriter.WriteLine("</table></body></html>");
    }

    private static void WriteExecutiveSummaryFile(
        string filePath,
        string configuredDomainControllers,
        int total,
        int passed,
        int failed,
        int passRate,
        int healthScore,
        string scoreColor,
        IReadOnlyCollection<TestResult> allResultSnapshot,
        IReadOnlyCollection<AdHealthFinding> allFindingSnapshot)
    {
        List<TestResult> failures = allResultSnapshot
            .Where(r => r.Result.Equals("FAIL", StringComparison.OrdinalIgnoreCase))
            .ToList();
        List<AdHealthFinding> findings = allFindingSnapshot
            .Where(f => f.Severity == "Critical" || f.Severity == "High")
            .ToList();

        using StreamWriter w = new(filePath, false);
        w.WriteLine("<!DOCTYPE html><html><head><meta charset='utf-8'>");
        w.WriteLine("<title>AD Guardian Executive Summary</title>");
        w.WriteLine("<style>");
        w.WriteLine("body{font-family:'Segoe UI',Arial,sans-serif;margin:30px;color:#333}");
        w.WriteLine("h1{color:#1A73E8;border-bottom:3px solid #1A73E8;padding-bottom:8px}");
        w.WriteLine("h2{color:#1A73E8;margin-top:24px}");
        w.WriteLine(".score{font-size:36px;font-weight:bold;padding:15px;border-radius:10px;display:inline-block;color:#fff}");
        w.WriteLine(".summary{background:#f0f8ff;padding:18px;border-radius:10px;margin:12px 0}");
        w.WriteLine("table{border-collapse:collapse;width:100%;margin:10px 0}");
        w.WriteLine("th,td{border:1px solid #ddd;padding:10px;text-align:left}");
        w.WriteLine("th{background-color:#1A73E8;color:white}");
        w.WriteLine(".PASS{color:#2E7D32;font-weight:bold}.FAIL{color:#C62828;font-weight:bold}");
        w.WriteLine(".critical{background:#FFEBEE}.high{background:#FFF3E0}");
        w.WriteLine(".footer{margin-top:30px;font-size:12px;color:#999;border-top:1px solid #ddd;padding-top:10px}");
        w.WriteLine("</style></head><body>");
        w.WriteLine("<h1>AD Guardian Executive Summary</h1>");
        w.WriteLine($"<p><strong>Generated:</strong> {DateTime.Now:dd MMM yyyy HH:mm}</p>");
        w.WriteLine($"<p><strong>Domain Controllers:</strong> {configuredDomainControllers}</p>");
        w.WriteLine($"<div class='score' style='background:{scoreColor}'>{healthScore}/100</div>");
        w.WriteLine("<h2>Test Results Overview</h2>");
        w.WriteLine($"<div class='summary'><strong>Total:</strong> {total} | <strong>Passed:</strong> {passed} | <strong>Failed:</strong> {failed} | <strong>Pass Rate:</strong> {passRate}%</div>");

        if (failures.Count > 0)
        {
            w.WriteLine("<h2>Failed Tests</h2><table><tr><th>Service</th><th>Server</th><th>Message</th></tr>");
            foreach (TestResult failure in failures)
            {
                w.WriteLine($"<tr class='FAIL'><td>{failure.Service}</td><td>{failure.Server}</td><td>{failure.Message}</td></tr>");
            }

            w.WriteLine("</table>");
        }

        if (findings.Count > 0)
        {
            w.WriteLine("<h2>Critical & High Findings</h2><table><tr><th>Category</th><th>Severity</th><th>Finding</th><th>Remediation</th></tr>");
            foreach (AdHealthFinding finding in findings)
            {
                string rowClass = finding.Severity == "Critical" ? "critical" : "high";
                w.WriteLine($"<tr class='{rowClass}'><td>{finding.Category}</td><td>{finding.Severity}</td><td>{finding.Summary}</td><td>{finding.Remediation}</td></tr>");
            }

            w.WriteLine("</table>");
        }

        w.WriteLine("<h2>All Test Results</h2><table><tr><th>Service</th><th>Server</th><th>Result</th><th>Message</th></tr>");
        foreach (TestResult result in allResultSnapshot)
        {
            w.WriteLine($"<tr><td>{result.Service}</td><td>{result.Server}</td><td class='{result.Result}'>{result.Result}</td><td>{result.Message}</td></tr>");
        }

        w.WriteLine("</table>");
        w.WriteLine("<div class='footer'>AD Guardian — Automated Active Directory Health Report</div>");
        w.WriteLine("</body></html>");
    }

    private static Task SendConfiguredTestEmailAsync(string recipient)
    {
        return Task.Run(() =>
        {
            using SmtpClient client = new("smtp.gmail.com", 587)
            {
                Credentials = new NetworkCredential("adguardianutility@gmail.com", "ihai btfi qeja nbqp"),
                EnableSsl = true
            };
            using MailMessage message = new("adguardianutility@gmail.com", recipient)
            {
                Subject = "AD Guardian - Test Email",
                Body = "This is a test email from AD Guardian. If you received this, your email configuration is working correctly."
            };
            client.Send(message);
        });
    }

    private void InitializeLogsFilters()
    {
        if (LogsDcFilter.Items.Count == 0)
        {
            LogsDcFilter.Items.Add("All domain controllers");
            LogsDcFilter.SelectedIndex = 0;
        }

        if (LogsResultFilter != null && LogsResultFilter.Items.Count == 0)
        {
            LogsResultFilter.Items.Add("All Results");
            LogsResultFilter.Items.Add("Failures");
            LogsResultFilter.Items.Add("Passes");
            LogsResultFilter.SelectedIndex = 0;
        }

        if (LogsSectionFilter != null && LogsSectionFilter.Items.Count == 0)
        {
            LogsSectionFilter.Items.Add("All test sections");
            LogsSectionFilter.SelectedIndex = 0;
        }
    }

    private void RefreshLogsWorkspace()
    {
        if (suppressLogsWorkspaceRefresh)
        {
            return;
        }

        suppressLogsWorkspaceRefresh = true;
        try
        {
            RefreshLogsFilterOptions();
            RefreshLogsView();
            UpdateLogsWorkspaceSummary();
        }
        finally
        {
            suppressLogsWorkspaceRefresh = false;
        }
    }

    private void RefreshLogsView()
    {
        logResultItemsView?.Refresh();
        RefreshVisibleLogViewer();
    }

    private void UpdateLogsWorkspaceSummary()
    {
        if (LogsVisibleCountText == null ||
            LogsFailureCountText == null ||
            LogsPassCountText == null ||
            LogsSummaryText == null ||
            LogsFileNameText == null)
        {
            return;
        }

        List<TestResult> visibleItems = logResultItemsView?.Cast<TestResult>().ToList() ?? new List<TestResult>();
        int failures = visibleItems.Count(item => item.Result.Equals("FAIL", StringComparison.OrdinalIgnoreCase));
        int passes = visibleItems.Count(item => item.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase));

        LogsVisibleCountText.Text = visibleItems.Count.ToString(CultureInfo.InvariantCulture);
        LogsFailureCountText.Text = failures.ToString(CultureInfo.InvariantCulture);
        LogsPassCountText.Text = passes.ToString(CultureInfo.InvariantCulture);
        string selectedController = LogsDcFilter?.SelectedItem as string ?? "All domain controllers";
        string selectedResult = LogsResultFilter?.SelectedItem as string ?? "All Results";
        string selectedSection = LogsSectionFilter?.SelectedItem as string ?? "All test sections";
        bool hasSearch = !string.IsNullOrWhiteSpace(LogsSearchBox?.Text);

        if (currentVisibleLogLineCount == 0)
        {
            LogsSummaryText.Text = "No log content matches the current controller, section, and text filters.";
        }
        else
        {
            LogsSummaryText.Text =
                $"Showing {currentVisibleLogLineCount} log line{(currentVisibleLogLineCount == 1 ? string.Empty : "s")} " +
                $"from {currentVisibleSectionCount} section{(currentVisibleSectionCount == 1 ? string.Empty : "s")} " +
                $"across {currentVisibleControllerCount} domain controller{(currentVisibleControllerCount == 1 ? string.Empty : "s")}. " +
                $"Controller: {selectedController}. Result: {selectedResult}. Section: {selectedSection}. " +
                (hasSearch ? "Text search is applied to the visible raw log." : "Use search to narrow the visible raw log text.");
        }

        LogsFileNameText.Text = string.IsNullOrWhiteSpace(latestLogsFilePath)
            ? "Current summary view"
            : Path.GetFileName(latestLogsFilePath);
    }

    private void RefreshLogsFilterOptions()
    {
        if (LogsDcFilter == null || LogsResultFilter == null || LogsSectionFilter == null)
        {
            return;
        }

        suppressLogsFilterEvents = true;
        try
        {
            string? currentControllerSelection = LogsDcFilter.SelectedItem as string;
            string? currentResultSelection = LogsResultFilter.SelectedItem as string;
            string? currentSectionSelection = LogsSectionFilter.SelectedItem as string;
            List<string> servers = logResultItems
                .Select(result => result.Server)
                .Where(server => !string.IsNullOrWhiteSpace(server))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(server => server, StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<string> services = logResultItems
                .Select(result => result.Service)
                .Where(service => !string.IsNullOrWhiteSpace(service))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(service => service, StringComparer.OrdinalIgnoreCase)
                .ToList();

            LogsDcFilter.Items.Clear();
            LogsDcFilter.Items.Add("All domain controllers");
            foreach (string server in servers)
            {
                LogsDcFilter.Items.Add(server);
            }

            string controllerSelectionToRestore = !string.IsNullOrWhiteSpace(currentControllerSelection) && LogsDcFilter.Items.Contains(currentControllerSelection)
                ? currentControllerSelection
                : "All domain controllers";

            if (!Equals(LogsDcFilter.SelectedItem, controllerSelectionToRestore))
            {
                LogsDcFilter.SelectedItem = controllerSelectionToRestore;
            }

            ComboBox logsResultFilter = LogsResultFilter;
            logsResultFilter.Items.Clear();
            logsResultFilter.Items.Add("All Results");
            logsResultFilter.Items.Add("Failures");
            logsResultFilter.Items.Add("Passes");

            string resultSelectionToRestore = !string.IsNullOrWhiteSpace(currentResultSelection) && logsResultFilter.Items.Contains(currentResultSelection)
                ? currentResultSelection
                : "All Results";

            if (!Equals(logsResultFilter.SelectedItem, resultSelectionToRestore))
            {
                logsResultFilter.SelectedItem = resultSelectionToRestore;
            }

            ComboBox logsSectionFilter = LogsSectionFilter;
            logsSectionFilter.Items.Clear();
            logsSectionFilter.Items.Add("All test sections");
            foreach (string service in services)
            {
                logsSectionFilter.Items.Add(service);
            }

            string sectionSelectionToRestore = !string.IsNullOrWhiteSpace(currentSectionSelection) && logsSectionFilter.Items.Contains(currentSectionSelection)
                ? currentSectionSelection
                : "All test sections";

            if (!Equals(logsSectionFilter.SelectedItem, sectionSelectionToRestore))
            {
                logsSectionFilter.SelectedItem = sectionSelectionToRestore;
            }
        }
        finally
        {
            suppressLogsFilterEvents = false;
        }
    }

    internal void LogsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshLogsView();
        UpdateLogsWorkspaceSummary();
    }

    internal void LogsFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || suppressLogsFilterEvents || suppressLogsWorkspaceRefresh)
        {
            return;
        }

        RefreshLogsView();
        UpdateLogsWorkspaceSummary();
    }

    internal void ClearLogsFilters_Click(object sender, RoutedEventArgs e)
    {
        LogsSearchBox.Text = string.Empty;
        LogsDcFilter.SelectedIndex = 0;
        LogsResultFilter.SelectedIndex = 0;
        LogsSectionFilter.SelectedIndex = 0;
        RefreshLogsView();
        UpdateLogsWorkspaceSummary();
    }

    internal async void BackToHealth_Click(object sender, RoutedEventArgs e)
    {
        await NavigateToSectionAsync(1).ConfigureAwait(true);
    }

    internal void ShowFullLog_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(latestLogsFilePath) || !File.Exists(latestLogsFilePath))
        {
            NotificationService.Show(this, "Show Full Log", "No raw log file is available for the current view.");
            return;
        }

        ShowLogFileInLogsTab(latestLogsFilePath);
    }

    internal async void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        string runsRoot = GetRunsRootDirectoryPath();
        if (!Directory.Exists(runsRoot))
        {
            NotificationService.Show(this, "No Logs", "No run logs directory found. Run tests first to generate logs.");
            return;
        }

        OpenFileDialog dialog = new()
        {
            Title = "Select a log file to view",
            InitialDirectory = runsRoot,
            Filter = "Log files (*.txt)|*.txt|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true) return;

        string filePath = dialog.FileName;
        await LoadExternalLogFileAsync(filePath);
    }

    private async Task LoadExternalLogFileAsync(string filePath)
    {
        try
        {
            string content = await File.ReadAllTextAsync(filePath).ConfigureAwait(true);
            latestLogsText = content;
            latestLogsFilePath = filePath;
            LogsFileNameText.Text = Path.GetFileName(filePath);
            RefreshLogSectionEntries(content, filePath);

            logsTextPending = false;
            _ = NavigateToSectionAsync(5);
        }
        catch (Exception ex)
        {
            NotificationService.Show(this, "Error", $"Failed to load log file:\n{ex.Message}", isError: true);
        }
    }

    internal async void dgLogsEntries_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (dgLogsEntries.SelectedItem is TestResult)
        {
            if (logsTextPending)
            {
                await LoadLogsTabContentAsync().ConfigureAwait(true);
            }

            JumpToSelectedLogEntry();
        }
    }
    internal void PopOutLogViewer_Click(object sender, RoutedEventArgs e)
    {
        string title = "Raw Log Evidence";
        if (!string.IsNullOrWhiteSpace(latestLogsFilePath))
        {
            title = Path.GetFileName(latestLogsFilePath);
        }

        string logContent = BuildFilteredLogText();
        if (string.IsNullOrWhiteSpace(logContent))
        {
            logContent = "No log content available. Run tests to populate the log viewer.";
        }

        LogViewerWindow logViewer = new(title, logContent, () => latestLogsText, () =>
        {
            string t = "Raw Log Evidence";
            if (!string.IsNullOrWhiteSpace(latestLogsFilePath))
                t = Path.GetFileName(latestLogsFilePath);
            return t;
        })
        {
            Owner = this
        };

        logViewer.Show();
    }


    private void JumpToSelectedLogEntry()
    {
        if (dgLogsEntries.SelectedItem is not TestResult selectedEntry)
        {
            return;
        }

        if (LogsListBox.ItemsSource is not IList<LogLine> logLines || logLines.Count == 0)
        {
            return;
        }

        int index = FindLogMatchIndex(logLines, selectedEntry);
        if (index < 0)
        {
            return;
        }

        LogsListBox.SelectedIndex = -1;
        LogsListBox.SelectedIndex = index;
        LogsListBox.ScrollIntoView(logLines[index]);
        LogsListBox.Focus();
    }

    private static int FindLogMatchIndex(IList<LogLine> logLines, TestResult entry)
    {
        string[] candidates =
        {
            entry.Service,
            $"{entry.Server}",
            entry.Message
        };

        for (int i = 0; i < logLines.Count; i++)
        {
            foreach (string candidate in candidates.Where(text => !string.IsNullOrWhiteSpace(text)))
            {
                if (logLines[i].Text.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private void RefreshVisibleLogViewer()
    {
        if (LogsListBox == null)
        {
            return;
        }

        string filteredLogText = BuildFilteredLogText();
        IReadOnlyList<LogLine> visibleLogLines = GetCachedLogLines(filteredLogText);
        currentVisibleLogLineCount = visibleLogLines.Count;
        LogsListBox.ItemsSource = visibleLogLines;
    }

    private string BuildFilteredLogText()
    {
        string sourceText = latestLogsText ?? string.Empty;
        string selectedController = LogsDcFilter?.SelectedItem as string ?? "All domain controllers";
        string selectedResult = LogsResultFilter?.SelectedItem as string ?? "All Results";
        string selectedSection = LogsSectionFilter?.SelectedItem as string ?? "All test sections";
        string searchText = LogsSearchBox?.Text?.Trim() ?? string.Empty;

        bool filterController = !selectedController.Equals("All domain controllers", StringComparison.OrdinalIgnoreCase);
        bool filterResult = !selectedResult.Equals("All Results", StringComparison.OrdinalIgnoreCase);
        bool filterSection = !selectedSection.Equals("All test sections", StringComparison.OrdinalIgnoreCase);
        bool filterSearch = !string.IsNullOrWhiteSpace(searchText);

        if (string.IsNullOrWhiteSpace(sourceText))
        {
            currentVisibleSectionCount = 0;
            currentVisibleControllerCount = 0;
            return string.Empty;
        }

        if (!filterController && !filterResult && !filterSection && !filterSearch)
        {
            currentVisibleSectionCount = logResultItems.Count;
            currentVisibleControllerCount = logResultItems
                .Select(result => result.Server)
                .Where(server => !string.IsNullOrWhiteSpace(server))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            return sourceText;
        }

        List<ParsedLogSection> sections = ParseLogSections(sourceText);
        IEnumerable<ParsedLogSection> filteredSections = sections.Where(section =>
            (!filterController || string.Equals(section.Server, selectedController, StringComparison.OrdinalIgnoreCase)) &&
            (!filterResult ||
                (selectedResult.Equals("Failures", StringComparison.OrdinalIgnoreCase) && string.Equals(section.Result, "FAIL", StringComparison.OrdinalIgnoreCase)) ||
                (selectedResult.Equals("Passes", StringComparison.OrdinalIgnoreCase) && string.Equals(section.Result, "PASS", StringComparison.OrdinalIgnoreCase))) &&
            (!filterSection || string.Equals(section.Service, selectedSection, StringComparison.OrdinalIgnoreCase)));

        StringBuilder builder = new();
        HashSet<string> visibleControllers = new(StringComparer.OrdinalIgnoreCase);
        int visibleSections = 0;

        foreach (ParsedLogSection section in filteredSections)
        {
            List<string> visibleLines = filterSearch
                ? section.Lines.Where(line => line.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0).ToList()
                : new List<string>(section.Lines);

            if (visibleLines.Count == 0)
            {
                continue;
            }

            visibleSections++;
            if (!string.IsNullOrWhiteSpace(section.Server))
            {
                visibleControllers.Add(section.Server);
            }

            foreach (string line in visibleLines)
            {
                builder.AppendLine(line);
            }

            builder.AppendLine();
        }

        currentVisibleSectionCount = visibleSections;
        currentVisibleControllerCount = visibleControllers.Count;

        return builder.ToString().TrimEnd();
    }

    private List<ParsedLogSection> ParseLogSections(string logText)
    {
        List<ParsedLogSection> sections = new();
        ParsedLogSection? currentSection = null;
        string currentServer = string.Empty;

        using StringReader reader = new(logText);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string trimmedLine = line.Trim();
            string? parsedServer = TryParseServerFromLogLine(trimmedLine);
            if (!string.IsNullOrWhiteSpace(parsedServer))
            {
                currentServer = parsedServer;
            }

            if (trimmedLine.StartsWith("Starting test:", StringComparison.OrdinalIgnoreCase))
            {
                currentSection = new ParsedLogSection
                {
                    Service = trimmedLine.Replace("Starting test:", string.Empty).Trim(),
                    Server = currentServer
                };
                currentSection.Lines.Add(line);
                sections.Add(currentSection);
                continue;
            }

            if (currentSection == null)
            {
                continue;
            }

            string? resultServer = TryExtractControllerFromResultLine(trimmedLine);
            if (!string.IsNullOrWhiteSpace(resultServer) &&
                string.IsNullOrWhiteSpace(currentSection.Server))
            {
                currentSection.Server = resultServer;
            }

            if (trimmedLine.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                currentSection.Result = "FAIL";
            }
            else if (trimmedLine.Contains("passed", StringComparison.OrdinalIgnoreCase))
            {
                currentSection.Result = "PASS";
            }

            currentSection.Lines.Add(line);
        }

        for (int i = 0; i < sections.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(sections[i].Server))
            {
                continue;
            }

            string? inferredServer = sections[i].Lines
                .Select(logLine => TryExtractControllerFromResultLine(logLine.Trim()) ?? TryParseServerFromLogLine(logLine.Trim()))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            if (!string.IsNullOrWhiteSpace(inferredServer))
            {
                sections[i].Server = inferredServer;
            }
        }

        return sections;
    }

    private string BuildPrivilegeInsightSummary()
    {
        if (latestInventory.PrivilegedGroupCounts.Count == 0)
        {
            return "Privilege analysis will appear after a collection runs.";
        }

        IEnumerable<string> highlights = latestInventory.PrivilegedGroupCounts
            .Where(pair => pair.Value >= 0)
            .OrderByDescending(pair => pair.Value)
            .Take(3)
            .Select(pair => $"{pair.Key}: {pair.Value}");

        return "Top privileged groups by member count: " + string.Join(" | ", highlights);
    }

    private static bool IsSecurityFinding(AdHealthFinding finding)
    {
        return finding.Category.Equals("Privilege", StringComparison.OrdinalIgnoreCase) ||
               finding.Category.Equals("Telemetry", StringComparison.OrdinalIgnoreCase) ||
               finding.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase) ||
               finding.Severity.Equals("High", StringComparison.OrdinalIgnoreCase);
    }

    private static int SeverityRank(string severity)
    {
        return severity switch
        {
            "Critical" => 4,
            "High" => 3,
            "Medium" => 2,
            "Low" => 1,
            _ => 0
        };
    }

    private IEnumerable<TestResult> ParseDCDiagOutput(string server, string output, string logFilePath)
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

        return results
            .Where(result => !string.Equals(result.Result, "In Progress", StringComparison.OrdinalIgnoreCase))
            .GroupBy(result => BuildTestResultKey(result), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static string BuildTestResultKey(TestResult result)
    {
        return string.Join("|",
            result.Service ?? string.Empty,
            result.Server ?? string.Empty,
            result.Result ?? string.Empty,
            result.Message ?? string.Empty,
            result.LogFilePath ?? string.Empty);
    }

    private static string? TryParseServerFromLogLine(string line)
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

    private static string? TryExtractControllerFromResultLine(string line)
    {
        string[] tokens = line
            .Split([' ', '\t', ',', ';', ':'], StringSplitOptions.RemoveEmptyEntries);

        foreach (string token in tokens)
        {
            if (token.EndsWith("$", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (token.Any(char.IsDigit) &&
                token.Any(char.IsLetter) &&
                !token.Contains(".", StringComparison.OrdinalIgnoreCase) &&
                token.Length >= 4)
            {
                return NormalizeControllerName(token);
            }
        }

        return null;
    }

    private static string NormalizeControllerName(string value)
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

    internal void dgFindings_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (dgFindings?.SelectedItem is not AdHealthFinding finding)
        {
            FindingDetailsText.Text = "Select a finding to see the full explanation, evidence, and next action.";
            UpdateActionButtonStates();
            return;
        }

        FindingDetailsText.Text =
            $"{finding.Summary}{Environment.NewLine}{Environment.NewLine}" +
            $"Category: {finding.Category}{Environment.NewLine}" +
            $"Severity: {finding.Severity}{Environment.NewLine}" +
            $"Target: {finding.Target}{Environment.NewLine}" +
            $"Source: {finding.Source}{Environment.NewLine}" +
            $"Status: {finding.Status}{Environment.NewLine}{Environment.NewLine}" +
            $"Evidence: {finding.Evidence}{Environment.NewLine}{Environment.NewLine}" +
            $"Recommended action: {finding.Remediation}";
        UpdateActionButtonStates();
    }

    internal void OpenFindingLog_Click(object sender, RoutedEventArgs e)
    {
        if (dgFindings?.SelectedItem is not AdHealthFinding finding ||
            string.IsNullOrWhiteSpace(finding.LogFilePath) ||
            !File.Exists(finding.LogFilePath))
        {
            NotificationService.Show(this, "Open Related Log", "The selected finding does not have an associated log file.");
            return;
        }

        ShowLogFileInLogsTab(finding.LogFilePath);
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

    private async Task<List<TestResult>> RunTimeSkewCheckAsync(string dc, string logFilePath, CancellationToken token)
    {
        List<TestResult> results = new();
        try
        {
            string output = await RunCommandAsync($"w32tm /monitor /computers:{dc}", logFilePath, token);
            bool passed = !output.Contains("error") && !output.Contains("FAIL");
            results.Add(new TestResult { Service = "Time Skew", Server = dc, Result = passed ? "PASS" : "FAIL", Message = passed ? "Time sync OK." : "Time skew detected or w32tm error.", LogFilePath = logFilePath });
        }
        catch { results.Add(new TestResult { Service = "Time Skew", Server = dc, Result = "FAIL", Message = "Time sync check failed.", LogFilePath = logFilePath }); }
        return results;
    }

    private async Task<List<TestResult>> RunLdapBindCheckAsync(string dc, string logFilePath, CancellationToken token)
    {
        List<TestResult> results = new();
        try
        {
            string script = $"try {{ $root = [ADSI]\"LDAP://{dc}\"; $root.distinguishedName; Write-Output \"PASS\" }} catch {{ Write-Output \"FAIL\" }}";
            string output = await RunPowerShellScriptAsync(script, logFilePath, token);
            bool passed = output.Contains("PASS") && !output.Contains("FAIL");
            results.Add(new TestResult { Service = "LDAP Bind", Server = dc, Result = passed ? "PASS" : "FAIL", Message = passed ? "LDAP bind succeeded." : "LDAP bind failed.", LogFilePath = logFilePath });
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

    private async Task<List<TestResult>> RunSmbLdapSigningCheckAsync(string dc, string logFilePath, CancellationToken token)
    {
        List<TestResult> results = new();
        try
        {
            string script = $"Get-Service -ComputerName {dc} -Name LanmanServer -ErrorAction SilentlyContinue | ForEach-Object {{ if ($_.Status -eq 'Running') {{ 'SMB=Running' }} else {{ 'SMB=' + $_.Status }} }}";
            string output = await RunPowerShellScriptAsync(script, logFilePath, token);
            bool smbOk = output.Contains("SMB=Running");
            results.Add(new TestResult { Service = "SMB/LDAP Signing", Server = dc, Result = smbOk ? "PASS" : "FAIL", Message = smbOk ? "Server service running." : "Server service not running.", LogFilePath = logFilePath });
        }
        catch { results.Add(new TestResult { Service = "SMB/LDAP Signing", Server = dc, Result = "FAIL", Message = "Signing check threw exception.", LogFilePath = logFilePath }); }
        return results;
    }

    internal void ViewSelectedLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            List<TestResult> selected = testResultsGrid.SelectedItems.OfType<TestResult>().ToList();
            if (selected.Count == 0)
            {
                new SuccessNotification("No Selection", "No results selected. Check the checkbox(es) to select result(s) to view.", isError: true).ShowDialog();
                return;
            }

            List<TestResult> validResults = selected.Where(r => !string.IsNullOrWhiteSpace(r.LogFilePath) && File.Exists(r.LogFilePath)).ToList();
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

            List<string> uniqueLogFiles = validResults
                .Select(result => result.LogFilePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (uniqueLogFiles.Count > 1)
            {
                latestLogsText = BuildMergedLogText(uniqueLogFiles);
                latestLogsFilePath = uniqueLogFiles[0];
                RefreshLogSectionEntries(latestLogsText, latestLogsFilePath);
                LogsFileNameText.Text = $"Combined full logs from {uniqueLogFiles.Count} files";
                logsTextPending = false;
                _ = NavigateToSectionAsync(5);
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
            string fileSources = string.Join(", ", validResults
                .Select(r => Path.GetFileName(r.LogFilePath))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            string serviceSources = string.Join(", ", selected
                .Where(r => !string.IsNullOrWhiteSpace(r.Service))
                .Select(r => r.Service)
                .Distinct());
            LogsFileNameText.Text = $"({validResults.Count} result(s)) {serviceSources} — {fileSources}";
            logsTextPending = false;
            _ = NavigateToSectionAsync(5);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        dashboardRefreshTimer.Stop();
        dashboardRefreshTimer.Tick -= DashboardRefreshTimer_Tick;
        cancellationTokenSource?.Cancel();
        cancellationTokenSource?.Dispose();
        scheduledLogCache.Clear();
        cachedLogLines = Array.Empty<LogLine>();
        cachedLogLinesLength = -1;
        cachedLogLinesHash = 0;
        latestLogsText = string.Empty;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        Dispose();
        base.OnClosing(e);
    }

    private string GenerateLotteryNumbers()
    {
        Random rnd = new();
        return string.Join(", ", Enumerable.Range(1, 59).OrderBy(_ => rnd.Next()).Take(6).OrderBy(n => n));
    }

    private void ShowLotteryPopup(string message)
    {
        FlowDocument document = new();
        document.Blocks.Add(new Paragraph(new Run(message))
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 16,
            Foreground = Brushes.DarkBlue,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(20)
        });

        Window popup = new()
        {
            Title = "Lottery Numbers",
            Height = 300,
            Width = 500,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new RichTextBox
            {
                Document = document,
                IsReadOnly = true,
                Margin = new Thickness(10)
            }
        };
        popup.ShowDialog();
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        AboutWindow aboutWindow = new();
        aboutWindow.Owner = this;
        aboutWindow.ShowDialog();
    }

    private void SendEmailWithAttachment(string subject, string bodyDetail, string attachmentPath)
    {
        try
        {
            string toAddress = recipientEmail;
            string fingerprint = string.Join("|", toAddress, subject, attachmentPath ?? string.Empty, bodyDetail ?? string.Empty);
            DateTime nowUtc = DateTime.UtcNow;
            if (string.Equals(lastEmailFingerprint, fingerprint, StringComparison.Ordinal) &&
                (nowUtc - lastEmailSentUtc) < TimeSpan.FromSeconds(30))
            {
                Debug.WriteLine("Duplicate email send suppressed.");
                return;
            }

            using MailMessage mail = new("ADGuardian@funasset.com", toAddress)
            {
                Subject = subject,
                IsBodyHtml = true
            };

            bool isFailed = subject.Contains("[FAILED]", StringComparison.OrdinalIgnoreCase);
            string headerColor = isFailed ? "#C62828" : "#2E7D32";
            string headerBg = isFailed ? "#FFEBEE" : "#E8F5E9";
            string statusText = isFailed ? "Some tests failed — review the details below." : "All tests completed successfully.";

            string htmlBody = $@"
<html>
  <head>
    <style>
      body {{
        font-family: 'Segoe UI', Arial, sans-serif;
        font-size: 14px;
        color: #333;
        margin: 0;
        padding: 0;
      }}
      .header-bar {{
        background-color: {headerBg};
        border-left: 5px solid {headerColor};
        padding: 14px 18px;
        margin-bottom: 16px;
        border-radius: 4px;
      }}
      .header-title {{
        font-size: 17px;
        font-weight: bold;
        color: {headerColor};
      }}
      .header-time {{
        font-size: 12px;
        color: #666;
        margin-top: 2px;
      }}
      .content {{
        margin-bottom: 15px;
        padding: 0 4px;
      }}
      .details {{
        background-color: #f7f7f7;
        padding: 12px 14px;
        border: 1px solid #ddd;
        border-radius: 5px;
      }}
      .footer {{
        font-size: 12px;
        color: #777;
        margin-top: 20px;
        border-top: 1px solid #eee;
        padding-top: 10px;
      }}
    </style>
  </head>
  <body>
    <div class='header-bar'>
      <div class='header-title'>{subject}</div>
      <div class='header-time'>{DateTime.Now:f}</div>
    </div>
    <div class='content'>
      <p>{statusText}</p>
      <div class='details'>
         {bodyDetail}
      </div>
      <p>Please review the attached log file for detailed information.</p>
    </div>
    <div class='footer'>
      This is an automated message from AD Guardian.
    </div>
  </body>
</html>";

            mail.Body = htmlBody;
            if (!string.IsNullOrWhiteSpace(attachmentPath) && File.Exists(attachmentPath))
            {
                mail.Attachments.Add(new Attachment(attachmentPath));
            }

            using SmtpClient client = new("smtp.gmail.com", 587)
            {
                Credentials = new NetworkCredential("adguardianutility@gmail.com", "ihai btfi qeja nbqp"),
                EnableSsl = true,
                Timeout = (int)ScheduledEmailTimeout.TotalMilliseconds
            };
            client.Send(mail);
            lastEmailFingerprint = fingerprint;
            lastEmailSentUtc = nowUtc;
        }
        catch (Exception ex)
        {
            if (isScheduledLaunch)
            {
                Debug.WriteLine("Failed to send scheduled email: " + ex);
            }
            else
            {
                NotificationService.Show(this, "Email Error", "Failed to send email: " + ex.Message, isError: true);
            }
        }
    }
}
