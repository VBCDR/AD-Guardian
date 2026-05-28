using System;

namespace AdHealthMonitor;

public sealed class DashboardSnapshot
{
    public DateTime CapturedAtUtc { get; set; }
    public int HealthScore { get; set; }
    public int CriticalFindings { get; set; }
    public int PassingTests { get; set; }
    public int ConfiguredDomainControllers { get; set; }
    public int TotalRuns { get; set; }
    public string LastRunSummary { get; set; } = string.Empty;
    public int FindingsCriticalCount { get; set; }
    public int FindingsHighCount { get; set; }
    public int FindingsMediumCount { get; set; }
    public int FindingsLowCount { get; set; }
    public int LastRunPassed { get; set; }
    public int LastRunFailed { get; set; }
    public int LastRunTotal { get; set; }
}
