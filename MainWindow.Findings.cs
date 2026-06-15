// MainWindow partial class - Findings functionality
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
    internal void FindingsSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFindingsFilter();

    private void RebuildFindings()
    {
        allFindings.Clear();
        foreach (TestResult result in allResults)
        {
            if (IsNonActionableResult(result.Result))
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
        // Manual dedup + sort: avoids LINQ GroupBy/Select/OrderByDescending/ThenBy/ToList
        // allocations on every findings rebuild.
        Dictionary<string, AdHealthFinding> dedup = new(allFindings.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < allFindings.Count; i++)
        {
            string key = BuildFindingKey(allFindings[i]);
            dedup.TryAdd(key, allFindings[i]);
        }

        List<AdHealthFinding> deduplicatedFindings = new(dedup.Values);
        deduplicatedFindings.Sort((a, b) =>
        {
            int cmp = SeverityRank(b.Severity).CompareTo(SeverityRank(a.Severity)); // descending
            if (cmp != 0) return cmp;
            cmp = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
            cmp = string.Compare(a.Target, b.Target, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
            return string.Compare(a.Summary, b.Summary, StringComparison.OrdinalIgnoreCase);
        });
        allFindings.Clear();
        allFindings.AddRange(deduplicatedFindings);
        SyncFindingItems();
    }

    internal static string BuildFindingKey(AdHealthFinding finding)
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

    /// <summary>
    /// Returns true when the severity represents an actionable finding
    /// (i.e. not informational/passing). Used by dashboard snapshot,
    /// summary text, and active-finding filters to keep the "Info"
    /// exclusion in one place.
    /// </summary>
    internal static bool IsActiveSeverity(string severity)
    {
        // Treat null or empty severity as inactive (equivalent to "Info").
        if (string.IsNullOrEmpty(severity))
            return false;
        return !severity.Equals("Info", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsNonActionableResult(string result)
    {
        return result.Equals("PASS", StringComparison.OrdinalIgnoreCase) ||
               result.Equals("INFO", StringComparison.OrdinalIgnoreCase) ||
               result.Equals("WARN", StringComparison.OrdinalIgnoreCase) ||
               result.Equals("WARNING", StringComparison.OrdinalIgnoreCase);
    }

    private List<AdHealthFinding> GetActiveFindings()
    {
        // Manual filter: avoids LINQ Where allocation
        List<AdHealthFinding> active = new();
        for (int i = 0; i < allFindings.Count; i++)
        {
            if (IsActiveSeverity(allFindings[i].Severity))
                active.Add(allFindings[i]);
        }
        return active;
    }

    internal static string InferCategory(string service)
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

    internal static string InferSeverity(TestResult result)
    {
        if (IsNonActionableResult(result.Result))
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

    internal static string BuildFindingSummary(TestResult result)
    {
        if (IsNonActionableResult(result.Result))
        {
            return result.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase)
                ? $"{result.Service} passed on {result.Server}."
                : $"{result.Service} reported information for {result.Server}.";
        }

        return $"{result.Service} failed on {result.Server}.";
    }

    internal static string SuggestRemediation(TestResult result)
    {
        if (IsNonActionableResult(result.Result))
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

    private void UpdateSecurityGrid()
    {
        if (dgSecurityFindings == null)
        {
            return;
        }

        // Manual filter + sort + count: avoids LINQ Where/OrderByDescending/ToList/Count allocations
        List<AdHealthFinding> securityFindings = new();
        int critical = 0, high = 0;
        for (int i = 0; i < allFindings.Count; i++)
        {
            AdHealthFinding f = allFindings[i];
            if (!IsSecurityFinding(f))
                continue;
            securityFindings.Add(f);
            if (f.Severity == "Critical") critical++;
            else if (f.Severity == "High") high++;
        }
        securityFindings.Sort((a, b) => SeverityRank(b.Severity).CompareTo(SeverityRank(a.Severity)));
        ReplaceCollection(securityFindingItems, securityFindings);

        // Manual sum: avoids LINQ .Values.Sum()
        int totalPrivGroups = 0;
        if (latestInventory?.PrivilegedGroupCounts != null)
        {
            foreach (int v in latestInventory.PrivilegedGroupCounts.Values)
                totalPrivGroups += v;
        }

        SecurityTotalFindingsText.Text = securityFindings.Count.ToString(CultureInfo.InvariantCulture);
        SecurityCriticalText.Text = critical.ToString(CultureInfo.InvariantCulture);
        SecurityHighText.Text = high.ToString(CultureInfo.InvariantCulture);
        SecurityPrivGroupCountText.Text = totalPrivGroups.ToString(CultureInfo.InvariantCulture);
    }

    private string BuildPrivilegeInsightSummary()
    {
        if (latestInventory.PrivilegedGroupCounts.Count == 0)
        {
            return "Privilege analysis will appear after a collection runs.";
        }

        // Manual filter + sort + take: avoids LINQ Where/OrderByDescending/Take/Select allocations
        List<KeyValuePair<string, int>> positiveGroups = new(latestInventory.PrivilegedGroupCounts.Count);
        foreach (var pair in latestInventory.PrivilegedGroupCounts)
        {
            if (pair.Value >= 0)
                positiveGroups.Add(pair);
        }
        positiveGroups.Sort((a, b) => b.Value.CompareTo(a.Value));

        int take = Math.Min(3, positiveGroups.Count);
        string[] highlights = new string[take];
        for (int i = 0; i < take; i++)
            highlights[i] = $"{positiveGroups[i].Key}: {positiveGroups[i].Value}";

        return "Top privileged groups by member count: " + string.Join(" | ", highlights);
    }

    internal static bool IsSecurityFinding(AdHealthFinding finding)
    {
        return finding.Category.Equals("Privilege", StringComparison.OrdinalIgnoreCase) ||
               finding.Category.Equals("Telemetry", StringComparison.OrdinalIgnoreCase) ||
               finding.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase) ||
               finding.Severity.Equals("High", StringComparison.OrdinalIgnoreCase);
    }

    internal static int SeverityRank(string severity)
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
        if (dgFindings?.SelectedItem is not AdHealthFinding finding)
        {
            NotificationService.Show(this, "Open Related Log", "Please select a finding first.");
            return;
        }

        if (string.IsNullOrWhiteSpace(finding.LogFilePath))
        {
            NotificationService.Show(this, "Log Not Available", "The selected finding does not have a log file path recorded. This can happen when viewing results loaded from a previous session. Try running the tests again to generate fresh logs.");
            return;
        }

        if (!File.Exists(finding.LogFilePath))
        {
            NotificationService.Show(this, "Log File Missing", "The log file for this finding no longer exists on disk. Log files are automatically cleaned up after 14 days or when more than 100 runs accumulate. Try running the tests again to generate fresh logs.");
            return;
        }

        ShowLogFileInLogsTab(finding.LogFilePath);
    }

}
