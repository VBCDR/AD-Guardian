using System.Windows.Controls;

namespace AdHealthMonitor;

public partial class LogsTabPage : UserControl
{
    public LogsTabPage() { InitializeComponent(); }
    private void LogsFilter_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.LogsFilter_Changed(sender, e);
    }
    private void ShowFullLog_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.ShowFullLog_Click(sender, e);
    }
    private void ClearLogsFilters_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.ClearLogsFilters_Click(sender, e);
    }
    private void OpenLogFile_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.OpenLogFile_Click(sender, e);
    }
    private void dgLogsEntries_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.dgLogsEntries_SelectionChanged(sender, e);
    }
    private void LogsSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.LogsSearchBox_TextChanged(sender, e);
    }
    private void PopOutLogViewer_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.PopOutLogViewer_Click(sender, e);
    }
    private void BackToHealth_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.BackToHealth_Click(sender, e);
    }
}
