// MainWindow partial class - Export functionality
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
    internal async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (allResults.Count == 0)
        {
            new SuccessNotification("No Results", "No test results available to export.", isError: true) { Owner = this }.ShowDialog();
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
            new SuccessNotification("Export Failed", $"Failed to export:\n{ex.Message}", isError: true) { Owner = this }.ShowDialog();
        }
    }

    internal async void ExecutiveSummary_Click(object sender, RoutedEventArgs e)
    {
        if (allResults.Count == 0)
        {
            new SuccessNotification("No Results", "No test results available. Run tests first.", isError: true) { Owner = this }.ShowDialog();
            return;
        }

        int total = allResults.Count;
        int passed = 0, failed = 0;
        for (int i = 0; i < total; i++)
        {
            if (allResults[i].Result.Equals("PASS", StringComparison.OrdinalIgnoreCase)) passed++;
            else if (allResults[i].Result.Equals("FAIL", StringComparison.OrdinalIgnoreCase)) failed++;
        }
        int passRate = total > 0 ? (int)((double)passed / total * 100) : 0;
        int healthScore = CalculateHealthScore();
        string scoreColor = healthScore >= 80 ? "#2E7D32" : healthScore >= 50 ? "#F57F17" : "#C62828";

        // Manual filter: avoids LINQ Where/ToList allocations
        List<TestResult> failures = new();
        for (int i = 0; i < allResults.Count; i++)
        {
            if (allResults[i].Result.Equals("FAIL", StringComparison.OrdinalIgnoreCase))
                failures.Add(allResults[i]);
        }
        List<AdHealthFinding> findings = new();
        for (int i = 0; i < allFindings.Count; i++)
        {
            string sev = allFindings[i].Severity;
            if (sev == "Critical" || sev == "High")
                findings.Add(allFindings[i]);
        }

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
            new SuccessNotification("Summary Failed", $"Failed to build executive summary:\n{ex.Message}", isError: true) { Owner = this }.ShowDialog();
            return;
        }

        try { Process.Start(new ProcessStartInfo(sfd.FileName) { UseShellExecute = true }); }            catch { new SuccessNotification("Summary Saved", $"Summary saved to:\n{sfd.FileName}") { Owner = this }.ShowDialog(); }
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
        int passed = 0, failed = 0;
        foreach (TestResult r in results)
        {
            if (r.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase)) passed++;
            else if (r.Result.Equals("FAIL", StringComparison.OrdinalIgnoreCase)) failed++;
        }

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
        // Manual filter: avoids LINQ Where/ToList allocations
        List<TestResult> failures = new();
        foreach (TestResult r in allResultSnapshot)
        {
            if (r.Result.Equals("FAIL", StringComparison.OrdinalIgnoreCase))
                failures.Add(r);
        }
        List<AdHealthFinding> findings = new();
        foreach (AdHealthFinding f in allFindingSnapshot)
        {
            string sev = f.Severity;
            if (sev == "Critical" || sev == "High")
                findings.Add(f);
        }

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

}
