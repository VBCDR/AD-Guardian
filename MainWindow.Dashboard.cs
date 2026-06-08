// MainWindow partial class - Dashboard functionality
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
    private string? _dashboardHash;
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

        if (_HealthTab != null)
        {
            HealthScoreText.Text = healthScore.ToString(CultureInfo.InvariantCulture);
            CriticalFindingsText.Text = criticalFindings.ToString(CultureInfo.InvariantCulture);
            PassingTestsText.Text = passingTests.ToString(CultureInfo.InvariantCulture);
            DomainControllerCountText.Text = configuredControllers.ToString(CultureInfo.InvariantCulture);
        }

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

        if (_InfrastructureTab != null)
        {
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
        }

        if (_SecurityTab != null)
        {
            PrivilegedInsightsText.Text = BuildPrivilegeInsightSummary();
            SecuritySummaryText.Text = latestTelemetry.TotalServices > 0
                ? $"Security view includes {allFindings.Count(f => IsSecurityFinding(f))} security-oriented finding(s) across privilege and telemetry signals."
                : "Security findings are derived from privileged group breadth, failing directory tests, and service telemetry.";
        }
        if (_FindingsTab != null)
        {
            FindingsOpenCountText.Text = activeFindings.Count.ToString(CultureInfo.InvariantCulture);
            FindingsHighCountText.Text = highOrAboveFindings.ToString(CultureInfo.InvariantCulture);
            FindingsCriticalCountText.Text = criticalFindings.ToString(CultureInfo.InvariantCulture);
        }

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
        if (_HealthTab != null)
        {
            HealthScoreText.Text = snapshot.HealthScore.ToString(CultureInfo.InvariantCulture);
            CriticalFindingsText.Text = snapshot.CriticalFindings.ToString(CultureInfo.InvariantCulture);
            PassingTestsText.Text = snapshot.PassingTests.ToString(CultureInfo.InvariantCulture);
            DomainControllerCountText.Text = snapshot.ConfiguredDomainControllers.ToString(CultureInfo.InvariantCulture);
        }

        HomeHealthScoreText.Text = snapshot.HealthScore.ToString(CultureInfo.InvariantCulture);
        HomeCriticalText.Text = snapshot.CriticalFindings.ToString(CultureInfo.InvariantCulture);
        HomePassingText.Text = snapshot.PassingTests.ToString(CultureInfo.InvariantCulture);
        HomePassRateText.Text = snapshot.LastRunTotal > 0
            ? $"{snapshot.LastRunPassed * 100 / Math.Max(1, snapshot.LastRunTotal)}%"
            : "--";
        HomeTotalRunsText.Text = snapshot.TotalRuns.ToString(CultureInfo.InvariantCulture);
        HomeLastRunText.Text = snapshot.LastRunSummary;

        if (_FindingsTab != null)
        {
            FindingsOpenCountText.Text = (snapshot.FindingsCriticalCount + snapshot.FindingsHighCount + snapshot.FindingsMediumCount + snapshot.FindingsLowCount)
                .ToString(CultureInfo.InvariantCulture);
            FindingsHighCountText.Text = (snapshot.FindingsCriticalCount + snapshot.FindingsHighCount).ToString(CultureInfo.InvariantCulture);
            FindingsCriticalCountText.Text = snapshot.FindingsCriticalCount.ToString(CultureInfo.InvariantCulture);
        }

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

}
