// MainWindow partial class - History functionality
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
    internal void dpHistoryFilter_SelectedDateChanged(object sender, SelectionChangedEventArgs e) => ApplyHistoryFilter();

    internal void txtHistorySearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyHistoryFilter();

    internal void ClearHistoryFilters_Click(object sender, RoutedEventArgs e)
    {
        dpHistoryFilter.SelectedDate = null;
        txtHistorySearch.Text = string.Empty;
        if (historyPageBound)
        {
            EnsureHistoryItemsView().Refresh();
        }
    }

    private void ApplyHistoryFilter()
    {
        if (historyPageBound)
        {
            EnsureHistoryItemsView().Refresh();
        }
    }
    private async Task SaveTestHistoryAsync(TestHistoryEntry entry)
    {
        try
        {
            await EnsureHistoryLoadedAsync().ConfigureAwait(true);

            if (IsDuplicateHistoryEntry(historyEntries, entry))
            {
                return;
            }

            historyEntries.Add(entry);
            historyEntries.Sort((a, b) => b.RunDate.CompareTo(a.RunDate));
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

            loadedHistory.Sort((a, b) => b.RunDate.CompareTo(a.RunDate));
            historyEntries = loadedHistory;
            historyFullyLoaded = true;
            SyncHistoryItems(historyEntries);
            RefreshDashboard();
        }
        catch (Exception ex)
        {
            NotificationService.Show(this, "Error", "Error loading test history: " + ex.Message, isError: true);
        }
    }

    private Task EnsureHistoryLoadedAsync()
    {
        if (historyFullyLoaded)
        {
            return Task.CompletedTask;
        }

        if (historyLoadTask == null || historyLoadTask.IsCompleted)
        {
            historyLoadTask = LoadHistoryAsync();
        }

        return historyLoadTask;
    }

    internal async void ViewSelectedHistoryRun_Click(object sender, RoutedEventArgs e)
    {
        // Manual search: avoids LINQ OfType/FirstOrDefault allocation
        TestHistoryEntry? entry = dgTestHistory.SelectedItem as TestHistoryEntry;
        if (entry == null)
        {
            foreach (object item in dgTestHistory.SelectedItems)
            {
                if (item is TestHistoryEntry te)
                {
                    entry = te;
                    break;
                }
            }
        }

        if (entry != null)
        {
            string logFilePath = entry.LogFilePath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(logFilePath))
            {
                NotificationService.Show(this, "View Selected", "The selected run does not have a log file path.");
                return;
            }

            NavigateToSection(1);
            await ShowScheduledResultsAsync(logFilePath).ConfigureAwait(true);
        }
        else
        {
            NotificationService.Show(this, "View Selected", "Please select exactly one history run to open.");
        }
    }

    internal async void DeleteSelectedHistory_Click(object sender, RoutedEventArgs e)
    {
        // Manual cast: avoids LINQ OfType/ToList allocations
        List<TestHistoryEntry> selectedEntries = new();
        foreach (object item in dgTestHistory.SelectedItems)
        {
            if (item is TestHistoryEntry te)
                selectedEntries.Add(te);
        }

        if (selectedEntries.Count == 0 && dgTestHistory.SelectedItem is TestHistoryEntry singleEntry)
        {
            selectedEntries.Add(singleEntry);
        }

        if (selectedEntries.Count > 0)
        {
            List<TestHistoryEntry> previousEntries = historyEntries.ToList();
            // Manual filter + sort: avoids LINQ Select/ToHashSet + Where/OrderByDescending/ToList allocations
            HashSet<string> removalKeys = new(StringComparer.Ordinal);
            for (int i = 0; i < selectedEntries.Count; i++)
                removalKeys.Add(BuildHistoryEntryKey(selectedEntries[i]));

            List<TestHistoryEntry> keptEntries = new();
            for (int i = 0; i < historyEntries.Count; i++)
            {
                TestHistoryEntry entry = historyEntries[i];
                if (!removalKeys.Contains(BuildHistoryEntryKey(entry)))
                    keptEntries.Add(entry);
            }
            keptEntries.Sort((a, b) => b.RunDate.CompareTo(a.RunDate));
            historyEntries = keptEntries;

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

    internal static string BuildHistoryEntryKey(TestHistoryEntry entry)
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

    internal static bool IsDuplicateHistoryEntry(
        IReadOnlyList<TestHistoryEntry> existingEntries,
        TestHistoryEntry candidate)
    {
        // Manual duplicate check: avoids LINQ Any() allocation
        for (int i = 0; i < existingEntries.Count; i++)
        {
            TestHistoryEntry existing = existingEntries[i];
            if (existing.Total == candidate.Total &&
                existing.Passed == candidate.Passed &&
                existing.Failed == candidate.Failed &&
                string.Equals(existing.TestType, candidate.TestType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.Details, candidate.Details, StringComparison.Ordinal) &&
                string.Equals(existing.LogFilePath, candidate.LogFilePath, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs((existing.RunDate - candidate.RunDate).TotalMinutes) < 2)
                return true;
        }
        return false;
    }

    private async Task DeleteHistoryLogsAsync(
        IReadOnlyCollection<TestHistoryEntry> deletedEntries,
        IReadOnlyCollection<TestHistoryEntry> remainingEntries,
        IReadOnlyCollection<TestHistoryEntry> previousEntries)
    {
        try
        {
            // Manual loops: avoids LINQ Select/Where/Select/ToHashSet + Any allocations
            foreach (TestHistoryEntry entry in deletedEntries)
            {
                if (string.IsNullOrWhiteSpace(entry.LogFilePath)) continue;
                scheduledLogCache.Remove(Path.GetFullPath(entry.LogFilePath));
            }

            await Task.Run(() =>
            {
                HashSet<string> remainingLogPaths = new(StringComparer.OrdinalIgnoreCase);
                foreach (TestHistoryEntry e in remainingEntries)
                {
                    if (!string.IsNullOrWhiteSpace(e.LogFilePath))
                        remainingLogPaths.Add(Path.GetFullPath(e.LogFilePath));
                }

                HashSet<string> previousLogPaths = new(StringComparer.OrdinalIgnoreCase);
                foreach (TestHistoryEntry e in previousEntries)
                {
                    if (!string.IsNullOrWhiteSpace(e.LogFilePath))
                        previousLogPaths.Add(Path.GetFullPath(e.LogFilePath));
                }

                foreach (TestHistoryEntry deletedEntry in deletedEntries)
                {
                    if (string.IsNullOrWhiteSpace(deletedEntry.LogFilePath))
                        continue;

                    string fullLogPath = Path.GetFullPath(deletedEntry.LogFilePath);
                    string? runDirectory = GetManagedRunDirectoryPath(fullLogPath);
                    if (!string.IsNullOrWhiteSpace(runDirectory))
                    {
                        bool directoryStillReferenced = false;
                        foreach (string rp in remainingLogPaths)
                        {
                            if (string.Equals(GetManagedRunDirectoryPath(rp), runDirectory, StringComparison.OrdinalIgnoreCase))
                            {
                                directoryStillReferenced = true;
                                break;
                            }
                        }

                        if (!directoryStillReferenced && Directory.Exists(runDirectory))
                            Directory.Delete(runDirectory, true);

                        continue;
                    }

                    if (!remainingLogPaths.Contains(fullLogPath) && previousLogPaths.Contains(fullLogPath) && File.Exists(fullLogPath))
                        File.Delete(fullLogPath);
                }
            }).ConfigureAwait(true);
        }
        catch
        {
        }
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

    private bool HistoryItemsFilter(object item)
    {
        if (item is not TestHistoryEntry entry)
        {
            return false;
        }

        DateTime? selectedDate = dpHistoryFilter?.SelectedDate;
        string searchText = txtHistorySearch?.Text?.Trim() ?? string.Empty;

        return MatchesHistoryFilter(entry, searchText, selectedDate);
    }

    internal static bool MatchesHistoryFilter(
        TestHistoryEntry entry,
        string searchText,
        DateTime? selectedDate)
    {
        if (selectedDate.HasValue && entry.RunDate.Date != selectedDate.Value.Date)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        string search = searchText.Trim().ToLowerInvariant();
        return (entry.Details?.ToLowerInvariant().Contains(search) ?? false) ||
               entry.Total.ToString(CultureInfo.InvariantCulture).Contains(search) ||
               entry.Passed.ToString(CultureInfo.InvariantCulture).Contains(search) ||
               entry.Failed.ToString(CultureInfo.InvariantCulture).Contains(search) ||
               (entry.TestType?.ToLowerInvariant().Contains(search) ?? false);
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
                Foreground = AccentBlueBrushCached,
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

}
