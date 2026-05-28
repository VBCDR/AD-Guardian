using System;

namespace AdHealthMonitor;

public class ScheduledTask
{
    public string TaskName { get; set; } = string.Empty;
    public string DomainController { get; set; } = string.Empty;
    public string Frequency { get; set; } = string.Empty;
    public DateTime StartDate { get; set; }
    public string StartTime { get; set; } = string.Empty;

    public override string ToString()
    {
        return $"{TaskName} - {StartDate.ToShortDateString()} {StartTime} | DC(s): {DomainController} | Frequency: {Frequency}";
    }
}
