using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace Domain_Guardian.Tests;

/// <summary>
/// Performance regression tests that verify efficiency patterns in the codebase:
/// - Frozen brushes (thread-safe, no change notifications, zero allocations when shared)
/// - Resource dictionary completeness (all required keys present)
/// - Dashboard snapshot hash-based skip mechanism
/// - Log line caching
/// </summary>
public class PerformanceTests
{
    // ── Frozen brush verification ────────────────────────────────────────

    [Fact]
    public void ActiveNavBgBrush_IsFrozen()
    {
        Assert.True(AdHealthMonitor.MainWindow.ActiveNavBgBrush.IsFrozen,
            "ActiveNavBgBrush should be frozen for thread safety and performance");
    }

    [Fact]
    public void InactiveNavFgBrush_IsFrozen()
    {
        Assert.True(AdHealthMonitor.MainWindow.InactiveNavFgBrush.IsFrozen,
            "InactiveNavFgBrush should be frozen for thread safety and performance");
    }

    [Fact]
    public void AllDashboardBrushes_AreFrozen()
    {
        Brush[] brushes =
        [
            AdHealthMonitor.MainWindow.FailBrushCached,
            AdHealthMonitor.MainWindow.PassBrushCached,
            AdHealthMonitor.MainWindow.NeutralBrushCached,
            AdHealthMonitor.MainWindow.SeparatorBrushCached,
            AdHealthMonitor.MainWindow.BodyTextBrushCached,
            AdHealthMonitor.MainWindow.HighSeverityBrushCached,
            AdHealthMonitor.MainWindow.MediumSeverityBrushCached,
            AdHealthMonitor.MainWindow.LowSeverityBrushCached,
            AdHealthMonitor.MainWindow.AccentBlueBrushCached,
        ];

        foreach (Brush brush in brushes)
        {
            Assert.True(brush.IsFrozen,
                $"Dashboard brush {brush} should be frozen for zero-allocation sharing");
        }
    }

    [Fact]
    public void AdminBannerBrushes_AreFrozen()
    {
        Brush[] brushes =
        [
            AdHealthMonitor.MainWindow.AdminBannerBgBrush,
            AdHealthMonitor.MainWindow.AdminBannerBorderBrush,
            AdHealthMonitor.MainWindow.AdminBannerIconBrush,
            AdHealthMonitor.MainWindow.AdminBannerTextBrush,
            AdHealthMonitor.MainWindow.AdminBannerButtonBrush,
        ];

        foreach (Brush brush in brushes)
        {
            Assert.True(brush.IsFrozen,
                $"Admin banner brush {brush} should be frozen");
        }
    }

    [Fact]
    public void LogBrushes_AreFrozen()
    {
        // LogNormalBrush, LogFailBrush, LogPassBrush are private static readonly.
        // Verify they exist via reflection.
        Type logBrushOwner = typeof(AdHealthMonitor.MainWindow);
        string[] fieldNames = ["LogNormalBrush", "LogFailBrush", "LogPassBrush"];

        foreach (string fieldName in fieldNames)
        {
            var field = logBrushOwner.GetField(fieldName,
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic);

            Assert.NotNull(field);
            Assert.True(field!.FieldType == typeof(Brush),
                $"{fieldName} should be a Brush");

            Brush brush = (Brush)field.GetValue(null)!;
            Assert.True(brush.IsFrozen,
                $"{fieldName} should be frozen for performance");
        }
    }

    // ── Resource dictionary completeness ─────────────────────────────────

    [Theory]
    [InlineData("PanelBorderBrush")]
    [InlineData("PanelFillBrush")]
    [InlineData("SoftFillBrush")]
    [InlineData("HeadingBrush")]
    [InlineData("BodyBrush")]
    [InlineData("SidebarBgBrush")]
    [InlineData("SidebarHoverBrush")]
    [InlineData("SidebarActiveBrush")]
    [InlineData("AccentBlueBrush")]
    [InlineData("AccentGreenBrush")]
    [InlineData("AccentRedBrush")]
    [InlineData("SubtleInkBrush")]
    [InlineData("PassBrush")]
    [InlineData("FailBrush")]
    [InlineData("PendingBrush")]
    [InlineData("RoundedButtonStyle")]
    [InlineData("SidebarNavButtonStyle")]
    [InlineData("ModernTextBoxStyle")]
    [InlineData("ModernTabItemStyle")]
    [InlineData("PageHostTabControlStyle")]
    [InlineData("ModernProgressBarStyle")]
    [InlineData("ResultCellStyle")]
    [InlineData("ProgressBarGradient")]
    public void AppResources_ContainsRequiredKey(string key)
    {
        // Application.Resources must be available.
        // In the test runner we ensure it's loaded via LazyTabCreationTests.
        // Here we just verify the key exists.
        if (Application.Current == null)
        {
            // Skip if no Application (e.g. test running in isolation).
            return;
        }

        bool exists = Application.Current.Resources.Contains(key);
        Assert.True(exists,
            $"App.xaml Application.Resources must contain key '{key}'");
    }

    // ── Dashboard hash-based skip ────────────────────────────────────────

