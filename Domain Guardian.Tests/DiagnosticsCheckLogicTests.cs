using AdHealthMonitor;
using Xunit;

namespace Domain_Guardian.Tests;

public class DiagnosticsCheckLogicTests
{
    // ── EvaluateTimeSkewResult ───────────────────────────────────────────

    [Fact]
    public void TimeSkew_NormalOutput_WithOffset_Passes()
    {
        string output = @"2022DC01[128.0.2.191:123]:
    ICMP: 0ms delay
    NTP: +0.0000032s offset from 2022DC02
        RefID: 0x00000000
        Stratum: 4";

        Assert.True(MainWindow.EvaluateTimeSkewResult(output));
    }

    [Fact]
    public void TimeSkew_NormalOutput_WithRefIDOnly_Passes()
    {
        string output = @"2022DC01:
    RefID: 128.0.2.42
    Stratum: 3";

        Assert.True(MainWindow.EvaluateTimeSkewResult(output));
    }

    [Fact]
    public void TimeSkew_OutputWithFAILED_Fails()
    {
        string output = @"2022DC01:
    NTP: +0.0000032s offset from DC02
    FAILED: The time service is not synchronizing.";

        Assert.False(MainWindow.EvaluateTimeSkewResult(output));
    }

    [Fact]
    public void TimeSkew_OutputWithErrorCode_Fails()
    {
        string output = @"2022DC01:
    error code: 0x800705B4
    RefID: 0x00000000";

        Assert.False(MainWindow.EvaluateTimeSkewResult(output));
    }

    [Fact]
    public void TimeSkew_OutputWithLastError_Fails()
    {
        string output = @"2022DC01:
    last error: The RPC server is unavailable
    offset: +0.001s";

        Assert.False(MainWindow.EvaluateTimeSkewResult(output));
    }

    [Fact]
    public void TimeSkew_OutputWithHasAnError_Fails()
    {
        string output = @"The computer has not been configured to use a time source.
    This time service has an error.";

        Assert.False(MainWindow.EvaluateTimeSkewResult(output));
    }

    [Fact]
    public void TimeSkew_OutputWithNoOffsetOrRefID_Fails()
    {
        string output = @"The specified domain controller is not available.";

        Assert.False(MainWindow.EvaluateTimeSkewResult(output));
    }

    [Fact]
    public void TimeSkew_EmptyOutput_Fails()
    {
        Assert.False(MainWindow.EvaluateTimeSkewResult(""));
    }

    [Fact]
    public void TimeSkew_CaseInsensitive_FAILED_Fails()
    {
        string output = @"NTP: offset +0.001s
    Failed to query time source";

        Assert.False(MainWindow.EvaluateTimeSkewResult(output));
    }

    [Fact]
    public void TimeSkew_CaseInsensitive_offset_Passes()
    {
        string output = @"2022DC01:
    NTP: OFFSET +0.0000000s
    RefID: 0x00000000";

        Assert.True(MainWindow.EvaluateTimeSkewResult(output));
    }

    [Fact]
    public void TimeSkew_InfoWordErrorInOffset_Fails()
    {
        // w32tm output containing "FAILED" alongside offset — should fail
        string output = @"2022DC01:
    NTP: +0.0001s offset
    FAILED: source unreachable";

        Assert.False(MainWindow.EvaluateTimeSkewResult(output));
    }

    // ── EvaluateLdapBindResult ───────────────────────────────────────────

    [Fact]
    public void LdapBind_LDAP_OK_Passes()
    {
        string output = "LDAP_OK: DC=corp,DC=local";
        Assert.True(MainWindow.EvaluateLdapBindResult(output));
    }

    [Fact]
    public void LdapBind_LDAP_OK_WithPrefix_Passes()
    {
        string output = "VERBOSE: some extra output\nLDAP_OK: DC=office,DC=funasset,DC=com";
        Assert.True(MainWindow.EvaluateLdapBindResult(output));
    }

    [Fact]
    public void LdapBind_LDAP_FAIL_Fails()
    {
        string output = "LDAP_FAIL: The server is not operational.";
        Assert.False(MainWindow.EvaluateLdapBindResult(output));
    }

    [Fact]
    public void LdapBind_BothOkAndFail_Fails()
    {
        string output = "LDAP_OK: DC=corp\nLDAP_FAIL: Access denied";
        Assert.False(MainWindow.EvaluateLdapBindResult(output));
    }

