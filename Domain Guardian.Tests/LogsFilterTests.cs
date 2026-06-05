using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using AdHealthMonitor;
using Xunit;

namespace Domain_Guardian.Tests;

/// <summary>
/// Tests for the Logs tab search and filter functionality:
/// BuildLogLines (color-coded log rendering), ParseLogSections (log section extraction),
/// FindLogMatchIndex (jump-to-entry navigation), and FilterLogSections (search/filter behavior).
/// </summary>
public class LogsFilterTests
{
    // ── BuildLogLines tests ──────────────────────────────────────────────

    [Fact]
    public void BuildLogLines_EmptyString_ReturnsEmpty()
    {
        List<LogLine> lines = MainWindow.BuildLogLines(string.Empty);
        Assert.Empty(lines);
    }

    [Fact]
    public void BuildLogLines_WhitespaceOnly_ReturnsEmpty()
    {
        List<LogLine> lines = MainWindow.BuildLogLines("   \n\n   \n  ");
        Assert.Empty(lines);
    }

    [Fact]
    public void BuildLogLines_Null_ReturnsEmpty()
    {
        List<LogLine> lines = MainWindow.BuildLogLines(null!);
        Assert.Empty(lines);
    }

    [Fact]
    public void BuildLogLines_NormalText_ReturnsLinesWithNormalFormatting()
    {
        string text = "Starting test: Connectivity\nSome output line\nAnother normal line\n";
        List<LogLine> lines = MainWindow.BuildLogLines(text);

        Assert.Equal(3, lines.Count);
        Assert.All(lines, line =>
        {
            Assert.Equal(FontWeights.Normal, line.FontWeight);
            Assert.NotNull(line.Foreground);
        });
    }

    [Fact]
    public void BuildLogLines_FailLine_HasBoldRedFormatting()
    {
        string text = "......................... DC01 failed test DFSREvent\n";
        List<LogLine> lines = MainWindow.BuildLogLines(text);

        Assert.Single(lines);
        Assert.Equal(FontWeights.SemiBold, lines[0].FontWeight);
        // LogFailBrush is Color.FromRgb(211, 47, 47) - a red color
        Assert.IsType<SolidColorBrush>(lines[0].Foreground);
        var brush = (SolidColorBrush)lines[0].Foreground;
        Assert.Equal(211, brush.Color.R);
        Assert.Equal(47, brush.Color.G);
        Assert.Equal(47, brush.Color.B);
    }

    [Fact]
    public void BuildLogLines_PassLine_HasBoldGreenFormatting()
    {
        string text = "......................... DC01 passed test Connectivity\n";
        List<LogLine> lines = MainWindow.BuildLogLines(text);

        Assert.Single(lines);
        Assert.Equal(FontWeights.SemiBold, lines[0].FontWeight);
        // LogPassBrush is Color.FromRgb(46, 125, 50) - a green color
        Assert.IsType<SolidColorBrush>(lines[0].Foreground);
        var brush = (SolidColorBrush)lines[0].Foreground;
        Assert.Equal(46, brush.Color.R);
        Assert.Equal(125, brush.Color.G);
        Assert.Equal(50, brush.Color.B);
    }

    [Fact]
    public void BuildLogLines_MixedContent_ColorsLinesCorrectly()
    {
        string text =
            "Starting test: Connectivity\n" +
            "......................... DC01 passed test Connectivity\n" +
            "Starting test: DFSREvent\n" +
            "The DFS Replication Service failed to register.\n" +
            "......................... DC01 failed test DFSREvent\n";

        List<LogLine> lines = MainWindow.BuildLogLines(text);

        Assert.Equal(5, lines.Count);

        // Lines 0, 2: normal text (Starting test)
        Assert.Equal(FontWeights.Normal, lines[0].FontWeight);
        Assert.Equal(FontWeights.Normal, lines[2].FontWeight);

        // Line 1: pass (green, bold)
        Assert.Equal(FontWeights.SemiBold, lines[1].FontWeight);
        var passBrush = (SolidColorBrush)lines[1].Foreground;
        Assert.Equal(46, passBrush.Color.R);

        // Lines 3, 4: fail (red, bold) - both contain "failed"
        Assert.Equal(FontWeights.SemiBold, lines[3].FontWeight);
        Assert.Equal(FontWeights.SemiBold, lines[4].FontWeight);
        var failBrush = (SolidColorBrush)lines[3].Foreground;
        Assert.Equal(211, failBrush.Color.R);
    }

