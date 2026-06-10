using System;
using System.Collections.Generic;
using System.Linq;
using AdHealthMonitor;
using Xunit;

namespace Domain_Guardian.Tests;

/// <summary>
/// Tests for the Findings tab logic: category inference, severity scoring,
/// remediation suggestions, security classification, and deduplication.
/// </summary>
public class FindingsLogicTests
{
    // ── InferCategory tests ──────────────────────────────────────────────

    [Theory]
    [InlineData("DNS Resolution", "DNS")]
    [InlineData("dns check", "DNS")]
    [InlineData("DnsEvent", "DNS")]
    public void InferCategory_DnsServices_ReturnsDNS(string service, string expected)
    {
        Assert.Equal(expected, MainWindow.InferCategory(service));
    }

    [Theory]
    [InlineData("Replications", "Replication")]
    [InlineData("replication check", "Replication")]
    [InlineData("Replication latency", "Replication")]
    public void InferCategory_ReplicationServices_ReturnsReplication(string service, string expected)
    {
        Assert.Equal(expected, MainWindow.InferCategory(service));
    }

    [Fact]
    public void InferCategory_DFSREvent_ReturnsInfrastructure()
    {
        // "DFSREvent" = D-F-S-R-E-v-e-n-t — does NOT contain "Rep" as substring
        // It falls through all checks to Infrastructure
        Assert.Equal("Infrastructure", MainWindow.InferCategory("DFSREvent"));
    }

    [Fact]
    public void InferCategory_FrsEvent_ReturnsSysVol()
    {
        // "FrsEvent" contains "Frs" → SYSVOL ("Frs" is checked before Rep in the code)
        Assert.Equal("SYSVOL", MainWindow.InferCategory("FrsEvent"));
    }

    [Theory]
    [InlineData("NetLogons", "Domain Services")]
    [InlineData("Advertising", "Domain Services")]
    [InlineData("Locator", "Domain Services")]
    public void InferCategory_DomainServices_ReturnsDomainServices(string service, string expected)
    {
        Assert.Equal(expected, MainWindow.InferCategory(service));
    }

    [Fact]
    public void InferCategory_SysVolCheck_ReturnsSysVol()
    {
        Assert.Equal("SYSVOL", MainWindow.InferCategory("SysVolCheck"));
    }

    [Theory]
    [InlineData("MachineAccount", "Configuration")]
    [InlineData("Services", "Configuration")]
    public void InferCategory_ConfigurationServices_ReturnsConfiguration(string service, string expected)
    {
        Assert.Equal(expected, MainWindow.InferCategory(service));
    }

    [Theory]
    [InlineData("Connectivity", "Infrastructure")]
    [InlineData("KccEvent", "Infrastructure")]
    [InlineData("RidManager", "Infrastructure")]
    [InlineData("NCSecDesc", "Infrastructure")]
    [InlineData("SystemLog", "Infrastructure")]
    public void InferCategory_InfrastructureServices_ReturnsInfrastructure(string service, string expected)
    {
        Assert.Equal(expected, MainWindow.InferCategory(service));
    }

    // ── InferSeverity tests ──────────────────────────────────────────────

    [Fact]
    public void InferSeverity_PassResult_ReturnsInfo()
    {
        var result = new TestResult { Service = "Connectivity", Server = "DC01", Result = "PASS" };
        Assert.Equal("Info", MainWindow.InferSeverity(result));
    }

    [Theory]
    [InlineData("DNS Resolution", "FAIL", "Critical")]
    [InlineData("Replications", "FAIL", "Critical")]
    [InlineData("Advertising", "FAIL", "Critical")]
    public void InferSeverity_CriticalServices_ReturnsCritical(string service, string resultStr, string expected)
    {
        var result = new TestResult { Service = service, Server = "DC01", Result = resultStr };
        Assert.Equal(expected, MainWindow.InferSeverity(result));
    }

    [Fact]
    public void InferSeverity_DFSREvent_ReturnsMedium()
    {
        // "DFSREvent" does not contain "Rep" as substring, so falls to Medium
        var result = new TestResult { Service = "DFSREvent", Server = "DC01", Result = "FAIL" };
        Assert.Equal("Medium", MainWindow.InferSeverity(result));
    }

    [Theory]
    [InlineData("NetLogons", "FAIL", "High")]
    [InlineData("Services", "FAIL", "High")]
    [InlineData("SystemLog", "FAIL", "High")]
    public void InferSeverity_HighServices_ReturnsHigh(string service, string resultStr, string expected)
    {
        var result = new TestResult { Service = service, Server = "DC01", Result = resultStr };
        Assert.Equal(expected, MainWindow.InferSeverity(result));
    }

