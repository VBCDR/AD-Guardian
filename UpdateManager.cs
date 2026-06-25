using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
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
        public string body { get; set; } = string.Empty;
        public GitHubAsset[] assets { get; set; } = Array.Empty<GitHubAsset>();
    }

    internal sealed class GitHubAsset
    {
        public string name { get; set; } = string.Empty;
        public string browser_download_url { get; set; } = string.Empty;
        // GitHub returns "sha256:<hex>". Empty means "no digest" — the verification step
        // refuses to launch the installer in that case (fail-closed).
        public string digest { get; set; } = string.Empty;
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
        await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(true);
        if (!owner.IsLoaded || !owner.IsVisible) return;
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
            TimeSpan requestTimeout = isManualCheck ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(10);
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

            UpdatePromptWindow prompt = new(latestVersion, currentVersion, release.html_url)
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
                "Verifying installer",
                "Computing SHA-256 of the downloaded installer and comparing it to the GitHub release digest.",
                isIndeterminate: true);
            try
            {
                await VerifyInstallerHashAsync(installerPath, asset).ConfigureAwait(true);
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
        using HttpClient client = new() { Timeout = Timeout.InfiniteTimeSpan };
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

    // Maximum attempts when opening %TEMP%\ADGuardianUpdate for write.
    // 5 attempts with 250 / 500 / 750 / 1000 ms linear backoff across the 4
    // inter-attempt delays sums to exactly 2500ms = 2.5s of waits, comfortably
    // covering Windows Defender Controlled Folder Access / Smart App Control
    // scan locks that typically resolve in <1s after a write probe.
    internal const int MaxDownloadAttempts = 5;
    internal const int InitialDownloadBackoffMs = 250;

    private static async Task<string> DownloadInstallerAsync(GitHubAsset asset, Action<DownloadProgress> progressCallback)
    {
        using HttpClient client = new();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ADGuardianApp");
        using HttpResponseMessage response = await client.GetAsync(asset.browser_download_url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? totalBytes = response.Content.Headers.ContentLength;
        await using Stream sourceStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

        // Open the local target with retry-on-EACCES so a transient Defender
        // scan holding %TEMP%\ADGuardianUpdate does not abort the entire
        // update flow. The helper raises a clear IOException after retries
        // are exhausted, with the manual download URL inline so the user can
        // recover without digging through docs.
        FileStream fs = await OpenDownloadTargetStreamAsync(asset).ConfigureAwait(false);
        bool success = false;
        try
        {
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
            success = true;
        }
        finally
        {
            // Always close the FileStream before any Delete attempt -- otherwise
            // File.Delete throws a sharing-violation IOException that masks the
            // original error. On success success==true and we leave the file in
            // place for the SHA-256 verification step that follows.
            await fs.DisposeAsync().ConfigureAwait(false);
            if (!success)
            {
                try { File.Delete(fs.Name); } catch { /* best-effort cleanup */ }
            }
        }

        return fs.Name;
    }

    // Opens (or fails through to a clear diagnostic exception after MaxDownloadAttempts
    // retries) a writable FileStream on the update target. The factory + delay
    // delegates are bitten-by-tests seams -- production passes the defaults.
    internal static async Task<FileStream> OpenDownloadTargetStreamAsync(
        GitHubAsset asset,
        Func<string, FileStream>? streamFactory = null,
        Func<int, Task>? delay = null,
        string? overrideTempRoot = null)
    {
        Func<string, FileStream> factory = streamFactory ?? DefaultDownloadStreamFactory;
        Func<int, Task> wait = delay ?? DefaultDownloadDelay;
        string root = overrideTempRoot ?? Path.GetTempPath();
        string tempFile = Path.Combine(root, "ADGuardianUpdate", asset.name);

        Exception? lastException = null;
        for (int attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
        {
            try
            {
                string? dir = Path.GetDirectoryName(tempFile);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                return factory(tempFile);
            }
            catch (Exception ex) when (IsEaccesLikeException(ex))
            {
                lastException = ex;
                if (attempt >= MaxDownloadAttempts) break;
                await wait(InitialDownloadBackoffMs * attempt).ConfigureAwait(false);
            }
        }

        throw new IOException(
            $"Could not open update download target after {MaxDownloadAttempts} attempts at '{tempFile}'. " +
            "Likely cause: Windows Defender Controlled Folder Access, Smart App Control, or third-party antivirus is denying writes to the update cache. " +
            $"As a workaround, download the installer manually from {asset.browser_download_url} or visit https://github.com/VBCDR/AD-Guardian/releases/latest.",
            lastException);
    }

    private static FileStream DefaultDownloadStreamFactory(string path) =>
        new(path, FileMode.Create, FileAccess.Write, FileShare.None);

    private static Task DefaultDownloadDelay(int milliseconds) => Task.Delay(milliseconds);

    // Classifies the .NET exception types that surface ERROR_ACCESS_DENIED /
    // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION (the three access-denied
    // variants Defender / Smart App Control commonly raise in-process when
    // scanning a path). HResult values:
    //   0x80070005 (-2147024894) -- ERROR_ACCESS_DENIED (5)
    //   0x80070020 (-2147024608) -- ERROR_SHARING_VIOLATION (32)
    //   0x80070021 (-2147024607) -- ERROR_LOCK_VIOLATION (33)
    // NativeErrorCode values:
    //   5, 32, 33 -- the same three errno values via Win32Exception inner.
    internal static bool IsEaccesLikeException(Exception ex)
    {
        if (ex is UnauthorizedAccessException) return true;
        if (ex is IOException ioEx)
        {
            const int EAccessDenied = unchecked((int)0x80070005);
            const int ESharingViolation = unchecked((int)0x80070020);
            const int ELockViolation = unchecked((int)0x80070021);
            int hr = ioEx.HResult;
            if (hr == EAccessDenied || hr == ESharingViolation || hr == ELockViolation) return true;
            if (ioEx.InnerException is Win32Exception w32 &&
                (w32.NativeErrorCode == 5 || w32.NativeErrorCode == 32 || w32.NativeErrorCode == 33))
            {
                return true;
            }
        }
        return false;
    }

    // SHA-256 of the downloaded file in lower-case hex (64 chars).
    private static async Task<string> ComputeFileSha256HexAsync(string filePath)
    {
        await using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer, 0, bytesRead);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    // Verifies the downloaded installer against the digest returned by the GitHub Releases API.
    // Fail-closed: if the API returned no digest (or a non-SHA-256 algorithm), the installer is
    // not launched. This protects against tampered releases, MITM on the download URL, and
    // truncated/corrupt downloads.
    private static async Task VerifyInstallerHashAsync(string installerPath, GitHubAsset asset)
    {
        const string Sha256Prefix = "sha256:";

        if (string.IsNullOrWhiteSpace(asset.digest))
        {
            throw new InvalidOperationException(
                $"Release asset '{asset.name}' has no digest from GitHub; refusing to launch an unverified installer.");
        }

        if (!asset.digest.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Release asset '{asset.name}' has unsupported digest '{asset.digest}'; expected 'sha256:<hex>'.");
        }

        string expected = asset.digest[Sha256Prefix.Length..].Trim().ToLowerInvariant();
        if (expected.Length != 64)
        {
            throw new InvalidOperationException(
                $"Release asset '{asset.name}' has malformed SHA-256 digest (expected 64 hex chars, got {expected.Length}).");
        }

        string actual = await ComputeFileSha256HexAsync(installerPath).ConfigureAwait(false);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            // Best-effort cleanup of the bad file so it doesn't linger in the temp directory.
            try { File.Delete(installerPath); } catch { /* ignore */ }
            throw new InvalidOperationException(
                $"SHA-256 verification FAILED for '{asset.name}'. " +
                $"Expected '{expected}', computed '{actual}'. The downloaded file is corrupt or has been tampered with; the installer will not be launched.");
        }
    }

    private static void LaunchInstallerForUpdate(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/UPDATE /CLOSEAPPLICATIONS",
            UseShellExecute = true
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
