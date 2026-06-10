// MainWindow partial class - Logs functionality
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
    private static readonly Brush LogNormalBrush = FrozenBrush(Color.FromRgb(52, 73, 94));
    private static readonly Brush LogFailBrush = FrozenBrush(Color.FromRgb(211, 47, 47));
    private static readonly Brush LogPassBrush = FrozenBrush(Color.FromRgb(46, 125, 50));
    private bool LogResultItemsFilter(object item)
    {
        if (item is not TestResult result)
        {
            return false;
        }

        string searchText = LogsSearchBox?.Text?.Trim() ?? string.Empty;
        string selectedDc = LogsDcFilter?.SelectedItem as string ?? "All domain controllers";
        string selectedResult = LogsResultFilter?.SelectedItem as string ?? "All Results";
        string selectedSection = LogsSectionFilter?.SelectedItem as string ?? "All test sections";

        return MatchesLogResultFilter(result, searchText, selectedDc, selectedResult, selectedSection);
    }

    internal static bool MatchesLogResultFilter(
        TestResult result,
        string searchText,
        string selectedDc,
        string selectedResult,
        string selectedSection)
    {
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

        string search = searchText.Trim().ToLowerInvariant();
        return (result.Service?.ToLowerInvariant().Contains(search) ?? false) ||
               (result.Server?.ToLowerInvariant().Contains(search) ?? false) ||
               (result.Result?.ToLowerInvariant().Contains(search) ?? false) ||
               (result.Message?.ToLowerInvariant().Contains(search) ?? false);
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

    internal static List<LogLine> BuildLogLines(string text)
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
            NavigateToSection(5);
        }
        catch (Exception ex)
        {
            NotificationService.Show(this, "Error", $"Failed to load log file: {ex.Message}", isError: true);
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

        int visibleCount = 0, failures = 0, passes = 0;
        if (logResultItemsView != null)
        {
            foreach (TestResult item in logResultItemsView)
            {
                visibleCount++;
                if (item.Result.Equals("FAIL", StringComparison.OrdinalIgnoreCase)) failures++;
                else if (item.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase)) passes++;
            }
        }

        LogsVisibleCountText.Text = visibleCount.ToString(CultureInfo.InvariantCulture);
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
            // Manual distinct+sort: avoids LINQ Select/Where/Distinct/OrderBy/ToList allocations
            HashSet<string> serverSet = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> serviceSet = new(StringComparer.OrdinalIgnoreCase);
            foreach (TestResult r in logResultItems)
            {
                if (!string.IsNullOrWhiteSpace(r.Server))
                    serverSet.Add(r.Server);
                if (!string.IsNullOrWhiteSpace(r.Service))
                    serviceSet.Add(r.Service);
            }
            List<string> servers = new(serverSet);
            servers.Sort(StringComparer.OrdinalIgnoreCase);
            List<string> services = new(serviceSet);
            services.Sort(StringComparer.OrdinalIgnoreCase);

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

    internal void LogsSearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        LogsSearchPlaceholder.Visibility = Visibility.Collapsed;
    }

    internal void LogsSearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LogsSearchBox.Text))
        {
            LogsSearchPlaceholder.Visibility = Visibility.Visible;
        }
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

    internal void BackToHealth_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSection(1);
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
            NavigateToSection(5);
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

    internal static int FindLogMatchIndex(IList<LogLine> logLines, TestResult entry)
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

        (string filtered, int sections, int controllers) = FilterLogSections(sourceText, selectedController, selectedResult, selectedSection, searchText);
        currentVisibleSectionCount = sections;
        currentVisibleControllerCount = controllers;
        return filtered;
    }

    /// <summary>
    /// Filters parsed log sections by controller, result, section, and search text.
    /// Extracted from BuildFilteredLogText for testability.
    /// </summary>
    internal static (string Text, int SectionCount, int ControllerCount) FilterLogSections(
        string sourceText,
        string selectedController,
        string selectedResult,
        string selectedSection,
        string searchText)
    {
        bool filterController = !selectedController.Equals("All domain controllers", StringComparison.OrdinalIgnoreCase);
        bool filterResult = !selectedResult.Equals("All Results", StringComparison.OrdinalIgnoreCase);
        bool filterSection = !selectedSection.Equals("All test sections", StringComparison.OrdinalIgnoreCase);
        bool filterSearch = !string.IsNullOrWhiteSpace(searchText);

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
            IReadOnlyList<string> visibleLines = filterSearch
                ? section.Lines.Where(line => line.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0).ToList()
                : section.Lines;

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

        return (builder.ToString().TrimEnd(), visibleSections, visibleControllers.Count);
    }

    internal static List<ParsedLogSection> ParseLogSections(string logText)
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

            string? inferredServer = null;
            // Manual loop: avoids LINQ Select/FirstOrDefault delegate allocations
            foreach (string logLine in sections[i].Lines)
            {
                string trimmed = logLine.Trim();
                inferredServer = TryExtractControllerFromResultLine(trimmed) ?? TryParseServerFromLogLine(trimmed);
                if (!string.IsNullOrWhiteSpace(inferredServer))
                    break;
            }

            if (!string.IsNullOrWhiteSpace(inferredServer))
            {
                sections[i].Server = inferredServer;
            }
        }

        return sections;
    }

}