    [Theory]
    [InlineData("Connectivity", "FAIL", "Medium")]
    [InlineData("KccEvent", "FAIL", "Medium")]
    [InlineData("RidManager", "FAIL", "Medium")]
    public void InferSeverity_OtherFailures_ReturnsMedium(string service, string resultStr, string expected)
    {
        var result = new TestResult { Service = service, Server = "DC01", Result = resultStr };
        Assert.Equal(expected, MainWindow.InferSeverity(result));
    }

    // ── BuildFindingSummary tests ────────────────────────────────────────

    [Fact]
    public void BuildFindingSummary_PassResult_ReportsPassed()
    {
        var result = new TestResult { Service = "Connectivity", Server = "DC01", Result = "PASS" };
        string summary = MainWindow.BuildFindingSummary(result);

        Assert.Contains("Connectivity", summary);
        Assert.Contains("passed", summary);
        Assert.Contains("DC01", summary);
    }

    [Fact]
    public void BuildFindingSummary_FailResult_ReportsFailed()
    {
        var result = new TestResult { Service = "DNS Resolution", Server = "DC01", Result = "FAIL" };
        string summary = MainWindow.BuildFindingSummary(result);

        Assert.Contains("DNS Resolution", summary);
        Assert.Contains("failed", summary);
        Assert.Contains("DC01", summary);
    }

    // ── SuggestRemediation tests ─────────────────────────────────────────

    [Fact]
    public void SuggestRemediation_PassResult_NoActionRequired()
    {
        var result = new TestResult { Service = "Connectivity", Server = "DC01", Result = "PASS" };
        Assert.Equal("No action required.", MainWindow.SuggestRemediation(result));
    }

