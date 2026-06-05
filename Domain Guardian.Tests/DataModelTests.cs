using System;
using System.Collections.Generic;
using AdHealthMonitor;
using Xunit;

namespace Domain_Guardian.Tests;

public class PersistedAppSettingsTests
{
    [Fact]
    public void Constructor_SetsAllDefaults()
    {
        var settings = new PersistedAppSettings();

        Assert.Equal(string.Empty, settings.DomainControllers);
        Assert.Equal(string.Empty, settings.RecipientEmail);
        Assert.True(settings.TestDnsCheck);
        Assert.True(settings.TestReplication);
        Assert.True(settings.TestTimeSkew);
        Assert.True(settings.TestLdapBind);
        Assert.True(settings.TestCertDhcp);
        Assert.True(settings.TestSmbLdapSigning);
        Assert.True(settings.SendEmailManual);
        Assert.True(settings.SendEmailScheduled);
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var settings = new PersistedAppSettings
        {
            DomainControllers = "dc01.corp.local,dc02.corp.local",
            RecipientEmail = "admin@corp.local",
            TestDnsCheck = false,
            TestReplication = false,
            SendEmailManual = false
        };

        Assert.Equal("dc01.corp.local,dc02.corp.local", settings.DomainControllers);
        Assert.Equal("admin@corp.local", settings.RecipientEmail);
        Assert.False(settings.TestDnsCheck);
        Assert.False(settings.TestReplication);
        Assert.False(settings.SendEmailManual);
    }
}

public class TestHistoryEntryTests
{
    [Fact]
    public void Constructor_SetsDefaults()
    {
        var entry = new TestHistoryEntry();

        Assert.Equal(default, entry.RunDate);
        Assert.Equal(0, entry.Total);
        Assert.Equal(0, entry.Passed);
        Assert.Equal(0, entry.Failed);
        Assert.Equal(string.Empty, entry.Details);
        Assert.Equal(string.Empty, entry.LogFilePath);
        Assert.Equal(string.Empty, entry.TestType);
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var runDate = new DateTime(2025, 6, 1, 10, 30, 0, DateTimeKind.Local);
        var entry = new TestHistoryEntry
        {
            RunDate = runDate,
            Total = 10,
            Passed = 8,
            Failed = 2,
            Details = "DNS check failed on dc01",
            LogFilePath = @"C:\logs\test.log",
            TestType = "Manual"
        };

        Assert.Equal(runDate, entry.RunDate);
        Assert.Equal(10, entry.Total);
        Assert.Equal(8, entry.Passed);
        Assert.Equal(2, entry.Failed);
        Assert.Equal("DNS check failed on dc01", entry.Details);
        Assert.Equal(@"C:\logs\test.log", entry.LogFilePath);
        Assert.Equal("Manual", entry.TestType);
    }
}

public class DashboardSnapshotTests
{
    [Fact]
    public void Constructor_SetsDefaults()
    {
        var snapshot = new DashboardSnapshot();

        Assert.Equal(default, snapshot.CapturedAtUtc);
        Assert.Equal(0, snapshot.HealthScore);
        Assert.Equal(0, snapshot.CriticalFindings);
        Assert.Equal(0, snapshot.PassingTests);
        Assert.Equal(0, snapshot.ConfiguredDomainControllers);
        Assert.Equal(0, snapshot.TotalRuns);
        Assert.Equal(string.Empty, snapshot.LastRunSummary);
        Assert.Equal(0, snapshot.FindingsCriticalCount);
        Assert.Equal(0, snapshot.FindingsHighCount);
        Assert.Equal(0, snapshot.FindingsMediumCount);
        Assert.Equal(0, snapshot.FindingsLowCount);
        Assert.Equal(0, snapshot.LastRunPassed);
        Assert.Equal(0, snapshot.LastRunFailed);
        Assert.Equal(0, snapshot.LastRunTotal);
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var capturedAt = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var snapshot = new DashboardSnapshot
        {
            CapturedAtUtc = capturedAt,
            HealthScore = 85,
            CriticalFindings = 2,
            PassingTests = 18,
            ConfiguredDomainControllers = 3,
            TotalRuns = 42,
            LastRunSummary = "18/20 passed",
            FindingsCriticalCount = 2,
            FindingsHighCount = 3,
            FindingsMediumCount = 5,
            FindingsLowCount = 1,
            LastRunPassed = 18,
            LastRunFailed = 2,
            LastRunTotal = 20
        };

        Assert.Equal(capturedAt, snapshot.CapturedAtUtc);
        Assert.Equal(85, snapshot.HealthScore);
        Assert.Equal(2, snapshot.CriticalFindings);
        Assert.Equal(18, snapshot.PassingTests);
        Assert.Equal(3, snapshot.ConfiguredDomainControllers);
        Assert.Equal(42, snapshot.TotalRuns);
        Assert.Equal("18/20 passed", snapshot.LastRunSummary);
        Assert.Equal(2, snapshot.FindingsCriticalCount);
        Assert.Equal(3, snapshot.FindingsHighCount);
        Assert.Equal(5, snapshot.FindingsMediumCount);
        Assert.Equal(1, snapshot.FindingsLowCount);
        Assert.Equal(18, snapshot.LastRunPassed);
        Assert.Equal(2, snapshot.LastRunFailed);
        Assert.Equal(20, snapshot.LastRunTotal);
    }
}

