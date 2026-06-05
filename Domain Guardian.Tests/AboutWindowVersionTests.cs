using System;
using System.Reflection;
using Xunit;

namespace Domain_Guardian.Tests;

public class AboutWindowVersionTests
{
    // ── TryParseReleaseVersion ───────────────────────────────────────────

    [Fact]
    public void TryParseReleaseVersion_SimpleVersion_ReturnsParsed()
    {
        bool result = AboutWindow.TryParseReleaseVersion("2.0.7", out Version version);

        Assert.True(result);
        Assert.Equal(2, version.Major);
        Assert.Equal(0, version.Minor);
        Assert.Equal(7, version.Build);
        Assert.Equal(-1, version.Revision);
    }

    [Fact]
    public void TryParseReleaseVersion_VPrefix_StripsPrefix()
    {
        bool result = AboutWindow.TryParseReleaseVersion("v2.0.7", out Version version);

        Assert.True(result);
        Assert.Equal(2, version.Major);
        Assert.Equal(0, version.Minor);
        Assert.Equal(7, version.Build);
    }

    [Fact]
    public void TryParseReleaseVersion_WithPrereleaseSuffix_StripsSuffix()
    {
        bool result = AboutWindow.TryParseReleaseVersion("2.0.7-beta.1", out Version version);

        Assert.True(result);
        Assert.Equal(2, version.Major);
        Assert.Equal(0, version.Minor);
        Assert.Equal(7, version.Build);
    }

    [Fact]
    public void TryParseReleaseVersion_WithBuildMetadata_StripsMetadata()
    {
        bool result = AboutWindow.TryParseReleaseVersion("2.0.7+build123", out Version version);

        Assert.True(result);
        Assert.Equal(2, version.Major);
        Assert.Equal(0, version.Minor);
        Assert.Equal(7, version.Build);
    }

    [Fact]
    public void TryParseReleaseVersion_FourPartVersion_ParsesAllParts()
    {
        bool result = AboutWindow.TryParseReleaseVersion("2.0.7.0", out Version version);

        Assert.True(result);
        Assert.Equal(2, version.Major);
        Assert.Equal(0, version.Minor);
        Assert.Equal(7, version.Build);
        Assert.Equal(0, version.Revision);
    }

    [Fact]
    public void TryParseReleaseVersion_WithSpaces_Trims()
    {
        bool result = AboutWindow.TryParseReleaseVersion("  v2.0.7  ", out Version version);

        Assert.True(result);
        Assert.Equal(2, version.Major);
        Assert.Equal(0, version.Minor);
        Assert.Equal(7, version.Build);
    }

    [Fact]
    public void TryParseReleaseVersion_VPrefixWithSuffix_StripsBoth()
    {
        bool result = AboutWindow.TryParseReleaseVersion("v2.0.7-rc.2", out Version version);

        Assert.True(result);
        Assert.Equal(2, version.Major);
        Assert.Equal(0, version.Minor);
        Assert.Equal(7, version.Build);
    }

    [Fact]
    public void TryParseReleaseVersion_EmptyString_ReturnsFalse()
    {
        bool result = AboutWindow.TryParseReleaseVersion(string.Empty, out Version version);

        Assert.False(result);
        Assert.Equal(0, version.Major);
    }

    [Fact]
    public void TryParseReleaseVersion_Whitespace_ReturnsFalse()
    {
        bool result = AboutWindow.TryParseReleaseVersion("   ", out Version version);

        Assert.False(result);
        Assert.Equal(0, version.Major);
    }

    [Fact]
    public void TryParseReleaseVersion_InvalidString_ReturnsFalse()
    {
        bool result = AboutWindow.TryParseReleaseVersion("not-a-version", out Version version);

        Assert.False(result);
        Assert.Equal(0, version.Major);
    }

    [Fact]
    public void TryParseReleaseVersion_Null_ReturnsFalse()
    {
        bool result = AboutWindow.TryParseReleaseVersion(null!, out Version version);

        Assert.False(result);
        Assert.Equal(0, version.Major);
    }

