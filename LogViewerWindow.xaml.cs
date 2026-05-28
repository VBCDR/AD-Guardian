using System;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AdHealthMonitor;

[SupportedOSPlatform("windows")]
public partial class LogViewerWindow : Window
{
    private string currentLogContent = string.Empty;
    private readonly Func<string?>? getLogContent;
    private readonly Func<string?>? getLogTitle;

    public LogViewerWindow()
    {
        InitializeComponent();
    }

    public LogViewerWindow(string title, string logContent, Func<string?>? refreshContent = null, Func<string?>? refreshTitle = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        Title = title;
        currentLogContent = logContent;
        LogContentBox.Text = logContent;
        getLogContent = refreshContent;
        getLogTitle = refreshTitle;
        UpdateStats();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LogContentBox.ScrollToHome();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RefreshLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (getLogContent != null)
        {
            currentLogContent = getLogContent() ?? string.Empty;
            LogContentBox.Text = currentLogContent;
            LogContentBox.ScrollToHome();
            UpdateStats();
        }

        if (getLogTitle != null)
        {
            string? newTitle = getLogTitle();
            if (!string.IsNullOrWhiteSpace(newTitle))
            {
                TitleText.Text = newTitle;
                Title = newTitle;
            }
        }
    }

    public void UpdateContent(string title, string logContent)
    {
        TitleText.Text = title;
        Title = title;
        currentLogContent = logContent;
        LogContentBox.Text = logContent;
        LogContentBox.ScrollToHome();
        UpdateStats();
    }

    private void UpdateStats()
    {
        if (string.IsNullOrWhiteSpace(currentLogContent))
        {
            StatsText.Text = "No content";
        }
        else
        {
            int lines = currentLogContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Length;
            int chars = currentLogContent.Length;
            StatsText.Text = $"{lines} lines, {chars:N0} chars";
        }
    }
}