    [Fact]
    public void BuildLogLines_CaseInsensitive_CapturesFailPassKeywords()
    {
        string text =
            "A FAILURE was detected\n" +
            "This test was PASSING\n";

        List<LogLine> lines = MainWindow.BuildLogLines(text);

        Assert.Equal(2, lines.Count);
        Assert.Equal(FontWeights.SemiBold, lines[0].FontWeight); // "FAIL" in FAILURE
        Assert.Equal(FontWeights.SemiBold, lines[1].FontWeight); // "PASS" in PASSING
    }

    [Fact]
    public void BuildLogLines_PreservesLineText()
    {
        string text = "Command: dcdiag /s:DC01 /c /v\n   Testing server: Site\\DC01\n";
        List<LogLine> lines = MainWindow.BuildLogLines(text);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Command: dcdiag /s:DC01 /c /v", lines[0].Text);
        Assert.Equal("   Testing server: Site\\DC01", lines[1].Text);
    }

    // ── ParseLogSections tests ───────────────────────────────────────────

    [Fact]
    public void ParseLogSections_EmptyText_ReturnsEmpty()
    {
        var sections = MainWindow.ParseLogSections(string.Empty);
        Assert.Empty(sections);
    }

    [Fact]
    public void ParseLogSections_SingleTest_ParsesCorrectly()
    {
        string logText = string.Join("\n", new[]
        {
            "Command: dcdiag /s:DC01 /c /v",
            "Testing server: Default-First-Site-Name\\DC01",
            "   Starting test: Connectivity",
            "      ......................... DC01 passed test Connectivity"
        });

        var sections = MainWindow.ParseLogSections(logText);

        Assert.Single(sections);
        Assert.Equal("Connectivity", sections[0].Service);
        Assert.Equal("PASS", sections[0].Result);
        Assert.Equal("DC01", sections[0].Server);
        Assert.True(sections[0].Lines.Count >= 2, "Section should include 'Starting test:' line and result line");
    }

    [Fact]
    public void ParseLogSections_MultipleTests_ParsesAllSections()
    {
        string logText = string.Join("\n", new[]
        {
            "Testing server: Site\\DC01",
            "   Starting test: Connectivity",
            "      ......................... DC01 passed test Connectivity",
            "   Starting test: Advertising",
            "      ......................... DC01 passed test Advertising",
            "   Starting test: DFSREvent",
            "      The DFS Replication Service failed.",
            "      ......................... DC01 failed test DFSREvent"
        });

        var sections = MainWindow.ParseLogSections(logText);

        Assert.Equal(3, sections.Count);
        Assert.Equal("Connectivity", sections[0].Service);
        Assert.Equal("Advertising", sections[1].Service);
        Assert.Equal("DFSREvent", sections[2].Service);

        Assert.Equal("PASS", sections[0].Result);
        Assert.Equal("PASS", sections[1].Result);
        Assert.Equal("FAIL", sections[2].Result);
    }

    [Fact]
    public void ParseLogSections_MultipleServers_GroupsByServer()
    {
        string logText = string.Join("\n", new[]
        {
            "Testing server: Site\\DC01",
            "   Starting test: Connectivity",
            "      ......................... DC01 passed test Connectivity",
            "Testing server: Site\\DC02",
            "   Starting test: Connectivity",
            "      ......................... DC02 failed test Connectivity",
            "      The RPC server is unavailable."
        });

        var sections = MainWindow.ParseLogSections(logText);

        Assert.Equal(2, sections.Count);
        Assert.Equal("DC01", sections[0].Server);
        Assert.Equal("DC02", sections[1].Server);
        Assert.Equal("PASS", sections[0].Result);
        Assert.Equal("FAIL", sections[1].Result);
    }

    [Fact]
    public void ParseLogSections_ServerFromResultLine_InfersServer()
    {
        // If there's no "Testing server:" line, server should be inferred from the result line
        string logText = string.Join("\n", new[]
        {
            "   Starting test: Connectivity",
            "      ......................... 2022DC01 passed test Connectivity"
        });

        var sections = MainWindow.ParseLogSections(logText);

        Assert.Single(sections);
        Assert.Equal("Connectivity", sections[0].Service);
        Assert.Equal("2022DC01", sections[0].Server);
    }