    [Fact]
    public void LdapBind_NoMarker_Fails()
    {
        string output = "Some unexpected output from PowerShell";
        Assert.False(MainWindow.EvaluateLdapBindResult(output));
    }

    [Fact]
    public void LdapBind_EmptyOutput_Fails()
    {
        Assert.False(MainWindow.EvaluateLdapBindResult(""));
    }

    [Fact]
    public void LdapBind_CaseInsensitive_Passes()
    {
        string output = "ldap_ok: DC=corp,DC=local";
        Assert.True(MainWindow.EvaluateLdapBindResult(output));
    }

    [Fact]
    public void LdapBind_CaseInsensitive_Fail_Fails()
    {
        string output = "ldap_fail: connection refused";
        Assert.False(MainWindow.EvaluateLdapBindResult(output));
    }

    // ── BuildLdapBindMessage ─────────────────────────────────────────────

    [Fact]
    public void LdapBindMessage_Passed_ReturnsSuccess()
    {
        string msg = MainWindow.BuildLdapBindMessage("LDAP_OK: DC=corp", true);
        Assert.Equal("LDAP bind succeeded.", msg);
    }

    [Fact]
    public void LdapBindMessage_Failed_WithLDAP_FAIL_ReturnsErrorMessage()
    {
        string output = "LDAP_FAIL: The server is not operational.";
        string msg = MainWindow.BuildLdapBindMessage(output, false);
        Assert.Contains("The server is not operational.", msg);
    }

    [Fact]
    public void LdapBindMessage_Failed_NoMarker_ReturnsGeneric()
    {
        string msg = MainWindow.BuildLdapBindMessage("unexpected", false);
        Assert.Equal("LDAP bind failed.", msg);
    }

    // ── EvaluateSmbResult ────────────────────────────────────────────────

    [Fact]
    public void Smb_SMB_OK_Passes()
    {
        string output = "SMB_OK";
        Assert.True(MainWindow.EvaluateSmbResult(output));
    }

    [Fact]
    public void Smb_SMB_OK_WithWhitespace_Passes()
    {
        string output = "  SMB_OK  \r\n";
        Assert.True(MainWindow.EvaluateSmbResult(output));
    }

    [Fact]
    public void Smb_SMB_FAIL_Fails()
    {
        string output = "SMB_FAIL: Access is denied";
        Assert.False(MainWindow.EvaluateSmbResult(output));
    }

    [Fact]
    public void Smb_SMB_STATUS_Stopped_Fails()
    {
        string output = "SMB_STATUS=Stopped";
        Assert.False(MainWindow.EvaluateSmbResult(output));
    }

    [Fact]
    public void Smb_BothOkAndFail_Fails()
    {
        string output = "SMB_OK\nSMB_FAIL: timeout";
        Assert.False(MainWindow.EvaluateSmbResult(output));
    }

    [Fact]
    public void Smb_EmptyOutput_Fails()
    {
        Assert.False(MainWindow.EvaluateSmbResult(""));
    }

    [Fact]
    public void Smb_CaseInsensitive_Passes()
    {
        string output = "smb_ok";
        Assert.True(MainWindow.EvaluateSmbResult(output));
    }

    [Fact]
    public void Smb_CaseInsensitive_Fail_Fails()
    {
        string output = "smb_fail: service not found";
        Assert.False(MainWindow.EvaluateSmbResult(output));
    }

    [Fact]
    public void Smb_NoMarker_Fails()
    {
        string output = "Some random PowerShell output";
        Assert.False(MainWindow.EvaluateSmbResult(output));
    }

    // ── BuildSmbMessage ──────────────────────────────────────────────────

    [Fact]
    public void SmbMessage_Passed_ReturnsSuccess()
    {
        string msg = MainWindow.BuildSmbMessage("SMB_OK", true);
        Assert.Equal("Server service running.", msg);
    }

    [Fact]
    public void SmbMessage_Failed_WithSMB_FAIL_ReturnsErrorMessage()
    {
        string output = "SMB_FAIL: Access is denied";
        string msg = MainWindow.BuildSmbMessage(output, false);
        Assert.Contains("Access is denied", msg);
    }

    [Fact]
    public void SmbMessage_Failed_StatusStopped_ReturnsGeneric()
    {
        string output = "SMB_STATUS=Stopped";
        string msg = MainWindow.BuildSmbMessage(output, false);
        Assert.Equal("Server service not running.", msg);
    }
}
