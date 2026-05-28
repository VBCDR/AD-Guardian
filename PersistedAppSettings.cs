namespace AdHealthMonitor;

public sealed class PersistedAppSettings
{
    public string DomainControllers { get; set; } = string.Empty;
    public string RecipientEmail { get; set; } = string.Empty;
    public bool TestDnsCheck { get; set; } = true;
    public bool TestReplication { get; set; } = true;
    public bool TestTimeSkew { get; set; } = true;
    public bool TestLdapBind { get; set; } = true;
    public bool TestCertDhcp { get; set; } = true;
    public bool TestSmbLdapSigning { get; set; } = true;
    public bool SendEmailManual { get; set; } = true;
    public bool SendEmailScheduled { get; set; } = true;
}
