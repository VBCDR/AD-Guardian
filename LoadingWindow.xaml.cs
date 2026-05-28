using System;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;

namespace AdHealthMonitor;

[SupportedOSPlatform("windows")]
public partial class LoadingWindow : Window
{
    private readonly DispatcherTimer spinnerTimer;
    private readonly EventHandler? spinnerTickHandler;

    public LoadingWindow(string title, string message)
    {
        InitializeComponent();
        TitleTextBlock.Text = title;
        MessageTextBlock.Text = message;

        spinnerTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        spinnerTickHandler = (s, e) =>
        {
            SpinnerRotation.Angle = (SpinnerRotation.Angle + 6) % 360;
        };
        spinnerTimer.Tick += spinnerTickHandler;
    }

    private void Spinner_Loaded(object sender, RoutedEventArgs e)
    {
        spinnerTimer.Start();
    }

    private void Spinner_Unloaded(object sender, RoutedEventArgs e)
    {
        spinnerTimer.Stop();
        spinnerTimer.Tick -= spinnerTickHandler;
    }
}