    [Fact]
    public void TryParseReleaseVersion_OnlyV_ReturnsFalse()
    {
        bool result = AboutWindow.TryParseReleaseVersion("v", out Version version);

        Assert.False(result);
        Assert.Equal(0, version.Major);
    }

    [Fact]
    public void TryParseReleaseVersion_MajorMinor_ParsesCorrectly()
    {
        bool result = AboutWindow.TryParseReleaseVersion("2.0", out Version version);

        Assert.True(result);
        Assert.Equal(2, version.Major);
        Assert.Equal(0, version.Minor);
        Assert.Equal(-1, version.Build);
    }

    [Fact]
    public void TryParseReleaseVersion_VersionComparison_2_0_7_GreaterThan_2_0_6()
    {
        Assert.True(AboutWindow.TryParseReleaseVersion("2.0.7", out Version v207));
        Assert.True(AboutWindow.TryParseReleaseVersion("2.0.6", out Version v206));

        Assert.True(v207 > v206);
    }

    [Fact]
    public void TryParseReleaseVersion_VersionComparison_2_1_0_GreaterThan_2_0_99()
    {
        Assert.True(AboutWindow.TryParseReleaseVersion("2.1.0", out Version v210));
        Assert.True(AboutWindow.TryParseReleaseVersion("2.0.99", out Version v2099));

        Assert.True(v210 > v2099);
    }

    // ── AssemblyInformationalVersion ─────────────────────────────────────

    [Fact]
    public void Assembly_HasInformationalVersionAttribute()
    {
        var attribute = typeof(AboutWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        Assert.NotNull(attribute);
        Assert.False(string.IsNullOrWhiteSpace(attribute.InformationalVersion));
    }

    [Fact]
    public void Assembly_InformationalVersion_IsParseable()
    {
        var attribute = typeof(AboutWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        Assert.NotNull(attribute);
        bool parsed = AboutWindow.TryParseReleaseVersion(
            attribute.InformationalVersion, out Version version);

        Assert.True(parsed,
            $"AssemblyInformationalVersion '{attribute.InformationalVersion}' should be parseable as a version");
        Assert.True(version.Major >= 2,
            $"Version major should be >= 2, got {version.Major}");
    }

    [Fact]
    public void Assembly_InformationalVersion_MatchesAssemblyVersion()
    {
        var informationalAttr = typeof(AboutWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var assemblyVersion = typeof(AboutWindow).Assembly.GetName().Version;

        Assert.NotNull(informationalAttr);

        AboutWindow.TryParseReleaseVersion(
            informationalAttr.InformationalVersion,
            out Version informationalVersion);

        Assert.Equal(assemblyVersion.Major, informationalVersion.Major);
        Assert.Equal(assemblyVersion.Minor, informationalVersion.Minor);
    }

    // ── Window_Loaded display logic ──────────────────────────────────────

    [Fact]
    public void VersionDisplayFormat_VersionString_CorrectFormat()
    {
        // Simulates the Window_Loaded logic:
        //   VersionText.Text = $"v{informationalVersion}";
        //   VersionValueText.Text = informationalVersion;

        string informationalVersion = "2.0.7";
        string versionText = $"v{informationalVersion}";
        string versionValue = informationalVersion;

        Assert.Equal("v2.0.7", versionText);
        Assert.Equal("2.0.7", versionValue);
    }

    [Fact]
    public void VersionDisplayFormat_FallbackVersion_CorrectFormat()
    {
        // Simulates the fallback logic in Window_Loaded:
        //   Version v = Assembly.GetExecutingAssembly().GetName().Version
        //   VersionText.Text = $"v{v.Major}.{v.Minor}.{v.Build}"
        //   VersionValueText.Text = $"{v.Major}.{v.Minor}.{v.Build}"

        Version version = new(2, 0, 7);
        string versionText = $"v{version.Major}.{version.Minor}.{version.Build}";
        string versionValue = $"{version.Major}.{version.Minor}.{version.Build}";

        Assert.Equal("v2.0.7", versionText);
        Assert.Equal("2.0.7", versionValue);
    }
}
