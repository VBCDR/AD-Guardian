using System.Windows.Controls;

namespace AdHealthMonitor;

public partial class SettingsTabPage : UserControl
{
    public SettingsTabPage() { InitializeComponent(); }
    private void TestEmailButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.TestEmailButton_Click(sender, e);
    }
    private void SettingsSaveButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.SettingsSaveButton_Click(sender, e);
    }
}
