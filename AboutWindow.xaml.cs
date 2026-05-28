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

namespace Domain_Guardian;

public partial class AboutWindow : Window
{
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
        await AutoUpdateAsync();
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
                MessageBox.Show("Could not retrieve update information.", "Update Check", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Version latestVersion = new(release.tag_name.TrimStart('v'));
            Version currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            if (latestVersion <= currentVersion)
            {
                MessageBox.Show($"You are up to date (v{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build}).",
                    "No Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            UpdatePromptWindow prompt = new(latestVersion, currentVersion);
            prompt.Owner = this;
            bool? result = prompt.ShowDialog();

            if (result != true)
                return;

            GitHubAsset? asset = release.assets.FirstOrDefault(a =>
                a.browser_download_url.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
            if (asset == null)
            {
                MessageBox.Show("No suitable update asset found.", "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show("The application will now update and restart.", "Updating", MessageBoxButton.OK, MessageBoxImage.Information);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Error checking for updates: " + ex.Message, "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
