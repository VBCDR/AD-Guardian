using System;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;

namespace AdHealthMonitor;

[SupportedOSPlatform("windows")]
public partial class CustomPopupWindow : Window
{
    public string TaskName { get; set; } = "Test Completed!";
    public string LogFilePath { get; set; } = string.Empty;
    public string Results { get; set; } = string.Empty;
    public bool OpenedMainWindow { get; private set; }

    public CustomPopupWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TaskName))
        {
            TaskName = "Test Completed!";
        }

        HeaderTextBlock.Text = TaskName;
        Title = $"Scheduled Task Completed: {TaskName}";
        MessageTextBlock.Text = string.IsNullOrWhiteSpace(Results)
            ? $"Scheduled test '{TaskName}' completed successfully."
            : Results;
    }

    private void ViewResultsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenedMainWindow = true;
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (Application.Current.MainWindow is MainWindow mainWindow)
            {
                mainWindow.Visibility = Visibility.Visible;
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.ShowInTaskbar = true;
                mainWindow.Show();
                mainWindow.Activate();
                if (!string.IsNullOrWhiteSpace(LogFilePath) && File.Exists(LogFilePath))
                {
                    _ = mainWindow.ShowScheduledResultsAsync(LogFilePath);
                }
            }
        });
        Close();
    }

    private void ViewLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LogFilePath) || !File.Exists(LogFilePath))
        {
            NotificationService.Show(this, "View Log", "Log file not found.", isError: true);
            return;
        }

        OpenedMainWindow = true;
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (Application.Current.MainWindow is MainWindow mainWindow)
            {
                mainWindow.Visibility = Visibility.Visible;
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.ShowInTaskbar = true;
                mainWindow.Show();
                mainWindow.Activate();
                mainWindow.ShowLogFileInLogsTab(LogFilePath);
            }
        });
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        Domain_Guardian.AboutWindow aboutWindow = new() { Owner = this };
        aboutWindow.ShowDialog();
    }
}
