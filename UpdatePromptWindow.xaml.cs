using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace Domain_Guardian;

public partial class UpdatePromptWindow : Window
{
    private const string UnknownVersion = "unknown";
    private readonly string? _releaseHtmlUrl;

    public bool UpdateConfirmed { get; private set; }

    public UpdatePromptWindow(Version latestVersion, Version currentVersion, string? releaseHtmlUrl = null)
    {
        InitializeComponent();

        _releaseHtmlUrl = string.IsNullOrWhiteSpace(releaseHtmlUrl) ? null : releaseHtmlUrl;

        VersionInfoText.Text = $"v{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build} \u2192 v{latestVersion.Major}.{latestVersion.Minor}.{latestVersion.Build}";
        MessageText.Text = "A new version of AD Guardian is available. The latest installer will be downloaded and launched automatically.";
        string buildConfig = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? UnknownVersion;
        InstalledVersionText.Text = $"Installed binary: {GetInstalledFileVersion()} (build: {buildConfig})";

        // Hide the inline View Changelog button when the GitHub URL wasn't
        // supplied (e.g. older release API responses without html_url).
        ViewChangelogButton.Visibility = string.IsNullOrWhiteSpace(_releaseHtmlUrl)
            ? Visibility.Collapsed
            : Visibility.Visible;
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

    /// <summary>
    /// E2E test seam — lets unit tests intercept the browser-launch
    /// <see cref="Process.Start(ProcessStartInfo)"/> call so they don't spawn a
    /// real browser in the test sandbox. Default behaviour matches production
    /// (<see cref="Process.Start(ProcessStartInfo)"/> with
    /// <c>UseShellExecute=true</c>). Exposed as <c>internal</c> so the test
    /// assembly (which has <c>InternalsVisibleTo</c>) can swap it out around a
    /// single test invocation; production builds never touch this field.
    ///
    /// Marked at file scope so save/restore in <c>try/finally</c> in tests is
    /// easy; xUnit runs methods within a single class sequentially so parallel
    /// tests cannot collide on the override.
    /// </summary>
    internal static Func<ProcessStartInfo, Process?> LaunchUrlRunner = Process.Start;

    private void ViewChangelogButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_releaseHtmlUrl))
        {
            e.Handled = true;
            return;
        }

        try
        {
            // Process.Start with UseShellExecute=true hands the URL to the
            // user's default browser. Silently swallows launch failures so a
            // misconfigured browser never blocks the update decision. Routed
            // through LaunchUrlRunner so unit tests can intercept without
            // spawning a real browser (production behaviour unchanged).
            LaunchUrlRunner(new ProcessStartInfo
            {
                FileName = _releaseHtmlUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // Best-effort — the button still surfaces the URL in its label,
            // so the user can copy it manually if the launch failed.
        }
        finally
        {
            e.Handled = true;
        }
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