    [Fact]
    public void ParseLogSections_ServerFromCommandLine_ParsesServer()
    {
        string logText = string.Join("\n", new[]
        {
            "Command: dcdiag /s:dc01.corp.local /c /v",
            "   Starting test: Connectivity",
            "      ......................... dc01 passed test Connectivity"
        });

        var sections = MainWindow.ParseLogSections(logText);

        Assert.Single(sections);
        Assert.Equal("dc01.corp.local", sections[0].Server);
    }

    [Fact]
    public void ParseLogSections_NoTests_ReturnsEmpty()
    {
        string logText = "Directory Server Diagnosis\nPerforming initial setup:\nDone gathering initial info.\n";
        var sections = MainWindow.ParseLogSections(logText);

        Assert.Empty(sections);
    }

    [Fact]
    public void ParseLogSections_SectionWithoutResult_HasEmptyResult()
    {
        // A section that starts but has no pass/fail line
        string logText = string.Join("\n", new[]
        {
            "   Starting test: Connectivity",
            "      Some output but no pass/fail line"
        });

        var sections = MainWindow.ParseLogSections(logText);

        Assert.Single(sections);
        Assert.Equal("Connectivity", sections[0].Service);
        Assert.Equal(string.Empty, sections[0].Result);
    }

    [Fact]
    public void ParseLogSections_PreservesAllLines()
    {
        string logText = string.Join("\n", new[]
        {
            "Testing server: Site\\DC01",
            "   Starting test: Replications",
            "      Checking DC=corp,DC=local",
            "      Replication latency is too high.",
            "      ......................... DC01 failed test Replications"
        });

        var sections = MainWindow.ParseLogSections(logText);

        Assert.Single(sections);
        Assert.Equal("Replications", sections[0].Service);
        // Should have: "Starting test:", "Checking DC=...", "Replication latency...", result line
        Assert.True(sections[0].Lines.Count >= 3, $"Expected at least 3 lines, got {sections[0].Lines.Count}");
        Assert.Contains(sections[0].Lines, l => l.Contains("Replication latency"));
    }

    [Fact]
    public void ParseLogSections_MultipleServers_ServerContextCarriesForward()
    {
        // Server set by "Testing server:" should carry forward to subsequent tests
        string logText = string.Join("\n", new[]
        {
            "Testing server: Site\\DC01",
            "   Starting test: Connectivity",
            "      ......................... DC01 passed test Connectivity",
            "   Starting test: Advertising",
            "      ......................... DC01 passed test Advertising"
        });

        var sections = MainWindow.ParseLogSections(logText);

        Assert.Equal(2, sections.Count);
        Assert.All(sections, s => Assert.Equal("DC01", s.Server));
    }

    [Fact]
    public void ParseLogSections_RepadminLines_AreIncludedInSectionLines()
    {
        // repadmin output that appears between tests should be captured
        string logText = string.Join("\n", new[]
        {
            "Testing server: Site\\DC01",
            "   Starting test: Replications",
            "      * Replications Check",
            "      DC=DomainDnsZones,DC=corp,DC=local has 1 neighbors",
            "      ......................... DC01 passed test Replications"
        });

        var sections = MainWindow.ParseLogSections(logText);

        Assert.Single(sections);
        Assert.Contains(sections[0].Lines, l => l.Contains("DomainDnsZones"));
    }

    // ── FindLogMatchIndex tests ──────────────────────────────────────────

    [Fact]
    public void FindLogMatchIndex_MatchesServiceName()
    {
        // Use explicit list construction to avoid CRLF issues from string concatenation
        var logLines = new List<LogLine>
        {
            new() { Text = "Starting test: Connectivity" },
            new() { Text = "......................... DC01 passed test Connectivity" },
            new() { Text = "Starting test: DFSREvent" },
            new() { Text = "......................... DC01 failed test DFSREvent" },
        };

        // Only set Service — no Server or Message so the match is purely by service name
        var entry = new TestResult { Service = "DFSREvent", Result = "FAIL" };
        int index = MainWindow.FindLogMatchIndex(logLines, entry);

        Assert.Equal(2, index); // Matches "Starting test: DFSREvent" at index 2
    }

