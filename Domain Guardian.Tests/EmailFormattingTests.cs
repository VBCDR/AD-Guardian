using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AdHealthMonitor;
using Xunit;

namespace Domain_Guardian.Tests;

/// <summary>
/// Tests for FormatTestResultTable — the HTML email result table with DC grouping.
/// This method is private, so tests invoke it via reflection.
/// </summary>
public class EmailFormattingTests
{
    private static string FormatTestResultTable(List<TestResult> results, string[] dcList, string passColor, string failColor)
    {
        var method = typeof(MainWindow).GetMethod("FormatTestResultTable",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, [results, dcList, passColor, failColor])!;
    }

    [Fact]
    public void FormatTestResultTable_SinglePassingTest_RendersCorrectly()
    {
        var results = new List<TestResult>
        {
            new() { Service = "Connectivity", Server = "DC01", Result = "PASS", Message = "passed" }
        };
        string[] dcList = { "DC01" };

        string html = FormatTestResultTable(results, dcList, "#2E7D32", "#C62828");

        Assert.Contains("Connectivity", html);
        Assert.Contains("Pass", html);
        Assert.DoesNotContain("Fail", html);
        Assert.Contains("<table", html);
        Assert.Contains("</table>", html);
    }

    [Fact]
    public void FormatTestResultTable_SingleFailingTest_RendersFailRow()
    {
        var results = new List<TestResult>
        {
            new() { Service = "DFSREvent", Server = "DC01", Result = "FAIL", Message = "DFS Replication Service failed" }
        };
        string[] dcList = { "DC01" };

        string html = FormatTestResultTable(results, dcList, "#2E7D32", "#C62828");

        Assert.Contains("DFSREvent", html);
        Assert.Contains("Fail", html);
        Assert.Contains("#fef2f2", html); // fail row background
    }

    [Fact]
    public void FormatTestResultTable_MultipleTests_AllAppearInTable()
    {
        var results = new List<TestResult>
        {
            new() { Service = "Connectivity", Server = "DC01", Result = "PASS", Message = "passed" },
            new() { Service = "Advertising", Server = "DC01", Result = "PASS", Message = "passed" },
            new() { Service = "DFSREvent", Server = "DC01", Result = "FAIL", Message = "DFS Replication Service failed" },
            new() { Service = "Replications", Server = "DC01", Result = "FAIL", Message = "Replication latency" },
            new() { Service = "Connectivity", Server = "DC02", Result = "PASS", Message = "passed" },
            new() { Service = "DFSREvent", Server = "DC02", Result = "FAIL", Message = "DFS failed on DC02" },
        };
        string[] dcList = { "DC01", "DC02" };

        string html = FormatTestResultTable(results, dcList, "#2E7D32", "#C62828");

        Assert.Contains("Connectivity", html);
        Assert.Contains("DFSREvent", html);
        Assert.Contains("Replications", html);
        Assert.Contains("DC02", html);
    }

    [Fact]
    public void FormatTestResultTable_MultiDC_ShowsDCGroupHeaders()
    {
        var results = new List<TestResult>
        {
            new() { Service = "Connectivity", Server = "DC01", Result = "PASS", Message = "passed" },
            new() { Service = "Connectivity", Server = "DC02", Result = "FAIL", Message = "failed" },
        };
        string[] dcList = { "DC01", "DC02" };

        string html = FormatTestResultTable(results, dcList, "#2E7D32", "#C62828");

        // When >1 DC, group headers should appear
        Assert.Contains("DC:", html);
        // Both DCs should appear
        Assert.Contains("DC01", html);
        Assert.Contains("DC02", html);
    }

    [Fact]
    public void FormatTestResultTable_SingleDC_NoGroupHeaders()
    {
        var results = new List<TestResult>
        {
            new() { Service = "Connectivity", Server = "DC01", Result = "PASS", Message = "passed" },
            new() { Service = "Advertising", Server = "DC01", Result = "PASS", Message = "passed" },
        };
        string[] dcList = { "DC01" };

        string html = FormatTestResultTable(results, dcList, "#2E7D32", "#C62828");

        // Only one DC group — no "DC:" group header row
        Assert.DoesNotContain("DC:", html);
    }

