using System;
using System.Windows;

namespace Domain_Guardian;

public partial class UpdateProgressWindow : Window
{
    public UpdateProgressWindow(Version latestVersion, Version currentVersion)
    {
        InitializeComponent();
        VersionInfoText.Text = $"v{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build} -> v{latestVersion.Major}.{latestVersion.Minor}.{latestVersion.Build}";
    }

    public void SetStage(string status, string detail, bool isIndeterminate)
    {
        StatusText.Text = status;
        DetailText.Text = detail;
        ProgressBarControl.IsIndeterminate = isIndeterminate;
        if (isIndeterminate)
        {
            PercentText.Text = "Working...";
        }
    }

    public void SetProgress(long bytesReceived, long? totalBytes)
    {
        if (!totalBytes.HasValue || totalBytes.Value <= 0)
        {
            ProgressBarControl.IsIndeterminate = true;
            PercentText.Text = $"{FormatBytes(bytesReceived)} downloaded";
            return;
        }

        double percent = Math.Clamp((double)bytesReceived / totalBytes.Value * 100d, 0d, 100d);
        ProgressBarControl.IsIndeterminate = false;
        ProgressBarControl.Value = percent;
        PercentText.Text = $"{percent:0}% • {FormatBytes(bytesReceived)} of {FormatBytes(totalBytes.Value)}";
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = value;
        int unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.#} {units[unitIndex]}";
    }
}