    [Fact]
    public void FindLogMatchIndex_MatchesServerName()
    {
        var logLines = new List<LogLine>
        {
            new() { Text = "Testing server: Site\\DC01" },
            new() { Text = "Starting test: Connectivity" },
            new() { Text = "Testing server: Site\\DC02" },
            new() { Text = "Starting test: Connectivity" },
        };

        var entry = new TestResult { Service = "NonExistent", Server = "DC02", Result = "PASS" };
        int index = MainWindow.FindLogMatchIndex(logLines, entry);

        Assert.Equal(2, index); // Matches "Testing server: Site\\DC02"
    }

    [Fact]
    public void FindLogMatchIndex_MatchesMessage()
    {
        var logLines = new List<LogLine>
        {
            new() { Text = "Starting test: DFSREvent" },
            new() { Text = "The DFS Replication Service failed to register the WMI provider." },
            new() { Text = "......................... DC01 failed test DFSREvent" },
        };

        // Use Service/Server values not in the log lines so the match falls through to Message
        var entry = new TestResult
        {
            Service = "NonExistent",
            Server = "NonExistent",
            Result = "FAIL",
            Message = "DFS Replication Service"
        };
        int index = MainWindow.FindLogMatchIndex(logLines, entry);

        Assert.Equal(1, index); // Matches via Message: "The DFS Replication Service failed..."
    }

    [Fact]
    public void FindLogMatchIndex_NoMatch_ReturnsNegative()
    {
        var logLines = new List<LogLine>
        {
            new() { Text = "Starting test: Connectivity" },
            new() { Text = "......................... DC01 passed test Connectivity" },
        };

        // Use Server/Service/Message values that don't appear anywhere in the log lines
        var entry = new TestResult { Service = "SysVolCheck", Server = "NonExistentDC", Result = "PASS", Message = "no_match_here" };
        int index = MainWindow.FindLogMatchIndex(logLines, entry);

        Assert.Equal(-1, index);
    }

    [Fact]
    public void FindLogMatchIndex_EmptyLogLines_ReturnsNegative()
    {
        var logLines = new List<LogLine>();
        var entry = new TestResult { Service = "Connectivity", Server = "DC01", Result = "PASS" };

        int index = MainWindow.FindLogMatchIndex(logLines, entry);

        Assert.Equal(-1, index);
    }

    [Fact]
    public void FindLogMatchIndex_CaseInsensitive()
    {
        var logLines = new List<LogLine>
        {
            new() { Text = "Starting test: connectivity" },
            new() { Text = "passed" },
        };

        var entry = new TestResult { Service = "Connectivity", Server = "DC01", Result = "PASS" };
        int index = MainWindow.FindLogMatchIndex(logLines, entry);

        Assert.Equal(0, index);
    }

    [Fact]
    public void FindLogMatchIndex_ReturnsFirstMatch()
    {
        var logLines = new List<LogLine>
        {
            new() { Text = "Starting test: Connectivity" },
            new() { Text = "passed" },
            new() { Text = "Starting test: Connectivity" },  // duplicate
            new() { Text = "passed" },
        };

        var entry = new TestResult { Service = "Connectivity", Server = "DC01", Result = "PASS" };
        int index = MainWindow.FindLogMatchIndex(logLines, entry);

        Assert.Equal(0, index); // Should return the first match
    }

    // ── FilterLogSections (search/filter behavior) tests ────────────────

    private static readonly string FilterTestLogText = string.Join("\n", new[]
    {
        "Testing server: DC01",
        "   Starting test: Connectivity",
        "      ......................... DC01 passed test Connectivity",
        "   Starting test: DFSREvent",
        "      The DFS Replication Service failed to register.",
        "      ......................... DC01 failed test DFSREvent",
        "   Starting test: Replications",
        "      Checking DC=corp,DC=local",
        "      ......................... DC01 failed test Replications",
        "",  // blank separator between DC blocks (as in real output)
        "Testing server: DC02",
        "   Starting test: Connectivity",
        "      ......................... DC02 passed test Connectivity",
        "   Starting test: DFSREvent",
        "      ......................... DC02 passed test DFSREvent"
    });

