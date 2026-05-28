using System.Collections.Generic;

namespace AdHealthMonitor;

public class TelemetrySnapshot
{
    public static TelemetrySnapshot Empty { get; } = new();

    public List<TelemetryServiceMetric> Services { get; set; } = new();
    public List<AdHealthFinding> Findings { get; set; } = new();
    public int RunningServices => Services.FindAll(service => service.Status.Equals("Running", System.StringComparison.OrdinalIgnoreCase)).Count;
    public int TotalServices => Services.Count;
}

public class TelemetryServiceMetric
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StartType { get; set; } = string.Empty;
}
