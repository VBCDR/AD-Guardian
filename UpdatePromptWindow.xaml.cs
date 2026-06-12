using System;
using System.Reflection;
using System.Windows;

namespace Domain_Guardian;

public partial class UpdatePromptWindow : Window
{
    private const string UnknownVersion = "unknown";

    public bool UpdateConfirmed { get; private set; }

    public UpdatePromptWindow(Version latestVersion, Version currentVersion)
    {
        InitializeComponent();

        VersionInfoText.Text = $"v{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build} \u2192 v{latestVersion.Major}.{latestVersion.Minor}.{latestVersion.Build}";
        MessageText.Text = "A new version of AD Guardian is available. The latest installer will be downloaded and launched automatically.";
        string buildConfig = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? UnknownVersion;
        InstalledVersionText.Text = $"Installed binary: {GetInstalledFileVersion()} (build: {buildConfig})";
    }

    private static string GetInstalledFileVersion()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrWhiteSpace(fileVersion))
        {
            return fileVersion;
        }

        string? informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informationalVersion) ? UnknownVersion : informationalVersion;
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateConfirmed = true;
        DialogResult = true;
        Close();
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateConfirmed = false;
        DialogResult = false;
        Close();
    }
}