    [Fact]
    public void FilterLogSections_NoFilters_ReturnsAllText()
    {
        var (result, _, _) = MainWindow.FilterLogSections(
            FilterTestLogText, "All domain controllers", "All Results", "All test sections", "");

        // All sections should be present
        Assert.Contains("DC01 passed test Connectivity", result);
        Assert.Contains("DC01 failed test DFSREvent", result);
        Assert.Contains("DC01 failed test Replications", result);
        Assert.Contains("DC02 passed test Connectivity", result);
        Assert.Contains("DC02 passed test DFSREvent", result);
    }

    [Fact]
    public void FilterLogSections_SearchText_OnlyMatchingLines()
    {
        var (result, _, _) = MainWindow.FilterLogSections(
            FilterTestLogText, "All domain controllers", "All Results", "All test sections", "DFSREvent");

        // Should only contain lines mentioning DFSREvent
        Assert.Contains("DFSREvent", result);
        Assert.DoesNotContain("passed test Replications", result);
        // DFSREvent sections exist for both DC01 (fail) and DC02 (pass)
        Assert.Contains("DC01 failed test DFSREvent", result);
        Assert.Contains("DC02 passed test DFSREvent", result);
    }

    [Fact]
    public void FilterLogSections_SearchText_CaseInsensitive()
    {
        var (result, _, _) = MainWindow.FilterLogSections(
            FilterTestLogText, "All domain controllers", "All Results", "All test sections", "replication");

        // Should match "DFS Replication Service failed" and "Replications" lines
        Assert.Contains("DFS Replication Service", result);
        Assert.Contains("Replications", result);
    }

    [Fact]
    public void FilterLogSections_FilterByController_OnlyShowsMatchingDC()
    {
        var (result, _, _) = MainWindow.FilterLogSections(
            FilterTestLogText, "DC02", "All Results", "All test sections", "");

        // Only DC02 sections should appear
        Assert.Contains("DC02 passed test Connectivity", result);
        Assert.Contains("DC02 passed test DFSREvent", result);
        Assert.DoesNotContain("failed test DFSREvent", result);
        Assert.DoesNotContain("failed test Replications", result);
    }

    [Fact]
    public void FilterLogSections_FilterByFailures_OnlyShowsFailedSections()
    {
        var (result, _, _) = MainWindow.FilterLogSections(
            FilterTestLogText, "All domain controllers", "Failures", "All test sections", "");

        // Only failure sections should appear
        Assert.Contains("DC01 failed test DFSREvent", result);
        Assert.Contains("DC01 failed test Replications", result);
        Assert.DoesNotContain("passed test Connectivity", result);
        Assert.DoesNotContain("passed test DFSREvent", result);
    }

    [Fact]
    public void FilterLogSections_FilterByPasses_OnlyShowsPassedSections()
    {
        var (result, _, _) = MainWindow.FilterLogSections(
            FilterTestLogText, "All domain controllers", "Passes", "All test sections", "");

        // Only passed sections should appear
        Assert.Contains("DC01 passed test Connectivity", result);
        Assert.Contains("DC02 passed test Connectivity", result);
        Assert.Contains("DC02 passed test DFSREvent", result);
        Assert.DoesNotContain("failed test DFSREvent", result);
        Assert.DoesNotContain("failed test Replications", result);
    }

    [Fact]
    public void FilterLogSections_FilterBySection_OnlyShowsMatchingService()
    {
        var (result, _, _) = MainWindow.FilterLogSections(
            FilterTestLogText, "All domain controllers", "All Results", "Connectivity", "");

        // Only Connectivity sections should appear
        Assert.Contains("DC01 passed test Connectivity", result);
        Assert.Contains("DC02 passed test Connectivity", result);
        Assert.DoesNotContain("DFSREvent", result);
        Assert.DoesNotContain("Replications", result);
    }

