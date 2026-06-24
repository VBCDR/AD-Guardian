using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace AdHealthMonitor;

public partial class SuccessNotification : Window
{
    private DispatcherTimer? _autoDismissTimer;
    private int _autoDismissRemainingSeconds;

    public SuccessNotification(string title, string message, bool isError = false, int autoDismissSeconds = 0)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        Title = title;

        if (isError)
        {
            IconBorder.Background = new SolidColorBrush(Color.FromRgb(211, 47, 47));
            IconText.Text = "\u2716";
        }

        if (autoDismissSeconds <= 0)
        {
            return;
        }

        _autoDismissRemainingSeconds = autoDismissSeconds;
        AutoDismissText.Visibility = Visibility.Visible;
        AutoDismissText.Text = $"Auto-closing in {_autoDismissRemainingSeconds}s — click OK to keep this open";
        _autoDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoDismissTimer.Tick += AutoDismissTimer_Tick;
        _autoDismissTimer.Start();
        Closed += OnWindowClosed;
    }

    private void AutoDismissTimer_Tick(object? sender, EventArgs e)
    {
        if (--_autoDismissRemainingSeconds <= 0)
        {
            Close();
            return;
        }

        AutoDismissText.Text = $"Auto-closing in {_autoDismissRemainingSeconds}s — click OK to keep this open";
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _autoDismissTimer?.Stop();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
