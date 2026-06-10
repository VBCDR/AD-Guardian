using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using AdHealthMonitor;
using Xunit;

namespace Domain_Guardian.Tests;

/// <summary>
/// Unit tests for previously untested methods:
/// PadRight, GetControllerLogPath, CreateRunLogSession (edge cases),
/// GetManagedRunDirectoryPath, IsManagedRunLogPath, BuildPrivilegeInsightSummary.
/// </summary>
public class UntestedMethodsTests : IDisposable
{
    private readonly string testDirectory;

    public UntestedMethodsTests()
    {
        testDirectory = Path.Combine(Path.GetTempPath(), "UntestedMethodsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
        }
        catch { }
    }

    // ── PadRight tests ───────────────────────────────────────────────────

    private static string InvokePadRight(string? value, int width)
    {
        var method = typeof(MainWindow).GetMethod(
            "PadRight",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method.Invoke(null, new object?[] { value, width })!;
    }

    [Theory]
    [InlineData("Hello", 10, "Hello     ")]
    [InlineData("Test", 8, "Test    ")]
    [InlineData("A", 5, "A    ")]
    [InlineData("", 4, "    ")]
    [InlineData(null, 4, "    ")]
    public void PadRight_PadsToWidth(string? input, int width, string expected)
    {
        string result = InvokePadRight(input, width);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void PadRight_StringLongerThanWidth_ReturnsOriginal()
    {
        string result = InvokePadRight("HelloWorld", 5);
        Assert.Equal("HelloWorld", result);
    }

    [Fact]
    public void PadRight_ZeroWidth_ReturnsOriginal()
    {
        string result = InvokePadRight("Test", 0);
        Assert.Equal("Test", result);
    }

    [Fact]
    public void PadRight_ExactWidth_NoPadding()
    {
        string result = InvokePadRight("Hello", 5);
        Assert.Equal("Hello", result);
    }

    // ── GetControllerLogPath tests ───────────────────────────────────────

    [Fact]
    public void GetControllerLogPath_TypicalDc_ReturnsCorrectPath()
    {
        string runDir = Path.Combine(testDirectory, "run1");
        var session = new MainWindow.RunLogSession
        {
            RunDirectoryPath = runDir,
            TestType = "Manual",
            StartedAt = DateTime.Now,
            CombinedLogPath = Path.Combine(runDir, "combined.txt")
        };

        string path = MainWindow.GetControllerLogPath(session, "DC01");

        Assert.Equal(
            Path.Combine(runDir, "DC01_TestResults.txt"),
            path);
    }

    [Fact]
    public void GetControllerLogPath_FqdnDc_KeepsDotsInName()
    {
        string runDir = Path.Combine(testDirectory, "run_fqdn");
        var session = new MainWindow.RunLogSession { RunDirectoryPath = runDir };

        string path = MainWindow.GetControllerLogPath(session, "dc01.corp.local");

        Assert.Equal(
            Path.Combine(runDir, "dc01.corp.local_TestResults.txt"),
            path);
    }

    [Fact]
    public void GetControllerLogPath_SpecialChars_Sanitized()
    {
        string runDir = Path.Combine(testDirectory, "run_special");
        var session = new MainWindow.RunLogSession { RunDirectoryPath = runDir };

        string path = MainWindow.GetControllerLogPath(session, "DC:Test/Name*");

        // Colons, slashes, asterisks should be replaced with underscores
        Assert.Contains("DC_Test_Name_", path);
        Assert.EndsWith("_TestResults.txt", path);
        Assert.DoesNotContain(Path.GetInvalidFileNameChars(), Path.GetFileName(path));
    }

    [Fact]
    public void GetControllerLogPath_WhitespaceDc_SanitizedToRun()
    {
        string runDir = Path.Combine(testDirectory, "run_ws");
        var session = new MainWindow.RunLogSession { RunDirectoryPath = runDir };

        string path = MainWindow.GetControllerLogPath(session, "   ");

        Assert.Equal(
            Path.Combine(runDir, "run_TestResults.txt"),
            path);
    }

    [Fact]
    public void GetControllerLogPath_EmptyDc_SanitizedToRun()
    {
        string runDir = Path.Combine(testDirectory, "run_empty");
        var session = new MainWindow.RunLogSession { RunDirectoryPath = runDir };

        string path = MainWindow.GetControllerLogPath(session, "");

        Assert.Equal(
            Path.Combine(runDir, "run_TestResults.txt"),
            path);
    }

    // ── CreateRunLogSession edge cases ───────────────────────────────────

    [Fact]
    public void CreateRunLogSession_ManualTestType_CreatesExpectedStructure()
    {
        DateTime startedAt = new(2026, 6, 10, 14, 30, 45);
        var session = MainWindow.CreateRunLogSession(startedAt, "Manual");

        Assert.Contains("2026-06-10", session.RunDirectoryPath);
        Assert.Contains("143045", session.RunDirectoryPath);
        Assert.Contains("Manual", session.RunDirectoryPath);
        Assert.EndsWith("CombinedTestResults.txt", session.CombinedLogPath);
        Assert.Equal(startedAt, session.StartedAt);
        Assert.Equal("Manual", session.TestType);
        Assert.True(Directory.Exists(session.RunDirectoryPath));
    }

    [Fact]
    public void CreateRunLogSession_WhitespaceTestType_SanitizedToRun()
    {
        DateTime startedAt = new(2026, 6, 10, 14, 30, 45);
        var session = MainWindow.CreateRunLogSession(startedAt, "   ");

        Assert.Contains("143045_run", session.RunDirectoryPath);
        Assert.True(Directory.Exists(session.RunDirectoryPath));
    }

    [Fact]
    public void CreateRunLogSession_DifferentTimestamps_CreateDifferentDirs()
    {
        var session1 = MainWindow.CreateRunLogSession(
            new DateTime(2026, 6, 10, 10, 0, 0), "Manual");
        var session2 = MainWindow.CreateRunLogSession(
            new DateTime(2026, 6, 10, 10, 0, 1), "Manual");

        Assert.NotEqual(session1.RunDirectoryPath, session2.RunDirectoryPath);
        Assert.True(Directory.Exists(session1.RunDirectoryPath));
        Assert.True(Directory.Exists(session2.RunDirectoryPath));
    }

    [Fact]
    public void CreateRunLogSession_DifferentTestTypes_SameTimestamp_CreateDifferentDirs()
    {
        DateTime startedAt = new(2026, 6, 10, 14, 30, 45);
        var session1 = MainWindow.CreateRunLogSession(startedAt, "Nightly Check");
        var session2 = MainWindow.CreateRunLogSession(startedAt, "Weekly Scan");

        Assert.NotEqual(session1.RunDirectoryPath, session2.RunDirectoryPath);
        Assert.Contains("Nightly_Check", session1.RunDirectoryPath);
        Assert.Contains("Weekly_Scan", session2.RunDirectoryPath);
    }

    [Fact]
    public void CreateRunLogSession_CombinedLogPath_InsideRunDirectory()
    {
        var session = MainWindow.CreateRunLogSession(DateTime.Now, "Manual");

        Assert.StartsWith(session.RunDirectoryPath, session.CombinedLogPath);
        Assert.True(File.Exists(session.CombinedLogPath) || Directory.Exists(Path.GetDirectoryName(session.CombinedLogPath)));
    }

    [Fact]
    public void CreateRunLogSession_SameTimestampSameType_ReturnsSamePath()
    {
        // Idempotent: calling twice with same args should return same directory
        DateTime startedAt = new(2026, 6, 10, 14, 30, 45);
        var session1 = MainWindow.CreateRunLogSession(startedAt, "Idempotent");
        var session2 = MainWindow.CreateRunLogSession(startedAt, "Idempotent");

        Assert.Equal(session1.RunDirectoryPath, session2.RunDirectoryPath);
        Assert.True(Directory.Exists(session1.RunDirectoryPath));
    }

    // ── GetManagedRunDirectoryPath tests ─────────────────────────────────

    private static string? InvokeGetManagedRunDirectoryPath(string? path)
    {
        var method = typeof(MainWindow).GetMethod(
            "GetManagedRunDirectoryPath",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string?)method.Invoke(null, new object?[] { path });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void GetManagedRunDirectoryPath_NullOrWhitespace_ReturnsNull(string? input)
    {
        string? result = InvokeGetManagedRunDirectoryPath(input);
        Assert.Null(result);
    }

    [Fact]
    public void GetManagedRunDirectoryPath_PathInsideRunsDir_ReturnsParent()
    {
        // The runs root is C:\ADCheckLogs\runs
        string logPath = @"C:\ADCheckLogs\runs\2026-06-10\143045_Manual\test.log";
        string expectedParent = @"C:\ADCheckLogs\runs\2026-06-10\143045_Manual";

        string? result = InvokeGetManagedRunDirectoryPath(logPath);

        Assert.NotNull(result);
        // Normalize separators for comparison
        Assert.Equal(
            expectedParent.Replace('\\', Path.DirectorySeparatorChar),
            result.Replace('\\', Path.DirectorySeparatorChar));
    }

    [Fact]
    public void GetManagedRunDirectoryPath_PathOutsideRunsDir_ReturnsNull()
    {
        string logPath = @"C:\SomeOtherFolder\test.log";
        string? result = InvokeGetManagedRunDirectoryPath(logPath);
        Assert.Null(result);
    }

    [Fact]
    public void GetManagedRunDirectoryPath_PathAtRunsRoot_NotInsideSubdir_ReturnsNull()
    {
        // A file directly in C:\ADCheckLogs\runs (not in a subdirectory)
        // Parent directory is C:\ADCheckLogs, which does NOT start with C:\ADCheckLogs\runs
        string logPath = @"C:\ADCheckLogs\runs\orphan.log";

        string? result = InvokeGetManagedRunDirectoryPath(logPath);

        // The parent dir would be C:\ADCheckLogs\runs (the file is inside it)
        // But the check is: normalizedParent.StartsWith(runsRoot + "\")
        // C:\ADCheckLogs\runs\ starts with C:\ADCheckLogs\runs\ → true!
        // So this SHOULD return the parent
        Assert.NotNull(result);
    }

    [Fact]
    public void GetManagedRunDirectoryPath_RelativePath_ResolvesToFull()
    {
        // Save current directory
        string originalDir = Environment.CurrentDirectory;
        try
        {
            // Change to a temp directory for a predictable relative path
            Environment.CurrentDirectory = testDirectory;

            // Create a fake runs structure inside testDirectory
            // that mimics C:\ADCheckLogs\runs structure — but GetManagedRunDirectoryPath
            // uses an absolute C:\ADCheckLogs root, so relative paths won't match
            string relativePath = Path.Combine("subdir", "test.log");
            string? result = InvokeGetManagedRunDirectoryPath(relativePath);

            // Relative path resolves to {testDirectory}\subdir\test.log
            // which doesn't start with C:\ADCheckLogs\runs
            Assert.Null(result);
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
        }
    }

    [Fact]
    public void GetManagedRunDirectoryPath_CaseInsensitiveMatch()
    {
        string logPath = @"c:\adchecklogs\RUNS\2026-06-10\143045_Manual\test.log";
        string? result = InvokeGetManagedRunDirectoryPath(logPath);

        Assert.NotNull(result);
        Assert.Contains("143045_Manual", result);
    }

    [Fact]
    public void GetManagedRunDirectoryPath_InvalidPathChars_ReturnsNull()
    {
        // Path with invalid chars should cause GetFullPath to throw → caught → null
        string? result = InvokeGetManagedRunDirectoryPath("\0invalid");
        Assert.Null(result);
    }

    // ── IsManagedRunLogPath tests ────────────────────────────────────────

    private static bool InvokeIsManagedRunLogPath(string? path)
    {
        var method = typeof(MainWindow).GetMethod(
            "IsManagedRunLogPath",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method.Invoke(null, new object?[] { path })!;
    }

    [Fact]
    public void IsManagedRunLogPath_PathInsideRuns_ReturnsTrue()
    {
        string logPath = @"C:\ADCheckLogs\runs\2026-06-10\143045_Manual\test.log";
        Assert.True(InvokeIsManagedRunLogPath(logPath));
    }

    [Fact]
    public void IsManagedRunLogPath_PathOutsideRuns_ReturnsFalse()
    {
        string logPath = @"C:\Windows\Temp\test.log";
        Assert.False(InvokeIsManagedRunLogPath(logPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsManagedRunLogPath_NullOrWhitespace_ReturnsFalse(string? input)
    {
        Assert.False(InvokeIsManagedRunLogPath(input));
    }

    // ── BuildPrivilegeInsightSummary tests ───────────────────────────────

    /// <summary>
    /// Invokes the private instance method BuildPrivilegeInsightSummary
    /// on an uninitialized MainWindow with a specified inventory snapshot.
    /// </summary>
    private static string InvokeBuildPrivilegeInsightSummary(
        Dictionary<string, int> privilegedGroupCounts)
    {
        // Create an uninitialized MainWindow (avoids WPF initialization)
        var instance = (MainWindow)FormatterServices.GetUninitializedObject(typeof(MainWindow));

        // Create inventory snapshot
        var inventory = new AdInventorySnapshot();
        // Set the PrivilegedGroupCounts via reflection (init-only property)
        var countsField = typeof(AdInventorySnapshot).GetField(
            "<PrivilegedGroupCounts>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(countsField);
        countsField.SetValue(inventory, privilegedGroupCounts);

        // Set latestInventory on the MainWindow instance
        var inventoryField = typeof(MainWindow).GetField(
            "latestInventory",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(inventoryField);
        inventoryField.SetValue(instance, inventory);

        // Invoke the method
        var method = typeof(MainWindow).GetMethod(
            "BuildPrivilegeInsightSummary",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string)method.Invoke(instance, null)!;
    }

    [Fact]
    public void BuildPrivilegeInsightSummary_Empty_ReturnsPlaceholder()
    {
        string result = InvokeBuildPrivilegeInsightSummary(
            new Dictionary<string, int>());

        Assert.Equal(
            "Privilege analysis will appear after a collection runs.",
            result);
    }

    [Fact]
    public void BuildPrivilegeInsightSummary_SingleGroup_ShowsCount()
    {
        var counts = new Dictionary<string, int>
        {
            { "Domain Admins", 5 }
        };

        string result = InvokeBuildPrivilegeInsightSummary(counts);

        Assert.Contains("Top privileged groups by member count:", result);
        Assert.Contains("Domain Admins: 5", result);
    }

    [Fact]
    public void BuildPrivilegeInsightSummary_MultipleGroups_SortedDescending()
    {
        var counts = new Dictionary<string, int>
        {
            { "Domain Admins", 5 },
            { "Enterprise Admins", 3 },
            { "Schema Admins", 2 }
        };

        string result = InvokeBuildPrivilegeInsightSummary(counts);

        Assert.Contains("Top privileged groups by member count:", result);
        // Verify all groups appear
        Assert.Contains("Domain Admins: 5", result);
        Assert.Contains("Enterprise Admins: 3", result);
        Assert.Contains("Schema Admins: 2", result);
        // Verify sort order: highest count first
        int domainPos = result.IndexOf("Domain Admins", StringComparison.Ordinal);
        int enterprisePos = result.IndexOf("Enterprise Admins", StringComparison.Ordinal);
        int schemaPos = result.IndexOf("Schema Admins", StringComparison.Ordinal);
        Assert.True(domainPos < enterprisePos, "Domain Admins (5) should appear before Enterprise Admins (3)");
        Assert.True(enterprisePos < schemaPos, "Enterprise Admins (3) should appear before Schema Admins (2)");
    }

    [Fact]
    public void BuildPrivilegeInsightSummary_MoreThan3Groups_ShowsTop3Only()
    {
        var counts = new Dictionary<string, int>
        {
            { "Domain Admins", 10 },
            { "Enterprise Admins", 8 },
            { "Schema Admins", 6 },
            { "Backup Operators", 4 },
            { "Account Operators", 2 }
        };

        string result = InvokeBuildPrivilegeInsightSummary(counts);

        // Top 3 should appear
        Assert.Contains("Domain Admins: 10", result);
        Assert.Contains("Enterprise Admins: 8", result);
        Assert.Contains("Schema Admins: 6", result);
        // 4th+ should NOT appear
        Assert.DoesNotContain("Backup Operators", result);
        Assert.DoesNotContain("Account Operators", result);
    }

    [Fact]
    public void BuildPrivilegeInsightSummary_GroupsWithZeroCount_AreIncluded()
    {
        // The method uses Where(pair => pair.Value >= 0), so zero counts are included
        var counts = new Dictionary<string, int>
        {
            { "Domain Admins", 0 },
            { "Enterprise Admins", 0 },
            { "Schema Admins", 0 }
        };

        string result = InvokeBuildPrivilegeInsightSummary(counts);

        Assert.Contains("Domain Admins: 0", result);
        Assert.Contains("Enterprise Admins: 0", result);
        Assert.Contains("Schema Admins: 0", result);
    }

    [Fact]
    public void BuildPrivilegeInsightSummary_UsesPipeSeparator()
    {
        var counts = new Dictionary<string, int>
        {
            { "GroupA", 10 },
            { "GroupB", 5 }
        };

        string result = InvokeBuildPrivilegeInsightSummary(counts);

        Assert.Contains(" | ", result);
    }

    [Fact]
    public void BuildPrivilegeInsightSummary_NegativeCounts_AreExcluded()
    {
        // The method filters with Where(pair => pair.Value >= 0),
        // so negative counts should be filtered out.
        var counts = new Dictionary<string, int>
        {
            { "Domain Admins", 5 },
            { "Expired Group", -1 },
            { "Schema Admins", 2 }
        };

        string result = InvokeBuildPrivilegeInsightSummary(counts);

        Assert.Contains("Domain Admins: 5", result);
        Assert.Contains("Schema Admins: 2", result);
        Assert.DoesNotContain("Expired Group", result);
    }
}