    [Fact]
    public void FilterLogSections_CombinedFilters_ControllerAndFailures()
    {
        var (result, sectionCount, controllerCount) = MainWindow.FilterLogSections(
            FilterTestLogText, "DC01", "Failures", "All test sections", "");

        // Only DC01 failures: DFSREvent and Replications
        Assert.Contains("DC01 failed test DFSREvent", result);
        Assert.Contains("DC01 failed test Replications", result);
        Assert.DoesNotContain("passed test Connectivity", result);
        Assert.DoesNotContain("passed test DFSREvent", result);
        Assert.Equal(2, sectionCount);
        Assert.Equal(1, controllerCount);
    }

    [Fact]
    public void FilterLogSections_CombinedFilters_ControllerSectionAndSearch()
    {
        var (result, _, _) = MainWindow.FilterLogSections(
            FilterTestLogText, "DC01", "All Results", "DFSREvent", "failed");

        // DC01 DFSREvent section, only lines containing "failed"
        Assert.Contains("DFS Replication Service failed to register", result);
        Assert.Contains("DC01 failed test DFSREvent", result);
        Assert.DoesNotContain("Connectivity", result);
        Assert.DoesNotContain("DC02", result);
    }

    [Fact]
    public void FilterLogSections_SearchNoMatch_ReturnsEmpty()
    {
        var (result, sectionCount, _) = MainWindow.FilterLogSections(
            FilterTestLogText, "All domain controllers", "All Results", "All test sections", "nonexistent_term_xyz");

        Assert.Equal(string.Empty, result);
        Assert.Equal(0, sectionCount);
    }

