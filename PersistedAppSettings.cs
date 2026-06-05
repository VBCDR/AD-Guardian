namespace AdHealthMonitor;

public sealed class PersistedAppSettings
{
    public string DomainControllers { get; set; } = string.Empty;
    public string RecipientEmail { get; set; } = string.Empty;
    public bool TestDnsCheck { get; set; } = false;
    public bool TestReplication { get; set; } = true;
    public bool TestTimeSkew { get; set; } = false;
    public bool TestLdapBind { get; set; } = false;
    public bool TestCertDhcp { get; set; } = false;
    public bool TestSmbLdapSigning { get; set; } = false;
    public bool SendEmailManual { get; set; } = true;
    public bool SendEmailScheduled { get; set; } = true;
}
