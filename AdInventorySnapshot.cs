using System.Collections.Generic;

namespace AdHealthMonitor;

public class AdInventorySnapshot
{
    public static AdInventorySnapshot Empty { get; } = new();

    public string ForestName { get; set; } = "Unavailable";
    public string DomainName { get; set; } = "Unavailable";
    public string DomainMode { get; set; } = "Unavailable";
    public int DomainControllerCount { get; set; }
    public int TrustCount { get; set; }
    public int OrganizationalUnitCount { get; set; }
    public int GroupPolicyCount { get; set; }
    public int UserCount { get; set; }
    public int ComputerCount { get; set; }
    public Dictionary<string, int> PrivilegedGroupCounts { get; set; } = new();
    public List<AdHealthFinding> Findings { get; set; } = new();
}
