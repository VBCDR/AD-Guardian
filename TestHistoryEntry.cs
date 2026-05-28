using System;

namespace AdHealthMonitor;

public class TestHistoryEntry
{
    public DateTime RunDate { get; set; }
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public string Details { get; set; } = string.Empty;
    public string LogFilePath { get; set; } = string.Empty;
    public string TestType { get; set; } = string.Empty;
}