public class ScheduledTaskTests
{
    [Fact]
    public void Constructor_SetsDefaults()
    {
        var task = new ScheduledTask();

        Assert.Equal(string.Empty, task.TaskName);
        Assert.Equal(string.Empty, task.DomainController);
        Assert.Equal(string.Empty, task.Frequency);
        Assert.Equal(default, task.StartDate);
        Assert.Equal(string.Empty, task.StartTime);
    }

    [Fact]
    public void ToString_FormatsCorrectly()
    {
        var task = new ScheduledTask
        {
            TaskName = "Nightly Health Check",
            DomainController = "dc01.corp.local",
            Frequency = "Daily",
            StartDate = new DateTime(2025, 6, 1),
            StartTime = "22:00"
        };

        string result = task.ToString();

        Assert.Contains("Nightly Health Check", result);
        Assert.Contains(task.StartDate.ToShortDateString(), result);
        Assert.Contains("22:00", result);
        Assert.Contains("dc01.corp.local", result);
        Assert.Contains("Daily", result);
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var startDate = new DateTime(2025, 7, 15);
        var task = new ScheduledTask
        {
            TaskName = "Weekly Replication Check",
            DomainController = "dc01.corp.local,dc02.corp.local",
            Frequency = "Weekly",
            StartDate = startDate,
            StartTime = "06:00"
        };

        Assert.Equal("Weekly Replication Check", task.TaskName);
        Assert.Equal("dc01.corp.local,dc02.corp.local", task.DomainController);
        Assert.Equal("Weekly", task.Frequency);
        Assert.Equal(startDate, task.StartDate);
        Assert.Equal("06:00", task.StartTime);
    }
}

public class AdHealthFindingTests
{
    [Fact]
    public void Constructor_SetsDefaults()
    {
        var finding = new AdHealthFinding();

        Assert.Equal(string.Empty, finding.Category);
        Assert.Equal(string.Empty, finding.Severity);
        Assert.Equal(string.Empty, finding.Source);
        Assert.Equal(string.Empty, finding.Target);
        Assert.Equal(string.Empty, finding.Summary);
        Assert.Equal(string.Empty, finding.Details);
        Assert.Equal(string.Empty, finding.Evidence);
        Assert.Equal(string.Empty, finding.Remediation);
        Assert.Equal(string.Empty, finding.Status);
        Assert.Equal(string.Empty, finding.LogFilePath);
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var finding = new AdHealthFinding
        {
            Category = "DNS",
            Severity = "Critical",
            Source = "Health Checker",
            Target = "dc01.corp.local",
            Summary = "DNS resolution failed",
            Details = "SRV records missing for _ldap._tcp",
            Evidence = "nslookup returned NXDOMAIN",
            Remediation = "Recreate DNS zone records",
            Status = "Open",
            LogFilePath = @"C:\logs\dns.log"
        };

        Assert.Equal("DNS", finding.Category);
        Assert.Equal("Critical", finding.Severity);
        Assert.Equal("Health Checker", finding.Source);
        Assert.Equal("dc01.corp.local", finding.Target);
        Assert.Equal("DNS resolution failed", finding.Summary);
        Assert.Equal("SRV records missing for _ldap._tcp", finding.Details);
        Assert.Equal("nslookup returned NXDOMAIN", finding.Evidence);
        Assert.Equal("Recreate DNS zone records", finding.Remediation);
        Assert.Equal("Open", finding.Status);
        Assert.Equal(@"C:\logs\dns.log", finding.LogFilePath);
    }
}

public class TelemetrySnapshotTests
{
    [Fact]
    public void Empty_ReturnsSingletonWithNoServices()
    {
        var empty = TelemetrySnapshot.Empty;

        Assert.NotNull(empty);
        Assert.Empty(empty.Services);
        Assert.Empty(empty.Findings);
        Assert.Equal(0, empty.RunningServices);
        Assert.Equal(0, empty.TotalServices);
    }