    [Theory]
    [InlineData("DNS Resolution", "DNS client settings")]
    [InlineData("dns check", "DNS client settings")]
    public void SuggestRemediation_DnsService_SuggestsDnsReview(string service, string expectedSnippet)
    {
        var result = new TestResult { Service = service, Server = "DC01", Result = "FAIL" };
        string remediation = MainWindow.SuggestRemediation(result);
        Assert.Contains(expectedSnippet, remediation, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Replications", "replication links")]
    [InlineData("ReplSummary", "replication links")]
    public void SuggestRemediation_ReplicationService_SuggestsReplicationReview(string service, string expectedSnippet)
    {
        var result = new TestResult { Service = service, Server = "DC01", Result = "FAIL" };
        string remediation = MainWindow.SuggestRemediation(result);
        Assert.Contains(expectedSnippet, remediation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SuggestRemediation_DFSREvent_SuggestsGenericLogReview()
    {
        // "DFSREvent" does not contain "Rep" so SuggestRemediation falls to generic
        var result = new TestResult { Service = "DFSREvent", Server = "DC01", Result = "FAIL" };
        string remediation = MainWindow.SuggestRemediation(result);
        Assert.Contains("log", remediation, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("NetLogons", "Netlogon")]
    [InlineData("Advertising", "Netlogon")]
    public void SuggestRemediation_DomainServices_SuggestsServiceCheck(string service, string expectedSnippet)
    {
        var result = new TestResult { Service = service, Server = "DC01", Result = "FAIL" };
        string remediation = MainWindow.SuggestRemediation(result);
        Assert.Contains(expectedSnippet, remediation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SuggestRemediation_GenericFailure_SuggestsLogReview()
    {
        var result = new TestResult { Service = "Connectivity", Server = "DC01", Result = "FAIL" };
        string remediation = MainWindow.SuggestRemediation(result);
        Assert.Contains("log", remediation, StringComparison.OrdinalIgnoreCase);
    }

    // ── SeverityRank tests ───────────────────────────────────────────────

    [Theory]
    [InlineData("Critical", 4)]
    [InlineData("High", 3)]
    [InlineData("Medium", 2)]
    [InlineData("Low", 1)]
    [InlineData("Info", 0)]
    [InlineData("Unknown", 0)]
    [InlineData("", 0)]
    public void SeverityRank_ReturnsExpectedRank(string severity, int expected)
    {
        Assert.Equal(expected, MainWindow.SeverityRank(severity));
    }

    [Fact]
    public void SeverityRank_CriticalIsHigherThanHigh()
    {
        Assert.True(MainWindow.SeverityRank("Critical") > MainWindow.SeverityRank("High"));
        Assert.True(MainWindow.SeverityRank("High") > MainWindow.SeverityRank("Medium"));
        Assert.True(MainWindow.SeverityRank("Medium") > MainWindow.SeverityRank("Low"));
    }

    // ── IsSecurityFinding tests ──────────────────────────────────────────

    [Theory]
    [InlineData("Privilege", "Medium", true)]
    [InlineData("Telemetry", "Low", true)]
    [InlineData("DNS", "Critical", true)]
    [InlineData("DNS", "High", true)]
    [InlineData("Infrastructure", "Medium", false)]
    [InlineData("DNS", "Medium", false)]
    [InlineData("Configuration", "Low", false)]
    public void IsSecurityFinding_ClassifiesCorrectly(string category, string severity, bool expected)
    {
        var finding = new AdHealthFinding { Category = category, Severity = severity };
        Assert.Equal(expected, MainWindow.IsSecurityFinding(finding));
    }

    // ── BuildFindingKey tests ────────────────────────────────────────────

    [Fact]
    public void BuildFindingKey_IncludesAllFields()
    {
        var finding = new AdHealthFinding
        {
            Category = "DNS",
            Severity = "Critical",
            Source = "DCDiag",
            Target = "DC01 - DNS Resolution",
            Summary = "DNS failed on DC01",
            Status = "FAIL",
            LogFilePath = "test.log"
        };

        string key = MainWindow.BuildFindingKey(finding);

        Assert.Contains("DNS", key);
        Assert.Contains("Critical", key);
        Assert.Contains("DCDiag", key);
        Assert.Contains("DC01", key);
        Assert.Contains("FAIL", key);
        Assert.Contains("test.log", key);
    }

    [Fact]
    public void BuildFindingKey_HandlesNullFields()
    {
        var finding = new AdHealthFinding
        {
            Category = null!,
            Severity = null!,
            Source = null!,
            Target = null!,
            Summary = null!,
            Status = null!,
            LogFilePath = null!
        };

        // Should not throw
        string key = MainWindow.BuildFindingKey(finding);
        Assert.NotNull(key);
    }

    [Fact]
    public void BuildFindingKey_DifferentFindings_ProduceDifferentKeys()
    {
        var f1 = new AdHealthFinding { Category = "DNS", Severity = "Critical", Summary = "DNS failed" };
        var f2 = new AdHealthFinding { Category = "DNS", Severity = "Critical", Summary = "DNS OK" };

        Assert.NotEqual(MainWindow.BuildFindingKey(f1), MainWindow.BuildFindingKey(f2));
    }

    // ── RebuildFindings dedup + sort integration tests ───────────────────

    /// <summary>
    /// Simulates the core RebuildFindings logic: convert failed TestResults to findings,
    /// deduplicate by key, sort by severity then category then target.
    /// This tests the same algorithm without needing a MainWindow instance.
    /// </summary>
    private static List<AdHealthFinding> SimulateRebuildFindings(
        List<TestResult> results,
        List<AdHealthFinding>? inventoryFindings = null,
        List<AdHealthFinding>? telemetryFindings = null)
    {
        List<AdHealthFinding> allFindings = new();

        foreach (TestResult result in results)
        {
            if (result.Result.Equals("PASS", StringComparison.OrdinalIgnoreCase))
                continue;

            allFindings.Add(new AdHealthFinding
            {
                Category = MainWindow.InferCategory(result.Service),
                Severity = MainWindow.InferSeverity(result),
                Source = "DCDiag / Repadmin",
                Target = string.IsNullOrWhiteSpace(result.Server)
                    ? result.Service
                    : $"{result.Server} - {result.Service}",
                Summary = MainWindow.BuildFindingSummary(result),
                Details = result.Message,
                Evidence = result.Message,
                Remediation = MainWindow.SuggestRemediation(result),
                Status = result.Result,
                LogFilePath = result.LogFilePath
            });
        }

        if (inventoryFindings != null)
            allFindings.AddRange(inventoryFindings);
        if (telemetryFindings != null)
            allFindings.AddRange(telemetryFindings);

        // Deduplicate and sort (same logic as RebuildFindings)
        Dictionary<string, AdHealthFinding> dedup = new(allFindings.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < allFindings.Count; i++)
        {
            string key = MainWindow.BuildFindingKey(allFindings[i]);
            dedup.TryAdd(key, allFindings[i]);
        }

        List<AdHealthFinding> deduplicated = new(dedup.Values);
        deduplicated.Sort((a, b) =>
        {
            int cmp = MainWindow.SeverityRank(b.Severity).CompareTo(MainWindow.SeverityRank(a.Severity));
            if (cmp != 0) return cmp;
            cmp = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
            cmp = string.Compare(a.Target, b.Target, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
            return string.Compare(a.Summary, b.Summary, StringComparison.OrdinalIgnoreCase);
        });

        return deduplicated;
    }

    [Fact]
    public void RebuildFindings_PassResults_AreExcluded()
    {
        var results = new List<TestResult>
        {
            new() { Service = "Connectivity", Server = "DC01", Result = "PASS" },
            new() { Service = "Advertising", Server = "DC01", Result = "PASS" },
        };

        var findings = SimulateRebuildFindings(results);
        Assert.Empty(findings);
    }

    [Fact]
    public void RebuildFindings_FailResults_ProduceFindings()
    {
        var results = new List<TestResult>
        {
            new() { Service = "Connectivity", Server = "DC01", Result = "PASS" },
            new() { Service = "Replications", Server = "DC01", Result = "FAIL", Message = "Replication backlog" },
        };

        var findings = SimulateRebuildFindings(results);

        Assert.Single(findings);
        Assert.Equal("Replication", findings[0].Category);
        Assert.Equal("Critical", findings[0].Severity);
        Assert.Contains("Replications", findings[0].Target);
        Assert.Contains("DC01", findings[0].Target);
    }

    [Fact]
    public void RebuildFindings_DeduplicatesSameFinding()
    {
        var results = new List<TestResult>
        {
            new() { Service = "DFSREvent", Server = "DC01", Result = "FAIL", Message = "same", LogFilePath = "test.log" },
            new() { Service = "DFSREvent", Server = "DC01", Result = "FAIL", Message = "same", LogFilePath = "test.log" },
        };

        var findings = SimulateRebuildFindings(results);
        Assert.Single(findings); // Deduplicated
    }

    [Fact]
    public void RebuildFindings_SortsBySeverityDescending()
    {
        var results = new List<TestResult>
        {
            new() { Service = "Connectivity", Server = "DC01", Result = "FAIL", Message = "conn fail" },
            new() { Service = "DNS Resolution", Server = "DC01", Result = "FAIL", Message = "dns fail" },
            new() { Service = "NetLogons", Server = "DC01", Result = "FAIL", Message = "netlogons fail" },
        };

        var findings = SimulateRebuildFindings(results);

        Assert.Equal(3, findings.Count);
        // Critical (DNS) first, then High (NetLogons), then Medium (Connectivity)
        Assert.Equal("Critical", findings[0].Severity);
        Assert.Equal("High", findings[1].Severity);
        Assert.Equal("Medium", findings[2].Severity);
    }

    [Fact]
    public void RebuildFindings_SortsByCategoryThenTargetWithinSameSeverity()
    {
        var results = new List<TestResult>
        {
            new() { Service = "NetLogons", Server = "DC02", Result = "FAIL", Message = "fail" },
            new() { Service = "NetLogons", Server = "DC01", Result = "FAIL", Message = "fail" },
            new() { Service = "Services", Server = "DC01", Result = "FAIL", Message = "fail" },
        };

        var findings = SimulateRebuildFindings(results);

        Assert.Equal(3, findings.Count);
        // All High severity. Sort by category alphabetically, then target
        // Configuration (MachineAccount/Services) before Domain Services (NetLogons)
        // Actually: "Configuration" < "Domain Services" alphabetically
        Assert.Equal("Configuration", findings[0].Category);
        Assert.Equal("Domain Services", findings[1].Category);
        Assert.Equal("Domain Services", findings[2].Category);
        // Within Domain Services, DC01 < DC02
        Assert.Contains("DC01", findings[1].Target);
        Assert.Contains("DC02", findings[2].Target);
    }

    [Fact]
    public void RebuildFindings_IncludesInventoryAndTelemetryFindings()
    {
        var results = new List<TestResult>
        {
            new() { Service = "Replications", Server = "DC01", Result = "FAIL", Message = "fail" },
        };

        var inventoryFindings = new List<AdHealthFinding>
        {
            new() { Category = "Privilege", Severity = "High", Source = "Inventory", Target = "Domain Admins", Summary = "Too many admins" },
        };

        var telemetryFindings = new List<AdHealthFinding>
        {
            new() { Category = "Telemetry", Severity = "Critical", Source = "Telemetry", Target = "W32Time", Summary = "Time service stopped" },
        };

        var findings = SimulateRebuildFindings(results, inventoryFindings, telemetryFindings);

        Assert.Equal(3, findings.Count);
        // Replications→Critical, Telemetry→Critical (both Critical, sorted by category)
        // Then Privilege→High
        Assert.Equal("Critical", findings[0].Severity);
        Assert.Equal("Critical", findings[1].Severity);
        Assert.Equal("High", findings[2].Severity);
    }

    [Fact]
    public void RebuildFindings_MultiDC_GeneratesCorrectFindings()
    {
        var results = new List<TestResult>
        {
            new() { Service = "Connectivity", Server = "DC01", Result = "PASS" },
            new() { Service = "Connectivity", Server = "DC02", Result = "FAIL", Message = "RPC unavailable" },
            new() { Service = "Replications", Server = "DC01", Result = "FAIL", Message = "Replication backlog" },
            new() { Service = "Replications", Server = "DC02", Result = "PASS" },
        };

        var findings = SimulateRebuildFindings(results);

        Assert.Equal(2, findings.Count);
        // Critical (Replications/DC01) first, then Medium (Connectivity/DC02)
        Assert.Equal("Critical", findings[0].Severity);
        Assert.Contains("DC01", findings[0].Target);
        Assert.Equal("Medium", findings[1].Severity);
        Assert.Contains("DC02", findings[1].Target);
    }

    [Fact]
    public void RebuildFindings_SetsRemediationForEachFinding()
    {
        var results = new List<TestResult>
        {
            new() { Service = "DNS Resolution", Server = "DC01", Result = "FAIL", Message = "DNS failed" },
            new() { Service = "Replications", Server = "DC01", Result = "FAIL", Message = "Repl failed" },
            new() { Service = "Connectivity", Server = "DC01", Result = "FAIL", Message = "Conn failed" },
        };

        var findings = SimulateRebuildFindings(results);

        Assert.All(findings, f => Assert.NotEmpty(f.Remediation));
        // DNS finding should mention DNS
        var dnsFinding = findings.First(f => f.Category == "DNS");
        Assert.Contains("DNS", dnsFinding.Remediation, StringComparison.OrdinalIgnoreCase);
        // Replication finding should mention replication
        var replFinding = findings.First(f => f.Category == "Replication");
        Assert.Contains("replication", replFinding.Remediation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RebuildFindings_EmptyResults_ProducesEmpty()
    {
        var findings = SimulateRebuildFindings(new List<TestResult>());
        Assert.Empty(findings);
    }

    [Fact]
    public void RebuildFindings_AllPass_ProducesEmpty()
    {
        var results = new List<TestResult>
        {
            new() { Service = "Connectivity", Server = "DC01", Result = "PASS" },
            new() { Service = "DNS Resolution", Server = "DC02", Result = "PASS" },
        };

        var findings = SimulateRebuildFindings(results);
        Assert.Empty(findings);
    }

    // ── Full severity + category pipeline tests ──────────────────────────

    [Fact]
    public void FullPipeline_DnsFailure_ProducesCriticalDnsFinding()
    {
        var result = new TestResult { Service = "DNS Resolution", Server = "DC01", Result = "FAIL", Message = "nslookup failed" };

        string category = MainWindow.InferCategory(result.Service);
        string severity = MainWindow.InferSeverity(result);
        string summary = MainWindow.BuildFindingSummary(result);
        string remediation = MainWindow.SuggestRemediation(result);

        Assert.Equal("DNS", category);
        Assert.Equal("Critical", severity);
        Assert.Contains("DNS Resolution", summary);
        Assert.Contains("failed", summary);
        Assert.Contains("DNS", remediation);
    }

    [Fact]
    public void FullPipeline_ConnectivityPass_ProducesInfoInfrastructureFinding()
    {
        var result = new TestResult { Service = "Connectivity", Server = "DC01", Result = "PASS" };

        string category = MainWindow.InferCategory(result.Service);
        string severity = MainWindow.InferSeverity(result);
        string summary = MainWindow.BuildFindingSummary(result);
        string remediation = MainWindow.SuggestRemediation(result);

        Assert.Equal("Infrastructure", category);
        Assert.Equal("Info", severity);
        Assert.Contains("passed", summary);
        Assert.Equal("No action required.", remediation);
    }

    [Fact]
    public void FullPipeline_SecurityFindingClassification_WorksCorrectly()
    {
        // A DNS Critical failure should be classified as a security finding
        var dnsFinding = new AdHealthFinding { Category = "DNS", Severity = "Critical" };
        Assert.True(MainWindow.IsSecurityFinding(dnsFinding));

        // A Medium Infrastructure finding should NOT be a security finding
        var infraFinding = new AdHealthFinding { Category = "Infrastructure", Severity = "Medium" };
        Assert.False(MainWindow.IsSecurityFinding(infraFinding));

        // Privilege category is always a security finding regardless of severity
        var privFinding = new AdHealthFinding { Category = "Privilege", Severity = "Low" };
        Assert.True(MainWindow.IsSecurityFinding(privFinding));
    }
}
