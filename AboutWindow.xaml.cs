using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Navigation;
using AdHealthMonitor;

namespace Domain_Guardian;

public partial class AboutWindow : Window
{
    private bool updateCheckInProgress;

    public class GitHubRelease
    {
        public string tag_name { get; set; } = string.Empty;
        public string html_url { get; set; } = string.Empty;
        public GitHubAsset[] assets { get; set; } = Array.Empty<GitHubAsset>();
    }

    public class GitHubAsset
    {
        public string name { get; set; } = string.Empty;
        public string browser_download_url { get; set; } = string.Empty;
    }

    public AboutWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Version? v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.0";
        VersionValueText.Text = v != null ? $"{v.Major}.{v.Minor}.{v.Build} (rev {v.Revision})" : "1.0.0";
        FrameworkText.Text = RuntimeInformation.FrameworkDescription;
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private async void ManualUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (updateCheckInProgress)
        {
            return;
        }

        updateCheckInProgress = true;
        if (sender is FrameworkElement element)
        {
            element.IsEnabled = false;
        }

        await AutoUpdateAsync();

        if (sender is FrameworkElement trigger)
        {
            trigger.IsEnabled = true;
        }

        updateCheckInProgress = false;
    }

    private async Task AutoUpdateAsync()
    {
        try
        {
            using HttpClient client = new();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ADGuardianApp");
            string url = "https://api.github.com/repos/CianRogers/AD-Guardian/releases/latest";
            string json = await client.GetStringAsync(url);

            GitHubRelease? release;
            try
            {
                release = Newtonsoft.Json.JsonConvert.DeserializeObject<GitHubRelease>(json);
            }
            catch
            {
                release = null;
            }

            if (release?.tag_name == null)
            {
                NotificationService.Show(this, "Update Check", "Could not retrieve update information.");
                return;
            }

            if (!TryParseReleaseVersion(release.tag_name, out Version latestVersion))
            {
                NotificationService.Show(this, "Update Error", $"The latest release tag '{release.tag_name}' could not be understood.", isError: true);
                return;
            }

            Version currentVersion = GetCurrentAppVersion();
            if (latestVersion <= currentVersion)
            {
                NotificationService.Show(this, "No Updates", $"You are already on the latest version (v{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build}).");
                return;
            }

            UpdatePromptWindow prompt = new(latestVersion, currentVersion);
            prompt.Owner = this;
            bool? result = prompt.ShowDialog();

            if (result != true)
                return;

            GitHubAsset? asset = SelectInstallerAsset(release.assets);
            if (asset == null)
            {
                NotificationService.Show(this, "Update Error", "No suitable update asset found.", isError: true);
                return;
            }

            string tempFile = Path.Combine(Path.GetTempPath(), asset.name);
            using (HttpResponseMessage response = await client.GetAsync(asset.browser_download_url))
            {
                response.EnsureSuccessStatusCode();
                await using FileStream fs = new(tempFile, FileMode.Create);
                await response.Content.CopyToAsync(fs);
            }

            Process.Start(new ProcessStartInfo(tempFile) { UseShellExecute = true });
            NotificationService.Show(this, "Updating", "The application will now update and restart.");
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            NotificationService.Show(this, "Update Error", "Error checking for updates: " + ex.Message, isError: true);
        }
    }

    private static Version GetCurrentAppVersion()
    {
        Version assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        string informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? string.Empty;

        return TryParseReleaseVersion(informationalVersion, out Version parsedInformationalVersion)
            ? parsedInformationalVersion
            : new Version(assemblyVersion.Major, assemblyVersion.Minor, assemblyVersion.Build);
    }

    private static bool TryParseReleaseVersion(string rawVersion, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return false;
        }

        string normalized = rawVersion.Trim();
        if (normalized.StartsWith('v'))
        {
            normalized = normalized[1..];
        }

        int separatorIndex = normalized.IndexOfAny(['-', '+', ' ']);
        if (separatorIndex >= 0)
        {
            normalized = normalized[..separatorIndex];
        }

        if (Version.TryParse(normalized, out Version? parsedVersion) && parsedVersion != null)
        {
            version = parsedVersion;
            return true;
        }

        return false;
    }

    private static GitHubAsset? SelectInstallerAsset(GitHubAsset[] assets)
    {
        if (assets == null || assets.Length == 0)
        {
            return null;
        }

        string[] preferredNames =
        {
            "AD Guardian Installer.exe",
            "ADGuardianInstaller.exe",
            "Setup.exe"
        };

        foreach (string preferredName in preferredNames)
        {
            GitHubAsset? exactMatch = assets.FirstOrDefault(asset =>
                asset.name.Equals(preferredName, StringComparison.OrdinalIgnoreCase));
            if (exactMatch != null)
            {
                return exactMatch;
            }
        }

        GitHubAsset? exeInstaller = assets.FirstOrDefault(asset =>
            asset.name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            (asset.name.Contains("installer", StringComparison.OrdinalIgnoreCase) ||
             asset.name.Contains("setup", StringComparison.OrdinalIgnoreCase)));
        if (exeInstaller != null)
        {
            return exeInstaller;
        }

        GitHubAsset? msiInstaller = assets.FirstOrDefault(asset =>
            asset.name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
        if (msiInstaller != null)
        {
            return msiInstaller;
        }

        return assets.FirstOrDefault(asset =>
            asset.browser_download_url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            asset.browser_download_url.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