    [Fact]
    public void RunningServices_CountsOnlyRunning()
    {
        var snapshot = new TelemetrySnapshot
        {
            Services = new List<TelemetryServiceMetric>
            {
                new() { Name = "DNS", Status = "Running" },
                new() { Name = "Netlogon", Status = "Running" },
                new() { Name = "W32Time", Status = "Stopped" },
                new() { Name = "ADWS", Status = "running" }, // case-insensitive
                new() { Name = "KDC", Status = "Paused" }
            }
        };

        Assert.Equal(3, snapshot.RunningServices);
        Assert.Equal(5, snapshot.TotalServices);
    }

    [Fact]
    public void RunningServices_EmptyList_ReturnsZero()
    {
        var snapshot = new TelemetrySnapshot();

        Assert.Equal(0, snapshot.RunningServices);
        Assert.Equal(0, snapshot.TotalServices);
    }

    [Fact]
    public void Findings_CanBeAdded()
    {
        var snapshot = new TelemetrySnapshot();
        snapshot.Findings.Add(new AdHealthFinding { Category = "Service", Severity = "High", Status = "Open" });

        Assert.Single(snapshot.Findings);
        Assert.Equal("Service", snapshot.Findings[0].Category);
    }
}

public class TelemetryServiceMetricTests
{
    [Fact]
    public void Constructor_SetsDefaults()
    {
        var metric = new TelemetryServiceMetric();

        Assert.Equal(string.Empty, metric.Name);
        Assert.Equal(string.Empty, metric.DisplayName);
        Assert.Equal(string.Empty, metric.Status);
        Assert.Equal(string.Empty, metric.StartType);
    }
}

public class AdInventorySnapshotTests
{
    [Fact]
    public void Empty_ReturnsSingletonWithDefaults()
    {
        var empty = AdInventorySnapshot.Empty;

        Assert.NotNull(empty);
        Assert.Equal("Unavailable", empty.ForestName);
        Assert.Equal("Unavailable", empty.DomainName);
        Assert.Equal("Unavailable", empty.DomainMode);
        Assert.Equal(0, empty.DomainControllerCount);
        Assert.Equal(0, empty.TrustCount);
        Assert.Equal(0, empty.OrganizationalUnitCount);
        Assert.Equal(0, empty.GroupPolicyCount);
        Assert.Equal(0, empty.UserCount);
        Assert.Equal(0, empty.ComputerCount);
        Assert.Empty(empty.PrivilegedGroupCounts);
        Assert.Empty(empty.Findings);
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var snapshot = new AdInventorySnapshot
        {
            ForestName = "corp.local",
            DomainName = "corp.local",
            DomainMode = "Windows2016Forest",
            DomainControllerCount = 3,
            TrustCount = 1,
            OrganizationalUnitCount = 25,
            GroupPolicyCount = 12,
            UserCount = 500,
            ComputerCount = 350,
            PrivilegedGroupCounts = new Dictionary<string, int>
            {
                ["Domain Admins"] = 5,
                ["Enterprise Admins"] = 2
            }
        };

        Assert.Equal("corp.local", snapshot.ForestName);
        Assert.Equal(3, snapshot.DomainControllerCount);
        Assert.Equal(500, snapshot.UserCount);
        Assert.Equal(2, snapshot.PrivilegedGroupCounts.Count);
        Assert.Equal(5, snapshot.PrivilegedGroupCounts["Domain Admins"]);
    }
}

public class AppStartupStateTests
{
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var settings = new PersistedAppSettings { DomainControllers = "dc01" };
        var snapshot = new DashboardSnapshot { HealthScore = 90 };
        var history = new List<TestHistoryEntry> { new() { Total = 5, Passed = 5 } };
        var tasks = new List<ScheduledTask> { new() { TaskName = "Test" } };

        var state = new AppStartupState(settings, snapshot, history, tasks);

        Assert.Same(settings, state.Settings);
        Assert.Same(snapshot, state.DashboardSnapshot);
        Assert.Same(history, state.History);
        Assert.Same(tasks, state.ScheduledTasks);
    }

    [Fact]
    public void Constructor_AllowsNullDashboardSnapshot()
    {
        var settings = new PersistedAppSettings();
        var history = new List<TestHistoryEntry>();
        var tasks = new List<ScheduledTask>();

        var state = new AppStartupState(settings, null, history, tasks);

        Assert.Null(state.DashboardSnapshot);
    }
}
