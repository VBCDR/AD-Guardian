// MainWindow partial class - Dashboard functionality
// Extracted from MainWindow.xaml.cs during partial class refactoring.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Domain_Guardian;
using Newtonsoft.Json;

namespace AdHealthMonitor;

public partial class MainWindow
{
    private string? _dashboardHash;
    private StringBuilder? _hashBuilder;
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
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to persist dashboard snapshot: {ex}");
        }
    }

    private void RefreshDashboardCore()
    {
        if (!IsLoaded) return;
        // Pre-compute counts once to avoid repeated LINQ scans over the same collections.
        int passCount = 0, failCount = 0;
        for (int i = 0; i < allResults.Count; i++)
        {
            if (allResults[i].Result.Equals("PASS", StringComparison.OrdinalIgnoreCase)) passCount++;
            else if (allResults[i].Result.Equals("FAIL", StringComparison.OrdinalIgnoreCase)) failCount++;
        }

        int critCount = 0, highCount = 0, medCount = 0, lowCount = 0, securityFindingCount = 0;
        for (int i = 0; i < allFindings.Count; i++)
        {
            string sev = allFindings[i].Severity;
            if (sev.Equals("Critical", StringComparison.OrdinalIgnoreCase)) critCount++;
            else if (sev.Equals("High", StringComparison.OrdinalIgnoreCase)) highCount++;
            else if (sev.Equals("Medium", StringComparison.OrdinalIgnoreCase)) medCount++;
            else if (sev.Equals("Low", StringComparison.OrdinalIgnoreCase)) lowCount++;

            // Security finding count: matches IsSecurityFinding (Privilege/Telemetry category OR Critical/High severity)
            if (sev.Equals("Critical", StringComparison.OrdinalIgnoreCase) ||
                sev.Equals("High", StringComparison.OrdinalIgnoreCase) ||
                allFindings[i].Category.Equals("Privilege", StringComparison.OrdinalIgnoreCase) ||
                allFindings[i].Category.Equals("Telemetry", StringComparison.OrdinalIgnoreCase))
                securityFindingCount++;
        }

        // Build hash with StringBuilder to avoid string.Join boxing on every 120ms tick.
        _hashBuilder ??= new(128);
        _hashBuilder.Clear();
        StringBuilder hashBuilder = _hashBuilder;
        hashBuilder.Append(allResults.Count).Append('|')
            .Append(passCount).Append('|').Append(failCount).Append('|')
            .Append(allFindings.Count).Append('|').Append(critCount).Append('|').Append(highCount).Append('|').Append(medCount).Append('|')
            .Append(historyEntries.Count).Append('|')
            .Append(historyEntries.Count > 0 ? historyEntries[0].RunDate.Ticks : 0).Append('|')
            .Append(latestInventory.ForestName ?? "").Append('|')
            .Append(latestInventory.DomainControllerCount).Append('|')
            .Append(latestTelemetry.TotalServices).Append('|')
            .Append(securityFindingCount);
        string newHash = hashBuilder.ToString();
        if (string.Equals(_dashboardHash, newHash, StringComparison.Ordinal)) return;
        _dashboardHash = newHash;

        if (!HasLiveDashboardData() && cachedDashboardSnapshot != null)
        {
            ApplyCachedDashboardSnapshot(cachedDashboardSnapshot);
            return;
        }

        int configuredControllers = CountConfiguredDomainControllers();
        int passingTests = passCount;
        int criticalFindings = critCount;
        int healthScore = CalculateHealthScore(critCount, highCount, medCount);
        int highOrAboveFindings = critCount + highCount;

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
                ? $"Security view includes {securityFindingCount} security-oriented finding(s) across privilege and telemetry signals."
                : "Security findings are derived from privileged group breadth, failing directory tests, and service telemetry.";
        }
        if (_FindingsTab != null)
        {
            FindingsOpenCountText.Text = (critCount + highCount + medCount + lowCount).ToString(CultureInfo.InvariantCulture);
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
        RefreshHomeFindingsSummary(critCount, highCount, medCount, lowCount);
        if (MainTabControl.SelectedIndex == 0)
        {
            RefreshHomeRunHistoryBars();
            RefreshHomeTrendPolyline();
        }
    }

    private string? _cachedDomainControllersInput;
    private int _cachedDomainControllerCount;

    private int CountConfiguredDomainControllers()
    {
        // Cache since domainControllers string rarely changes between refreshes.
        if (string.Equals(_cachedDomainControllersInput, domainControllers, StringComparison.Ordinal))
        {
            return _cachedDomainControllerCount;
        }

        int count = 0;
        foreach (string part in domainControllers.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!string.IsNullOrWhiteSpace(part)) count++;
        }

        _cachedDomainControllersInput = domainControllers;
        _cachedDomainControllerCount = count;
        return count;
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
        // historyEntries is already sorted descending; take 6 newest then reverse for left-to-right display.
        int takeCount = Math.Min(6, historyEntries.Count);
        if (takeCount == 0) return;
        double maxCount = 1;
        for (int i = 0; i < takeCount; i++)
        {
            maxCount = Math.Max(maxCount, Math.Max(historyEntries[i].Passed, historyEntries[i].Failed));
        }
        double barMaxHeight = 90;

        // Iterate in reverse (oldest→newest) for left-to-right bar display.
        for (int i = takeCount - 1; i >= 0; i--)
        {
            var entry = historyEntries[i];
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
                    Background = FailBrushCached,
                    CornerRadius = new CornerRadius(3, 3, 0, 0),
                    Margin = new Thickness(0, 1, 0, 0)
                });
            }
            if (entry.Passed > 0)
            {
                stack.Children.Add(new Border
                {
                    Height = Math.Max(2, passHeight),
                    Background = PassBrushCached,
                    CornerRadius = new CornerRadius(3, 3, 0, 0),
                    Margin = new Thickness(0, 1, 0, 0)
                });
            }
            if (entry.Passed == 0 && entry.Failed == 0)
            {
                stack.Children.Add(new Border
                {
                    Height = 2,
                    Background = NeutralBrushCached,
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
        // historyEntries is already sorted descending; take 10 newest and iterate in reverse for left-to-right.
        int takeCount = Math.Min(10, historyEntries.Count);
        if (takeCount < 1) return;

        PointCollection points = new();
        double w = 120;
        double h = 60;
        for (int i = 0; i < takeCount; i++)
        {
            int idx = takeCount - 1 - i; // reverse: oldest at i=0, newest at i=takeCount-1
            double x = i * w / Math.Max(1, takeCount - 1);
            double rate = historyEntries[idx].Total > 0
                ? (double)historyEntries[idx].Passed / historyEntries[idx].Total
                : 0.5;
            double y = h - (rate * h * 0.8 + h * 0.1);
            points.Add(new Point(x, y));
        }
        HomeTrendPolyline.Points = points;
    }

    private void RefreshHomeFindingsSummary(int crit, int high, int med, int low)
    {
        HomeFindingsSummary.Children.Clear();

        void AddRow(string label, int count, Brush color)
        {
            Border row = new()
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(0, 0, 0, 6),
                BorderBrush = SeparatorBrushCached,
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            Grid g = new();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock labelTb = new()
            {
                Text = label,
                FontSize = 13,
                Foreground = BodyTextBrushCached,
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
                Foreground = BodyTextBrushCached
            });
            return;
        }

        AddRow("Critical", crit, FailBrushCached);
        AddRow("High", high, HighSeverityBrushCached);
        AddRow("Medium", med, MediumSeverityBrushCached);
        AddRow("Low", low, LowSeverityBrushCached);
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
        // Use for-loops instead of LINQ to avoid allocations on every dashboard persist.
        int passingTests = 0;
        for (int i = 0; i < allResults.Count; i++)
        {
            if (allResults[i].Result.Equals("PASS", StringComparison.OrdinalIgnoreCase)) passingTests++;
        }

        int crit = 0, high = 0, med = 0, low = 0;
        for (int i = 0; i < allFindings.Count; i++)
        {
            string sev = allFindings[i].Severity;
            if (!IsActiveSeverity(sev)) continue;
            if (sev.Equals("Critical", StringComparison.OrdinalIgnoreCase)) crit++;
            else if (sev.Equals("High", StringComparison.OrdinalIgnoreCase)) high++;
            else if (sev.Equals("Medium", StringComparison.OrdinalIgnoreCase)) med++;
            else if (sev.Equals("Low", StringComparison.OrdinalIgnoreCase)) low++;
        }

        TestHistoryEntry? latestRun = historyEntries.Count > 0 ? historyEntries[0] : null;

        return new DashboardSnapshot
        {
            CapturedAtUtc = DateTime.UtcNow,
            HealthScore = CalculateHealthScore(crit, high, med),
            CriticalFindings = crit,
            PassingTests = passingTests,
            ConfiguredDomainControllers = CountConfiguredDomainControllers(),
            TotalRuns = historyEntries.Count,
            LastRunSummary = historyEntries.Count > 0 ? BuildLastRunSummary() : "No runs yet",
            FindingsCriticalCount = crit,
            FindingsHighCount = high,
            FindingsMediumCount = med,
            FindingsLowCount = low,
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
                BorderBrush = SeparatorBrushCached,
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            Grid g = new();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock labelTb = new()
            {
                Text = label,
                FontSize = 13,
                Foreground = BodyTextBrushCached,
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
                Foreground = BodyTextBrushCached
            });
            return;
        }

        AddRow("Critical", crit, FailBrushCached);
        AddRow("High", high, HighSeverityBrushCached);
        AddRow("Medium", med, MediumSeverityBrushCached);
        AddRow("Low", low, LowSeverityBrushCached);
    }

    /// <summary>
    /// Convenience overload that computes severity counts from allFindings.
    /// Prefer the parameterized overload when counts are already available.
    /// </summary>
    private int CalculateHealthScore()
    {
        int crit = 0, high = 0, med = 0;
        for (int i = 0; i < allFindings.Count; i++)
        {
            string sev = allFindings[i].Severity;
            if (sev.Equals("Critical", StringComparison.OrdinalIgnoreCase)) crit++;
            else if (sev.Equals("High", StringComparison.OrdinalIgnoreCase)) high++;
            else if (sev.Equals("Medium", StringComparison.OrdinalIgnoreCase)) med++;
        }
        return CalculateHealthScore(crit, high, med);
    }

    /// <summary>
    /// Calculates the health score using pre-computed severity counts.
    /// Avoids redundant LINQ scans when counts are already computed by the caller.
    /// </summary>
    private int CalculateHealthScore(int critical, int high, int medium)
    {
        double currentPassRate = 100;
        TestHistoryEntry? latestRun = historyEntries.Count > 0 ? historyEntries[0] : null;
        if (latestRun != null && latestRun.Total > 0)
        {
            currentPassRate = (double)latestRun.Passed / latestRun.Total * 100;
        }

        double trendAvg = 100;
        int trendCount = Math.Min(5, historyEntries.Count);
        double trendSum = 0;
        int trendDiv = 0;
        for (int i = 0; i < trendCount; i++)
        {
            if (historyEntries[i].Total > 0)
            {
                trendSum += (double)historyEntries[i].Passed / historyEntries[i].Total * 100;
                trendDiv++;
            }
        }
        if (trendDiv > 0)
        {
            trendAvg = trendSum / trendDiv;
        }

        double findingsPenalty = Math.Min(30, critical * 10 + high * 5 + medium * 2);
        double findingsRatio = (100 - findingsPenalty) / 100;

        double score = (currentPassRate * 0.6 + trendAvg * 0.4) * findingsRatio;
        return Math.Max(0, Math.Min(100, (int)Math.Round(score)));
    }

}
