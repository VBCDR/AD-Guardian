using System;
using System.Diagnostics;
using System.IO;

using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Domain_Guardian;
using Newtonsoft.Json;

namespace AdHealthMonitor;

internal static class UpdateManager
{
    private const string GitHubRepo = "VBCDR/AD-Guardian";
    private static readonly string UpdateStateFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AdHealthMonitor",
        "UpdateState.json");

    internal sealed class GitHubRelease
    {
        public string tag_name { get; set; } = string.Empty;
        public string html_url { get; set; } = string.Empty;
        public GitHubAsset[] assets { get; set; } = Array.Empty<GitHubAsset>();
    }

    internal sealed class GitHubAsset
    {
        public string name { get; set; } = string.Empty;
        public string browser_download_url { get; set; } = string.Empty;
    }

    private sealed class UpdateState
    {
        public string DeferredVersion { get; set; } = string.Empty;
        public DateTime DeferredAtUtc { get; set; }
    }

    public static Version GetCurrentAppVersion()
    {
        Version assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        string informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? string.Empty;

        return TryParseReleaseVersion(informationalVersion, out Version parsedInformationalVersion)
            ? parsedInformationalVersion
            : new Version(assemblyVersion.Major, assemblyVersion.Minor, assemblyVersion.Build);
    }

    public static async Task CheckForUpdatesOnLaunchAsync(Window owner)
    {
        await CheckForUpdatesAsync(owner, isManualCheck: false).ConfigureAwait(true);
    }

    public static async Task ScheduleLaunchUpdateCheckAsync(Window owner)
    {
        await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(true);

        if (!owner.IsLoaded || !owner.IsVisible)
        {
            return;
        }

        await CheckForUpdatesAsync(owner, isManualCheck: false).ConfigureAwait(true);
    }

    public static async Task CheckForUpdatesManuallyAsync(Window owner)
    {
        await CheckForUpdatesAsync(owner, isManualCheck: true).ConfigureAwait(true);
    }

    private static async Task CheckForUpdatesAsync(Window owner, bool isManualCheck)
    {
        try
        {
            TimeSpan requestTimeout = isManualCheck ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(3);
            GitHubRelease? release = await FetchLatestReleaseAsync(requestTimeout).ConfigureAwait(true);
            if (release?.tag_name == null)
            {
                if (isManualCheck)
                {
                    NotificationService.Show(owner, "Update Check", "Could not retrieve update information.");
                }

                return;
            }

            if (!TryParseReleaseVersion(release.tag_name, out Version latestVersion))
            {
                if (isManualCheck)
                {
                    NotificationService.Show(owner, "Update Error", $"The latest release tag '{release.tag_name}' could not be understood.", isError: true);
                }

                return;
            }

            Version currentVersion = GetCurrentAppVersion();
            if (latestVersion <= currentVersion)
            {
                ClearDeferredVersion();
                if (isManualCheck)
                {
                    NotificationService.Show(owner, "No Updates", $"You are already on the latest version (v{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build}).");
                }

                return;
            }

            if (!isManualCheck && ShouldSuppressLaunchPrompt(latestVersion))
            {
                return;
            }

            UpdatePromptWindow prompt = new(latestVersion, currentVersion)
            {
                Owner = owner
            };

            bool? result = prompt.ShowDialog();
            if (result != true)
            {
                if (!isManualCheck)
                {
                    SaveDeferredVersion(latestVersion);
                }

                return;
            }

            ClearDeferredVersion();

            GitHubAsset? asset = SelectInstallerAsset(release.assets);
            if (asset == null)
            {
                NotificationService.Show(owner, "Update Error", "No suitable update asset found.", isError: true);
                return;
            }

            UpdateProgressWindow progressWindow = new(latestVersion, currentVersion)
            {
                Owner = owner
            };
            progressWindow.SetStage(
                "Downloading update",
                "Downloading the latest installer before the update starts.",
                isIndeterminate: true);
            progressWindow.Show();

            string installerPath;
            try
            {
                installerPath = await DownloadInstallerAsync(asset, progress =>
                {
                    progressWindow.Dispatcher.Invoke(() =>
                    {
                        progressWindow.SetStage(
                            "Downloading update",
                            "Downloading the latest installer before the update starts.",
                            isIndeterminate: !progress.TotalBytes.HasValue);
                        progressWindow.SetProgress(progress.BytesReceived, progress.TotalBytes);
                    });
                }).ConfigureAwait(true);
            }
            catch
            {
                progressWindow.Close();
                throw;
            }

            progressWindow.SetStage(
                "Starting installer",
                "The installer is being launched. You will see installation progress in the setup window.",
                isIndeterminate: true);
            await Task.Delay(500).ConfigureAwait(true);

            LaunchInstallerForUpdate(installerPath);
            progressWindow.Close();
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            if (isManualCheck)
            {
                NotificationService.Show(owner, "Update Error", "Error checking for updates: " + ex.Message, isError: true);
            }
        }
    }

    private static async Task<GitHubRelease?> FetchLatestReleaseAsync(TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        using HttpClient client = new();
        client.Timeout = timeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ADGuardianApp");
        string url = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
        string json = await client.GetStringAsync(url, cts.Token).ConfigureAwait(false);

        try
        {
            return JsonConvert.DeserializeObject<GitHubRelease>(json);
        }
        catch
        {
            return null;
        }
    }

    private sealed class DownloadProgress
    {
        public long BytesReceived { get; init; }
        public long? TotalBytes { get; init; }
    }

    private static async Task<string> DownloadInstallerAsync(GitHubAsset asset, Action<DownloadProgress> progressCallback)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ADGuardianUpdate");
        Directory.CreateDirectory(tempDir);
        string tempFile = Path.Combine(tempDir, asset.name);

        using HttpClient client = new();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ADGuardianApp");
        using HttpResponseMessage response = await client.GetAsync(asset.browser_download_url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? totalBytes = response.Content.Headers.ContentLength;
        await using Stream sourceStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using FileStream fs = new(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);

        byte[] buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            await fs.WriteAsync(buffer, 0, bytesRead).ConfigureAwait(false);
            totalRead += bytesRead;
            progressCallback(new DownloadProgress
            {
                BytesReceived = totalRead,
                TotalBytes = totalBytes
            });
        }

        return tempFile;
    }

    private static void LaunchInstallerForUpdate(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/UPDATE /CLOSEAPPLICATIONS",
            UseShellExecute = true,
            Verb = "runas"
        });
    }

    public static bool TryParseReleaseVersion(string rawVersion, out Version version)
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
            "AD.Guardian.Installer.exe",
            "ADGuardianInstaller.exe",
            "Setup.exe"
        };

        // Manual loops: avoids LINQ FirstOrDefault delegate allocations
        foreach (string preferredName in preferredNames)
        {
            for (int i = 0; i < assets.Length; i++)
            {
                if (assets[i].name.Equals(preferredName, StringComparison.OrdinalIgnoreCase))
                    return assets[i];
            }
        }

        for (int i = 0; i < assets.Length; i++)
        {
            GitHubAsset asset = assets[i];
            if (asset.name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                (asset.name.Contains("installer", StringComparison.OrdinalIgnoreCase) ||
                 asset.name.Contains("setup", StringComparison.OrdinalIgnoreCase)))
                return asset;
        }

        for (int i = 0; i < assets.Length; i++)
        {
            if (assets[i].browser_download_url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return assets[i];
        }

        return null;
    }

    private static bool ShouldSuppressLaunchPrompt(Version latestVersion)
    {
        UpdateState state = LoadUpdateState();
        return string.Equals(state.DeferredVersion, latestVersion.ToString(3), StringComparison.OrdinalIgnoreCase) &&
               state.DeferredAtUtc > DateTime.UtcNow.AddHours(-12);
    }

    private static void SaveDeferredVersion(Version latestVersion)
    {
        UpdateState state = new()
        {
            DeferredVersion = latestVersion.ToString(3),
            DeferredAtUtc = DateTime.UtcNow
        };

        SaveUpdateState(state);
    }

    private static void ClearDeferredVersion()
    {
        SaveUpdateState(new UpdateState());
    }

    private static UpdateState LoadUpdateState()
    {
        try
        {
            if (!File.Exists(UpdateStateFilePath))
            {
                return new UpdateState();
            }

            string json = File.ReadAllText(UpdateStateFilePath);
            return JsonConvert.DeserializeObject<UpdateState>(json) ?? new UpdateState();
        }
        catch
        {
            return new UpdateState();
        }
    }

    private static void SaveUpdateState(UpdateState state)
    {
        try
        {
            string directory = Path.GetDirectoryName(UpdateStateFilePath) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonConvert.SerializeObject(state, Formatting.Indented);
            File.WriteAllText(UpdateStateFilePath, json);
        }
        catch
        {
        }
    }
}