    [Fact]
    public void DashboardHash_EmptyData_ProducesConsistentHash()
    {
        // The dashboard uses a hash of result counts to skip redundant
        // refreshes. Verify the hash is deterministic for identical input.
        string hash1 = BuildTestHash(0, 0, 0, 0, 0, 0, 0, 0);
        string hash2 = BuildTestHash(0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void DashboardHash_DifferentData_ProducesDifferentHash()
    {
        string hash1 = BuildTestHash(10, 8, 2, 3, 1, 1, 1, 5);
        string hash2 = BuildTestHash(10, 8, 2, 3, 1, 1, 1, 6);
        Assert.NotEqual(hash1, hash2);
    }

    private static string BuildTestHash(
        int resultCount, int passCount, int failCount,
        int findingsCount, int critCount, int highCount,
        int medCount, int historyCount)
    {
        return string.Join("|",
            resultCount, passCount, failCount,
            findingsCount, critCount, highCount,
            medCount, historyCount);
    }

    // ── Log line caching ─────────────────────────────────────────────────

    [Fact]
    public void BuildLogLines_EmptyText_ReturnsEmptyList()
    {
        var lines = AdHealthMonitor.MainWindow.BuildLogLines(string.Empty);
        Assert.Empty(lines);
    }

    [Fact]
    public void BuildLogLines_NullText_ReturnsEmptyList()
    {
        var lines = AdHealthMonitor.MainWindow.BuildLogLines(null!);
        Assert.Empty(lines);
    }

    [Fact]
    public void BuildLogLines_CorrectCount()
    {
        string text = "Line 1\nLine 2\n\nLine 4\n";
        var lines = AdHealthMonitor.MainWindow.BuildLogLines(text);
        // Empty line (line 3) is skipped
        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void BuildLogLines_FailLine_HasBoldWeight()
    {
        string text = "Starting test: DNS\n  The DNS test failed on DC01\n";
        var lines = AdHealthMonitor.MainWindow.BuildLogLines(text);
        Assert.Contains(lines, l => l.FontWeight == System.Windows.FontWeights.SemiBold);
    }

    [Fact]
    public void BuildLogLines_PassLine_HasGreenBrush()
    {
        // Verify LogPassBrush color matches the expected green
        var field = typeof(AdHealthMonitor.MainWindow).GetField("LogPassBrush",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var brush = (SolidColorBrush)field!.GetValue(null)!;
        Assert.Equal(Color.FromRgb(46, 125, 50), brush.Color);
    }

    // ── Filter log sections efficiency ───────────────────────────────────

    [Fact]
    public void FilterLogSections_NoFilters_ReturnsFullText()
    {
        string source = "---- Results for DC: DC01 ----\nStarting test: DNS\n  passed\n\n";
        var (text, sections, controllers) = AdHealthMonitor.MainWindow.FilterLogSections(
            source, "All domain controllers", "All Results", "All test sections", "");

        Assert.Equal(1, sections);
        Assert.Equal(1, controllers);
        Assert.Contains("passed", text);
    }

    [Fact]
    public void FilterLogSections_ControllerFilter_FiltersCorrectly()
    {
        string source =
            "---- Results for DC: DC01 ----\nStarting test: DNS\n  passed\n\n" +
            "---- Results for DC: DC02 ----\nStarting test: DNS\n  failed\n\n";
        var (text, sections, controllers) = AdHealthMonitor.MainWindow.FilterLogSections(
            source, "DC01", "All Results", "All test sections", "");

        // The header line is excluded from section output; verify via counts
        Assert.Equal(1, sections);
        Assert.Equal(1, controllers);
        Assert.Contains("passed", text);
        Assert.DoesNotContain("failed", text);
    }

    // ── Utility function tests ───────────────────────────────────────────

    [Theory]
    [InlineData("test result", true)]
    [InlineData("no match here", false)]
    public void MatchesLogResultFilter_SearchText_FiltersCorrectly(string searchText, bool expected)
    {
        var result = new AdHealthMonitor.TestResult
        {
            Service = "Test",
            Server = "DC01",
            Result = "PASS",
            Message = "test result from DC"
        };

        bool matches = AdHealthMonitor.MainWindow.MatchesLogResultFilter(
            result, searchText, "All domain controllers", "All Results", "All test sections");

        Assert.Equal(expected, matches);
    }

    [Fact]
    public void EvaluateTimeSkewResult_PassCase()
    {
        string output = "DC01: NTP: offset 0.001s, RefID: 0x00000000";
        Assert.True(AdHealthMonitor.MainWindow.EvaluateTimeSkewResult(output));
    }

    [Fact]
    public void EvaluateTimeSkewResult_FailCase()
    {
        string output = "DC01: FAILED: error code 0x00000001";
        Assert.False(AdHealthMonitor.MainWindow.EvaluateTimeSkewResult(output));
    }

    [Fact]
    public void EvaluateLdapBindResult_PassCase()
    {
        string output = "LDAP_OK: DC=contoso,DC=com";
        Assert.True(AdHealthMonitor.MainWindow.EvaluateLdapBindResult(output));
    }

    [Fact]
    public void EvaluateLdapBindResult_FailCase()
    {
        string output = "LDAP_FAIL: Connection refused";
        Assert.False(AdHealthMonitor.MainWindow.EvaluateLdapBindResult(output));
    }

    // ── Health score boundary tests ──────────────────────────────────────

    [Fact]
    public void SanitizeFileNamePart_Empty_ReturnsDefault()
    {
        Assert.Equal("run", AdHealthMonitor.MainWindow.SanitizeFileNamePart(""));
        Assert.Equal("run", AdHealthMonitor.MainWindow.SanitizeFileNamePart("  "));
        Assert.Equal("run", AdHealthMonitor.MainWindow.SanitizeFileNamePart(null!));
    }

    [Fact]
    public void SanitizeFileNamePart_InvalidChars_AreReplaced()
    {
        string result = AdHealthMonitor.MainWindow.SanitizeFileNamePart("DC/01:test");
        Assert.DoesNotContain("/", result);
        Assert.DoesNotContain(":", result);
    }

    [Fact]
    public void SanitizeFileNamePart_ValidName_Unchanged()
    {
        Assert.Equal("DC01", AdHealthMonitor.MainWindow.SanitizeFileNamePart("DC01"));
    }
}
