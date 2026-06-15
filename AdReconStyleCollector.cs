using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AdHealthMonitor;

public class AdReconStyleCollector
{
    private static readonly TimeSpan PowerShellTimeout = TimeSpan.FromSeconds(20);

    public async Task<AdInventorySnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        const string script = """
$ErrorActionPreference = 'SilentlyContinue'
$hasAdModule = [bool](Get-Command Get-ADDomain -ErrorAction SilentlyContinue)
if (-not $hasAdModule) {
  [pscustomobject]@{
    HasAdModule = $false
    ForestName = 'Unavailable'
    DomainName = 'Unavailable'
    DomainMode = 'Unavailable'
    DomainControllerCount = 0
    TrustCount = 0
    OrganizationalUnitCount = 0
    GroupPolicyCount = 0
    UserCount = 0
    ComputerCount = 0
    PrivilegedGroups = @()
  } | ConvertTo-Json -Compress -Depth 5
  return
}

$privilegedGroups = foreach ($name in @('Domain Admins','Enterprise Admins','Schema Admins','Administrators','DNSAdmins','Backup Operators')) {
  $count = -1
  try {
    $count = @(Get-ADGroupMember -Identity $name -Recursive -ErrorAction Stop).Count
  } catch {}
  [pscustomobject]@{ Name = $name; Count = $count }
}

$groupPolicyCount = 0
if (Get-Command Get-GPO -ErrorAction SilentlyContinue) {
  try { $groupPolicyCount = @(Get-GPO -All -ErrorAction Stop).Count } catch {}
}

[pscustomobject]@{
  HasAdModule = $true
  ForestName = (Get-ADForest).Name
  DomainName = (Get-ADDomain).DNSRoot
  DomainMode = [string](Get-ADDomain).DomainMode
  DomainControllerCount = @(Get-ADDomainController -Filter *).Count
  TrustCount = @(Get-ADTrust -Filter *).Count
  OrganizationalUnitCount = @(Get-ADOrganizationalUnit -Filter *).Count
  GroupPolicyCount = $groupPolicyCount
  UserCount = (Get-ADUser -LDAPFilter '(objectClass=user)' -ResultSetSize $null | Measure-Object).Count
  ComputerCount = (Get-ADComputer -Filter * -ResultSetSize $null | Measure-Object).Count
  PrivilegedGroups = @($privilegedGroups)
} | ConvertTo-Json -Compress -Depth 5
""";

        string json = await RunPowerShellAsync(script, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return BuildFailureSnapshot("The AD inventory collector returned no data.");
        }

        try
        {
            JObject root = JObject.Parse(json);
            if (root.Value<bool?>("HasAdModule") != true)
            {
                return BuildFailureSnapshot("RSAT Active Directory PowerShell tools are not available. Install the AD module to unlock inventory breadth.");
            }

            AdInventorySnapshot snapshot = new()
            {
                ForestName = root.Value<string>("ForestName") ?? "Unavailable",
                DomainName = root.Value<string>("DomainName") ?? "Unavailable",
                DomainMode = root.Value<string>("DomainMode") ?? "Unavailable",
                DomainControllerCount = root.Value<int?>("DomainControllerCount") ?? 0,
                TrustCount = root.Value<int?>("TrustCount") ?? 0,
                OrganizationalUnitCount = root.Value<int?>("OrganizationalUnitCount") ?? 0,
                GroupPolicyCount = root.Value<int?>("GroupPolicyCount") ?? 0,
                UserCount = root.Value<int?>("UserCount") ?? 0,
                ComputerCount = root.Value<int?>("ComputerCount") ?? 0
            };

            if (root["PrivilegedGroups"] is JArray groups)
            {
                foreach (JToken group in groups)
                {
                    string name = group.Value<string>("Name") ?? string.Empty;
                    int count = group.Value<int?>("Count") ?? -1;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    snapshot.PrivilegedGroupCounts[name] = count;
                    if (count > 5)
                    {
                        snapshot.Findings.Add(new AdHealthFinding
                        {
                            Category = "Privilege",
                            Severity = "High",
                            Source = "ADRecon-style Collector",
                            Target = name,
                            Summary = $"{name} contains {count} members.",
                            Details = "Privileged groups with broad membership increase the blast radius of account compromise.",
                            Evidence = $"Member count: {count}",
                            Remediation = "Review delegated rights and reduce standing privileged group membership.",
                            Status = "Open"
                        });
                    }
                }
            }

            snapshot.Findings.Add(new AdHealthFinding
            {
                Category = "Infrastructure",
                Severity = "Info",
                Source = "ADRecon-style Collector",
                Target = snapshot.DomainName,
                Summary = $"Inventory collected for forest {snapshot.ForestName}.",
                Details = $"Domain controllers: {snapshot.DomainControllerCount}, trusts: {snapshot.TrustCount}, OUs: {snapshot.OrganizationalUnitCount}, GPOs: {snapshot.GroupPolicyCount}.",
                Evidence = $"Domain mode: {snapshot.DomainMode}",
                Remediation = "Use this inventory as the baseline for trend and drift analysis.",
                Status = "Collected"
            });

            return snapshot;
        }
        catch (Exception ex)
        {
            return BuildFailureSnapshot("Failed to parse AD inventory output: " + ex.Message);
        }
    }

    private static AdInventorySnapshot BuildFailureSnapshot(string message)
    {
        return new AdInventorySnapshot
        {
            Findings = new List<AdHealthFinding>
            {
                new()
                {
                    Category = "Infrastructure",
                    Severity = "Medium",
                    Source = "ADRecon-style Collector",
                    Target = "Active Directory inventory",
                    Summary = "Inventory breadth is unavailable.",
                    Details = message,
                    Evidence = message,
                    Remediation = "Install RSAT Active Directory tools and Group Policy tools on the operator workstation.",
                    Status = "Unavailable"
                }
            }
        };
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
            throw new TimeoutException($"PowerShell inventory collection exceeded {PowerShellTimeout.TotalSeconds:0} seconds.");
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