    [Fact]
    public void FormatTestResultTable_NonMatchingServerNames_StillShowsResults()
    {
        // When parsed server names (e.g. "DC01.corp.local") differ from user-entered
        // short names (e.g. "DC01"), all results should still appear in the table.
        var results = new List<TestResult>
        {
            new() { Service = "Connectivity", Server = "DC01.corp.local", Result = "PASS", Message = "passed" },
            new() { Service = "Connectivity", Server = "DC02.corp.local", Result = "FAIL", Message = "RPC error" },
        };
        string[] dcList = { "DC01", "DC02" }; // short names

        string html = FormatTestResultTable(results, dcList, "#2E7D32", "#C62828");

        // Both results must appear despite name mismatch
        Assert.Contains("DC01.corp.local", html);
        Assert.Contains("DC02.corp.local", html);
        Assert.Contains("RPC error", html);
    }

    [Fact]
    public void FormatTestResultTable_NullServer_UsesFirstDcFromList()
    {
        var results = new List<TestResult>
        {
            new() { Service = "DCDiag", Server = null, Result = "FAIL", Message = "DC unreachable" },
        };
        string[] dcList = { "DC01", "DC02" };

        string html = FormatTestResultTable(results, dcList, "#2E7D32", "#C62828");

        Assert.Contains("DCDiag", html);
        Assert.Contains("Fail", html);
        Assert.Contains("DC unreachable", html);
    }

    [Fact]
    public void FormatTestResultTable_LongMessage_TruncatedTo80Chars()
    {
        string longMessage = new string('x', 150);
        var results = new List<TestResult>
        {
            new() { Service = "Connectivity", Server = "DC01", Result = "FAIL", Message = longMessage }
        };
        string[] dcList = { "DC01" };

        string html = FormatTestResultTable(results, dcList, "#2E7D32", "#C62828");

        // Message should be truncated to 80 chars + ellipsis character.
        // Verify the full 150-char string is absent, but the 80-char prefix is present.
        Assert.DoesNotContain(longMessage, html);
        Assert.Contains("…", html); // ellipsis
        Assert.Contains(longMessage[..80], html); // first 80 chars intact
    }

    [Fact]
    public void FormatTestResultTable_HtmlEncodesServerNames()
    {
        // Use two servers so the DC group header renders (single-server groups suppress it)
        var results = new List<TestResult>
        {
            new() { Service = "Connectivity", Server = "DC01<script>", Result = "PASS", Message = "ok" },
            new() { Service = "Connectivity", Server = "DC02", Result = "PASS", Message = "ok" },
        };
        string[] dcList = { "DC01", "DC02" };

        string html = FormatTestResultTable(results, dcList, "#2E7D32", "#C62828");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html); // properly encoded
    }

    [Fact]
    public void FormatTestResultTable_IncludesAttachmentReference()
    {
        var results = new List<TestResult>
        {
            new() { Service = "Connectivity", Server = "DC01", Result = "PASS", Message = "passed" }
        };
        string[] dcList = { "DC01" };

        string html = FormatTestResultTable(results, dcList, "#2E7D32", "#C62828");

        Assert.Contains("ResultsSummary.txt", html);
    }

    [Fact]
    public void FormatTestResultTable_EmptyResults_ReturnsMinimalTable()
    {
        var results = new List<TestResult>();
        string[] dcList = { "DC01" };

        string html = FormatTestResultTable(results, dcList, "#2E7D32", "#C62828");

        Assert.Contains("<table", html);
        Assert.Contains("th", html); // header row present
        Assert.Contains("</table>", html);
    }

    [Fact]
    public void FormatTestResultTable_UserListedDcsAppearFirst()
    {
        // DC03 is in user's dcList ("DC01") but parsed as "DC03". It should
        // appear after DC01 (which matches the user list) in the sort order.
        var results = new List<TestResult>
        {
            new() { Service = "Connectivity", Server = "DC03", Result = "PASS", Message = "passed on DC03" },
            new() { Service = "Connectivity", Server = "DC01", Result = "FAIL", Message = "failed on DC01" },
        };
        string[] dcList = { "DC01" }; // DC01 is user-listed, DC03 is not

        string html = FormatTestResultTable(results, dcList, "#2E7D32", "#C62828");

        // DC01 should come before DC03 in the sorted output
        int dc01Pos = html.IndexOf("DC01", StringComparison.Ordinal);
        int dc03Pos = html.IndexOf("DC03", StringComparison.Ordinal);
        Assert.True(dc01Pos < dc03Pos, "User-listed DC (DC01) should appear before non-listed DC (DC03)");
    }
}
