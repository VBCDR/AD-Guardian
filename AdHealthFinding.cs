namespace AdHealthMonitor;

public class AdHealthFinding
{
    public string Category { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public string Remediation { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string LogFilePath { get; set; } = string.Empty;
}