    [Fact]
    public void FilterLogSections_FilterNoMatch_ReturnsEmpty()
    {
        var (result, _, _) = MainWindow.FilterLogSections(
            FilterTestLogText, "DC99", "All Results", "All test sections", "");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FilterLogSections_EmptySource_ReturnsEmpty()
    {
        var (result, _, _) = MainWindow.FilterLogSections(
            string.Empty, "All domain controllers", "All Results", "All test sections", "");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FilterLogSections_ReturnsCorrectSectionAndControllerCounts()
    {
        var (result, sectionCount, controllerCount) = MainWindow.FilterLogSections(
            FilterTestLogText, "All domain controllers", "Failures", "All test sections", "");

        Assert.Equal(2, sectionCount);  // DFSREvent + Replications
        Assert.Equal(1, controllerCount);  // DC01 only
    }

    // ── MatchesLogResultFilter (TestResult predicate) tests ──────────────

    private static readonly TestResult Dc01Pass = new() { Service = "Connectivity", Server = "DC01", Result = "PASS", Message = "passed" };
    private static readonly TestResult Dc01Fail = new() { Service = "DFSREvent", Server = "DC01", Result = "FAIL", Message = "DFS Replication Service failed" };
    private static readonly TestResult Dc02Pass = new() { Service = "Connectivity", Server = "DC02", Result = "PASS", Message = "passed" };
    private static readonly TestResult Dc02Fail = new() { Service = "DFSREvent", Server = "DC02", Result = "FAIL", Message = "service not running" };

    [Fact]
    public void MatchesLogResultFilter_NoFilters_ReturnsTrue()
    {
        Assert.True(MainWindow.MatchesLogResultFilter(Dc01Pass, "", "All domain controllers", "All Results", "All test sections"));
        Assert.True(MainWindow.MatchesLogResultFilter(Dc01Fail, "", "All domain controllers", "All Results", "All test sections"));
    }

    [Fact]
    public void MatchesLogResultFilter_FilterByDC_OnlyMatchingDC()
    {
        Assert.True(MainWindow.MatchesLogResultFilter(Dc01Pass, "", "DC01", "All Results", "All test sections"));
        Assert.False(MainWindow.MatchesLogResultFilter(Dc02Pass, "", "DC01", "All Results", "All test sections"));
        Assert.True(MainWindow.MatchesLogResultFilter(Dc02Fail, "", "DC02", "All Results", "All test sections"));
    }

    [Fact]
    public void MatchesLogResultFilter_FilterByResult_FailuresOnly()
    {
        Assert.False(MainWindow.MatchesLogResultFilter(Dc01Pass, "", "All domain controllers", "Failures", "All test sections"));
        Assert.True(MainWindow.MatchesLogResultFilter(Dc01Fail, "", "All domain controllers", "Failures", "All test sections"));
        Assert.True(MainWindow.MatchesLogResultFilter(Dc02Fail, "", "All domain controllers", "Failures", "All test sections"));
    }

    [Fact]
    public void MatchesLogResultFilter_FilterByResult_PassesOnly()
    {
        Assert.True(MainWindow.MatchesLogResultFilter(Dc01Pass, "", "All domain controllers", "Passes", "All test sections"));
        Assert.False(MainWindow.MatchesLogResultFilter(Dc01Fail, "", "All domain controllers", "Passes", "All test sections"));
    }

    [Fact]
    public void MatchesLogResultFilter_FilterBySection_OnlyMatchingService()
    {
        Assert.True(MainWindow.MatchesLogResultFilter(Dc01Pass, "", "All domain controllers", "All Results", "Connectivity"));
        Assert.False(MainWindow.MatchesLogResultFilter(Dc01Fail, "", "All domain controllers", "All Results", "Connectivity"));
        Assert.True(MainWindow.MatchesLogResultFilter(Dc01Fail, "", "All domain controllers", "All Results", "DFSREvent"));
    }

    [Fact]
    public void MatchesLogResultFilter_SearchText_MatchesServiceServerMessage()
    {
        // Matches Service
        Assert.True(MainWindow.MatchesLogResultFilter(Dc01Pass, "Connectivity", "All domain controllers", "All Results", "All test sections"));
        // Matches Server
        Assert.True(MainWindow.MatchesLogResultFilter(Dc01Pass, "DC01", "All domain controllers", "All Results", "All test sections"));
        // Matches Message
        Assert.True(MainWindow.MatchesLogResultFilter(Dc01Fail, "Replication", "All domain controllers", "All Results", "All test sections"));
        // Matches Result
        Assert.True(MainWindow.MatchesLogResultFilter(Dc01Fail, "fail", "All domain controllers", "All Results", "All test sections"));
        // No match
        Assert.False(MainWindow.MatchesLogResultFilter(Dc01Pass, "nonexistent", "All domain controllers", "All Results", "All test sections"));
    }

    [Fact]
    public void MatchesLogResultFilter_SearchText_CaseInsensitive()
    {
        Assert.True(MainWindow.MatchesLogResultFilter(Dc01Pass, "connectivity", "All domain controllers", "All Results", "All test sections"));
        Assert.True(MainWindow.MatchesLogResultFilter(Dc01Fail, "REPLICATION", "All domain controllers", "All Results", "All test sections"));
    }

    [Fact]
    public void MatchesLogResultFilter_CombinedFilters_DCResultAndSearch()
    {
        // DC01 failures containing "Replication"
        Assert.True(MainWindow.MatchesLogResultFilter(Dc01Fail, "Replication", "DC01", "Failures", "All test sections"));
        // DC01 pass - filtered out by result
        Assert.False(MainWindow.MatchesLogResultFilter(Dc01Pass, "Connectivity", "DC01", "Failures", "All test sections"));
        // DC02 failure - filtered out by DC
        Assert.False(MainWindow.MatchesLogResultFilter(Dc02Fail, "Replication", "DC01", "Failures", "All test sections"));
    }

    // ── Full log filter pipeline integration ─────────────────────────────

    [Fact]
    public void FullPipeline_BuildLogLinesFromDcdiagOutput_ColorCodesCorrectly()
    {
        string dcdiagOutput = string.Join("\n", new[]
        {
            "Directory Server Diagnosis",
            "",
            "Testing server: Default-First-Site-Name\\DC01",
            "   Starting test: Connectivity",
            "      ......................... DC01 passed test Connectivity",
            "   Starting test: DFSREvent",
            "      The DFS Replication Service failed to register.",
            "      ......................... DC01 failed test DFSREvent",
            "   Starting test: Services",
            "      ......................... DC01 passed test Services"
        });

        List<LogLine> logLines = MainWindow.BuildLogLines(dcdiagOutput);

        Assert.NotEmpty(logLines);

        // Count colored lines
        int failCount = logLines.Count(l => l.FontWeight == FontWeights.SemiBold &&
            ((SolidColorBrush)l.Foreground).Color.R == 211);
        int passCount = logLines.Count(l => l.FontWeight == FontWeights.SemiBold &&
            ((SolidColorBrush)l.Foreground).Color.R == 46);

        Assert.Equal(2, failCount); // "failed to register" + "failed test DFSREvent"
        Assert.Equal(2, passCount); // "passed test Connectivity" + "passed test Services"
    }

    [Fact]
    public void FullPipeline_ParseSectionsThenFindEntry_WorksEndToEnd()
    {
        string logText = string.Join("\n", new[]
        {
            "---- Results for DC: DC01 ----",
            "   Starting test: Connectivity",
            "      ......................... DC01 passed test Connectivity",
            "   Starting test: DFSREvent",
            "      The DFS Replication Service failed to register.",
            "      ......................... DC01 failed test DFSREvent",
            "   Starting test: Services",
            "      ......................... DC01 passed test Services"
        });

        // Step 1: Parse into sections
        var sections = MainWindow.ParseLogSections(logText);
        Assert.Equal(3, sections.Count);

        // Step 2: Build log lines (as would be displayed)
        List<LogLine> logLines = MainWindow.BuildLogLines(logText);

        // Step 3: Simulate navigating to the DFSREvent failure
        var failedSection = sections.First(s => s.Result == "FAIL");
        Assert.Equal("DFSREvent", failedSection.Service);

        // Null out Server so the match is purely by Service (not by "DC01" in the header line)
        var entry = new TestResult
        {
            Service = failedSection.Service,
            Result = "FAIL",
            Message = "DFS Replication Service failed"
        };

        int matchIndex = MainWindow.FindLogMatchIndex(logLines, entry);
        Assert.True(matchIndex >= 0, "Should find the DFSREvent failure in the log lines");
        Assert.Contains("DFSREvent", logLines[matchIndex].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FullPipeline_MultiServerLog_CorrectSectionsAndServers()
    {
        string logText = string.Join("\n", new[]
        {
            "Command: dcdiag /s:dc01.corp.local /c /v",
            "---- Results for DC: DC01 ----",
            "   Starting test: Connectivity",
            "      ......................... DC01 passed test Connectivity",
            "   Starting test: Replications",
            "      Replication latency is too high.",
            "      ......................... DC01 failed test Replications",
            "",
            "Command: dcdiag /s:dc02.corp.local /c /v",
            "---- Results for DC: DC02 ----",
            "   Starting test: Connectivity",
            "      ......................... DC02 passed test Connectivity",
            "   Starting test: DFSREvent",
            "      ......................... DC02 failed test DFSREvent"
        });

        var sections = MainWindow.ParseLogSections(logText);

        Assert.Equal(4, sections.Count);

        // DC01 sections (server is inferred from result lines since Testing server has site prefix)
        var dc01Sections = sections.Where(s => s.Server == "DC01").ToList();
        var dc02Sections = sections.Where(s => s.Server == "DC02").ToList();
        Assert.Equal(2, dc01Sections.Count);
        Assert.Equal(2, dc02Sections.Count);
        Assert.Contains(dc01Sections, s => s.Service == "Connectivity" && s.Result == "PASS");
        Assert.Contains(dc01Sections, s => s.Service == "Replications" && s.Result == "FAIL");
        Assert.Contains(dc02Sections, s => s.Service == "Connectivity" && s.Result == "PASS");
        Assert.Contains(dc02Sections, s => s.Service == "DFSREvent" && s.Result == "FAIL");

        // Build log lines and verify count
        List<LogLine> logLines = MainWindow.BuildLogLines(logText);
        Assert.True(logLines.Count >= 10, $"Expected at least 10 log lines, got {logLines.Count}");
    }

    [Fact]
    public void FullPipeline_FilterThenBuildLogLines_EndToEnd()
    {
        // Simulate the full Logs tab workflow: filter sections then render as log lines
        var (filteredText, sectionCount, controllerCount) = MainWindow.FilterLogSections(
            FilterTestLogText, "DC01", "Failures", "All test sections", "");

        Assert.NotEmpty(filteredText);

        // Build log lines from filtered text
        List<LogLine> lines = MainWindow.BuildLogLines(filteredText);
        Assert.NotEmpty(lines);

        // All lines should be from failed sections only
        Assert.All(lines, line => Assert.DoesNotContain("Connectivity", line.Text));

        // Verify the count info
        Assert.Equal(2, sectionCount);
        Assert.Equal(1, controllerCount);
    }
}
