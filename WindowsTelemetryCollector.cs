using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AdHealthMonitor;

public class WindowsTelemetryCollector
{
    private static readonly TimeSpan PowerShellTimeout = TimeSpan.FromSeconds(20);

    public async Task<TelemetrySnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        const string script = """
$services = @('NTDS','DNS','DFSR','Netlogon','KDC','W32Time')
@(
  foreach ($serviceName in $services) {
    try {
      $service = Get-Service -Name $serviceName -ErrorAction Stop
      [pscustomobject]@{
        Name = $service.Name
        DisplayName = $service.DisplayName
        Status = [string]$service.Status
        StartType = [string]$service.StartType
      }
    } catch {
      [pscustomobject]@{
        Name = $serviceName
        DisplayName = $serviceName
        Status = 'Missing'
        StartType = 'Unknown'
      }
    }
  }
) | ConvertTo-Json -Compress -Depth 4
""";

        string json = await RunPowerShellAsync(script, cancellationToken);
        TelemetrySnapshot snapshot = new();

        try
        {
            JArray services = JArray.Parse(json);
            foreach (JToken service in services)
            {
                TelemetryServiceMetric metric = new()
                {
                    Name = service.Value<string>("Name") ?? string.Empty,
                    DisplayName = service.Value<string>("DisplayName") ?? string.Empty,
                    Status = service.Value<string>("Status") ?? string.Empty,
                    StartType = service.Value<string>("StartType") ?? string.Empty
                };

                snapshot.Services.Add(metric);
                if (!metric.Status.Equals("Running", StringComparison.OrdinalIgnoreCase))
                {
                    snapshot.Findings.Add(new AdHealthFinding
                    {
                        Category = "Telemetry",
                        Severity = metric.Status.Equals("Missing", StringComparison.OrdinalIgnoreCase) ? "High" : "Critical",
                        Source = "Windows Telemetry",
                        Target = metric.DisplayName,
                        Summary = $"{metric.DisplayName} is {metric.Status}.",
                        Details = $"Service {metric.Name} reported status {metric.Status} with start type {metric.StartType}.",
                        Evidence = $"{metric.Name}: {metric.Status}",
                        Remediation = "Review the service state on the domain controller and resolve dependency or startup issues.",
                        Status = "Open"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            snapshot.Findings.Add(new AdHealthFinding
            {
                Category = "Telemetry",
                Severity = "Medium",
                Source = "Windows Telemetry",
                Target = "Service telemetry",
                Summary = "Service telemetry could not be collected.",
                Details = ex.Message,
                Evidence = json,
                Remediation = "Validate local PowerShell access and Windows service query permissions.",
                Status = "Unavailable"
            });
        }

        return snapshot;
    }

    private static async Task<string> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(PowerShellTimeout);
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            // Use -EncodedCommand (Base64-encoded UTF-16LE) so the script text never appears
            // on the command line. This avoids escaping headaches and closes the door on
            // shell metacharacter injection from PowerShell syntax inside the script.
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        try
        {
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            string output = await outputTask.ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(output) ? error : output.Trim();
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryTerminateProcess(process);
            throw new TimeoutException($"PowerShell telemetry collection exceeded {PowerShellTimeout.TotalSeconds:0} seconds.");
        }
        catch (OperationCanceledException)
        {
            TryTerminateProcess(process);
            throw;
        }
    }

    private static void TryTerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
        }
    }
}
