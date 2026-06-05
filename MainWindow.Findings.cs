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

    private IEnumerable<AdHealthFinding> GetActiveFindings()
    {
        return allFindings.Where(f => !f.Severity.Equals("Info", StringComparison.OrdinalIgnoreCase));
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

    internal static string BuildFindingSummary(TestResult result)
    {
        if (result.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase))
        {
            return $"{result.Service} passed on {result.Server}.";
        }

        return $"{result.Service} failed on {result.Server}.";
    }

    internal static string SuggestRemediation(TestResult result)
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
        if (dgFindings?.SelectedItem is not AdHealthFinding finding ||
            string.IsNullOrWhiteSpace(finding.LogFilePath) ||
            !File.Exists(finding.LogFilePath))
        {
            NotificationService.Show(this, "Open Related Log", "The selected finding does not have an associated log file.");
            return;
        }

        ShowLogFileInLogsTab(finding.LogFilePath);
    }

}