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

    internal sealed class ParsedLogSection
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
    private bool testDnsCheck = false;
    private bool testReplication = true;
    private bool testTimeSkew = false;
    private bool testLdapBind = false;
    private bool testCertDhcp = false;
    private bool testSmbLdapSigning = false;
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
    internal static readonly Brush ActiveNavBgBrush = FrozenBrush(Color.FromRgb(26, 115, 232));
    internal static readonly Brush InactiveNavFgBrush = FrozenBrush(Color.FromRgb(176, 188, 201));

    // Shared frozen brushes for dashboard/findings/history rendering.
    // Frozen brushes are thread-safe, don't raise change notifications,
    // and can be shared across elements — avoiding per-refresh allocations.
    internal static readonly Brush FailBrushCached = FrozenBrush(Color.FromRgb(211, 47, 47));
    internal static readonly Brush PassBrushCached = FrozenBrush(Color.FromRgb(46, 125, 50));
    internal static readonly Brush NeutralBrushCached = FrozenBrush(Color.FromRgb(200, 200, 200));
    internal static readonly Brush SeparatorBrushCached = FrozenBrush(Color.FromRgb(230, 236, 242));
    internal static readonly Brush BodyTextBrushCached = FrozenBrush(Color.FromRgb(79, 100, 121));
    internal static readonly Brush HighSeverityBrushCached = FrozenBrush(Color.FromRgb(239, 108, 0));
    internal static readonly Brush MediumSeverityBrushCached = FrozenBrush(Color.FromRgb(245, 166, 35));
    internal static readonly Brush LowSeverityBrushCached = FrozenBrush(Color.FromRgb(100, 100, 100));
    internal static readonly Brush AccentBlueBrushCached = FrozenBrush(Color.FromRgb(26, 115, 232));


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
    private bool loadingLogsTabContent;
    private bool schedulerTasksLoaded;
    private bool healthPageBound;
    private bool findingsPageBound;
    private bool historyPageBound;
    private bool logsPageBound;
    private bool securityPageBound;
    private bool schedulerPageBound;
    private bool healthDetailsTextPending;
    private bool historyFullyLoaded;
    private Task? historyLoadTask;
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


    public MainWindow()
    {
        Stopwatch startupStopwatch = Stopwatch.StartNew();
        string[] args = Environment.GetCommandLineArgs();
        isScheduledLaunch = args.Length > 1 && args[1].Equals("-scheduled", StringComparison.OrdinalIgnoreCase);
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-GB");
        Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-GB");
        appStateStore = new AppStateStore(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdHealthMonitor", "AppState.db"));

        Trace.WriteLine($"[Startup] Pre-InitializeComponent: {startupStopwatch.ElapsedMilliseconds}ms");
        InitializeComponent();
        Trace.WriteLine($"[Startup] InitializeComponent: {startupStopwatch.ElapsedMilliseconds}ms");
        Trace.WriteLine($"[Startup] Post-InitializeComponent setup: {startupStopwatch.ElapsedMilliseconds}ms");
        dashboardRefreshTimer.Tick += DashboardRefreshTimer_Tick;
        UpdateActionButtonStates();
        InitializeBoundViews();
        cancellationTokenSource = new CancellationTokenSource();
        UpdateNavigationState();
        Trace.WriteLine($"[Startup] Constructor complete: {startupStopwatch.ElapsedMilliseconds}ms");
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

    private async Task DeferStartupInitializationAsync(Stopwatch startupStopwatch)
    {
        await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
        await InitializeAppStateAsync(startupStopwatch).ConfigureAwait(true);
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
        if (_HealthTab == null) return;
        bool showDetails = DetailsPanel.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(latestRunDetailsText);
        DetailsPanel.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateHealthSummaryText()
    {
        if (_HealthTab == null || HealthSummaryText == null)
        {
            return;
        }

        int configuredControllers = CountConfiguredDomainControllers();
        int total = allResults.Count;

        if (total == 0)
        {
            HealthSummaryText.Text = "No run data loaded yet.";
            return;
        }

        // Use for-loops and a HashSet instead of LINQ chain to avoid allocations.
        int passed = 0, failed = 0;
        HashSet<string> distinctServers = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < total; i++)
        {
            TestResult r = allResults[i];
            if (r.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase)) passed++;
            else if (r.Result.Equals("FAIL", StringComparison.OrdinalIgnoreCase)) failed++;
            if (!string.IsNullOrWhiteSpace(r.Server)) distinctServers.Add(r.Server);
        }

        int activeFindings = 0;
        for (int i = 0; i < allFindings.Count; i++)
        {
            if (IsActiveSeverity(allFindings[i].Severity)) activeFindings++;
        }

        HealthSummaryText.Text =
            $"Current view shows {total} test result(s) across {distinctServers.Count} of {configuredControllers} configured domain controller(s). " +
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

    private async Task InitializeAppStateAsync(Stopwatch startupStopwatch)
    {
        try
        {
            await Task.Run(appStateStore.Initialize).ConfigureAwait(true);
            Trace.WriteLine($"[Startup] DB Initialize: {startupStopwatch.ElapsedMilliseconds}ms");

            int startupHistoryLimit = isScheduledLaunch ? int.MaxValue : 10;
            AppStartupState startupState = await Task.Run(() => appStateStore.LoadStartupState(startupHistoryLimit)).ConfigureAwait(true);
            Trace.WriteLine($"[Startup] LoadStartupState: {startupStopwatch.ElapsedMilliseconds}ms");

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
            historyEntries = startupState.History;
            historyEntries.Sort((a, b) => b.RunDate.CompareTo(a.RunDate));
            historyFullyLoaded = isScheduledLaunch || historyEntries.Count < startupHistoryLimit;
            SyncHistoryItems(historyEntries);
            ReplaceScheduledTasks(startupState.ScheduledTasks);
            schedulerTasksLoaded = true;
            Trace.WriteLine($"[Startup] Apply state + sync: {startupStopwatch.ElapsedMilliseconds}ms");

            if (MainTabControl.SelectedIndex == 7)
            {
                LoadSettingsIntoPage();
            }
            else if (MainTabControl.SelectedIndex == 8)
            {
                RefreshSchedulerTaskList();
            }

            RefreshDashboardNow();
            Trace.WriteLine($"[Startup] Dashboard refresh: {startupStopwatch.ElapsedMilliseconds}ms");
            ScheduleLaunchUpdateCheck();
            Trace.WriteLine($"[Startup] TOTAL startup: {startupStopwatch.ElapsedMilliseconds}ms");
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
        _ = EnsureResultItemsView();
    }

    private ICollectionView EnsureResultItemsView()
    {
        if (resultItemsView == null)
        {
            resultItemsView = CollectionViewSource.GetDefaultView(resultItems);
            resultItemsView.Filter = ResultItemsFilter;
        }

        return resultItemsView;
    }

    private ICollectionView EnsureLogResultItemsView()
    {
        if (logResultItemsView == null)
        {
            logResultItemsView = CollectionViewSource.GetDefaultView(logResultItems);
            logResultItemsView.Filter = LogResultItemsFilter;
            InitializeLogsFilters();
        }

        return logResultItemsView;
    }

    private ICollectionView EnsureHistoryItemsView()
    {
        if (historyItemsView == null)
        {
            historyItemsView = CollectionViewSource.GetDefaultView(historyItems);
            historyItemsView.Filter = HistoryItemsFilter;
        }

        return historyItemsView;
    }

    private ICollectionView EnsureFindingItemsView()
    {
        if (findingItemsView == null)
        {
            findingItemsView = CollectionViewSource.GetDefaultView(findingItems);
            findingItemsView.Filter = FindingItemsFilter;
        }

        return findingItemsView;
    }

    private void EnsurePageBindings(int pageIndex)
    {
        switch (pageIndex)
        {
            case 3 when _InfrastructureTab == null:
                _ = EnsureInfrastructureTab();
                break;
            case 1 when !healthPageBound:
                testResultsGrid.ItemsSource = EnsureResultItemsView();
                healthPageBound = true;
                break;
            case 2 when !findingsPageBound:
                dgFindings.ItemsSource = EnsureFindingItemsView();
                findingsPageBound = true;
                break;
            case 4 when !historyPageBound:
                dgTestHistory.ItemsSource = EnsureHistoryItemsView();
                historyPageBound = true;
                _ = EnsureHistoryLoadedAsync();
                break;
            case 5 when !logsPageBound:
                dgLogsEntries.ItemsSource = EnsureLogResultItemsView();
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

        if (_HealthTab != null)
        {
            RunButton.IsEnabled = !isRunInProgress;
            StopButton.IsEnabled = isRunInProgress;
            ExportButton.IsEnabled = hasResults;
            ExecutiveSummaryButton.IsEnabled = hasResults;
            SearchBox.IsEnabled = hasResults;
            SearchButton.IsEnabled = hasResults;
            ViewSelectedLogButton.IsEnabled = testResultsGrid.SelectedItems.Count > 0;
        }

        if (_FindingsTab != null)
        {
            OpenFindingLogButton.IsEnabled = dgFindings?.SelectedItem is AdHealthFinding finding &&
                                             !string.IsNullOrWhiteSpace(finding.LogFilePath) &&
                                             File.Exists(finding.LogFilePath);
        }
    }

    private void SyncHistoryItems(IEnumerable<TestHistoryEntry> items)
    {
        ReplaceCollection(historyItems, items);
        if (historyPageBound)
        {
            using (EnsureHistoryItemsView().DeferRefresh())
            {
            }
        }
    }

    private void ScheduleLaunchUpdateCheck()
    {
        if (isScheduledLaunch)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(async () =>
        {
            await UpdateManager.ScheduleLaunchUpdateCheckAsync(this).ConfigureAwait(true);
        }, DispatcherPriority.ApplicationIdle);
    }

    private void SyncFindingItems()
    {
        ReplaceCollection(findingItems, allFindings);
        if (findingsPageBound)
        {
            using (EnsureFindingItemsView().DeferRefresh())
            {
            }
        }
    }

    private void ReplaceScheduledTasks(IEnumerable<ScheduledTask> items)
    {
        scheduledTasks.Clear();
        scheduledTasks.AddRange(items);
        if (_SchedulerTab != null)
        {
            SchedulerTaskList?.Items.Refresh();
        }
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
        if (healthPageBound) testResultsGrid.ItemsSource = EnsureResultItemsView();
        if (logsPageBound) dgLogsEntries.ItemsSource = EnsureLogResultItemsView();
        if (historyPageBound) dgTestHistory.ItemsSource = EnsureHistoryItemsView();
        if (findingsPageBound) dgFindings.ItemsSource = EnsureFindingItemsView();
        if (securityPageBound) dgSecurityFindings.ItemsSource = securityFindingItems;
        UpdateHealthResultsLayout();
        UpdateHealthSummaryText();
        HideRunProgress();
        RefreshDashboard();
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

    private void ApplySearchFilter()
    {
        resultItemsView?.Refresh();
    }

    private static string PadRight(string? value, int width)
    {
        return (value ?? string.Empty).PadRight(width);
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
                // Manual loops: avoids LINQ Select/Where/Select/ToHashSet allocations (2x 3-chain pipelines)
                HashSet<string> protectedFiles = new(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < historyEntries.Count; i++)
                {
                    string? path = historyEntries[i].LogFilePath;
                    if (!string.IsNullOrWhiteSpace(path))
                        protectedFiles.Add(Path.GetFullPath(path));
                }
                HashSet<string> protectedRunDirectories = new(StringComparer.OrdinalIgnoreCase);
                foreach (string filePath in protectedFiles)
                {
                    string? dir = GetManagedRunDirectoryPath(filePath);
                    if (!string.IsNullOrWhiteSpace(dir))
                        protectedRunDirectories.Add(Path.GetFullPath(dir));
                }

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

                    runDirectories.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

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

                    allRunDirs.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                    for (int ai = 100; ai < allRunDirs.Count; ai++)
                    {
                        DirectoryInfo extraDirectory = allRunDirs[ai];
                        if (protectedRunDirectories.Contains(extraDirectory.FullName))
                            continue;
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
                    .GetFiles("*.txt", SearchOption.TopDirectoryOnly);
                Array.Sort(legacyFlatFiles, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

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

                FileInfo[] allLogFiles = new DirectoryInfo(LogDirectoryPath)
                    .GetFiles("*.txt", SearchOption.TopDirectoryOnly);
                Array.Sort(allLogFiles, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int fi = 25; fi < allLogFiles.Length; fi++)
                {
                    FileInfo extraFile = allLogFiles[fi];
                    if (protectedFiles.Contains(extraFile.FullName))
                        continue;
                    extraFile.Delete();
                }
            }).ConfigureAwait(true);
        }
        catch
        {
        }
    }

    private async Task EnsureStartupInitializedAsync()
    {
        if (startupInitializationTask != null)
        {
            await startupInitializationTask.ConfigureAwait(true);
            startupInitializationTask = null;
        }
    }

    private void ApplyFindingsFilter()
    {
        if (findingsPageBound)
        {
            EnsureFindingItemsView().Refresh();
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

}
