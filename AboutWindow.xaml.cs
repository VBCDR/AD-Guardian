using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Navigation;
using AdHealthMonitor;

namespace Domain_Guardian;

public partial class AboutWindow : Window
{
    private bool updateCheckInProgress;

    public AboutWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        string version = UpdateManager.GetCurrentAppVersion().ToString(3);
        VersionText.Text = $"v{version}";
        VersionValueText.Text = version;
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

        await UpdateManager.CheckForUpdatesManuallyAsync(this);

        if (sender is FrameworkElement trigger)
        {
            trigger.IsEnabled = true;
        }

        updateCheckInProgress = false;
    }

    internal static bool TryParseReleaseVersion(string rawVersion, out Version version)
    {
        return UpdateManager.TryParseReleaseVersion(rawVersion, out version);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
