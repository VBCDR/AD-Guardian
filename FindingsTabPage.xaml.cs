using System.Windows.Controls;

namespace AdHealthMonitor;

public partial class FindingsTabPage : UserControl
{
    public FindingsTabPage() { InitializeComponent(); }
    private void BackToHealth_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.BackToHealth_Click(sender, e);
    }
    private void OpenFindingLog_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.OpenFindingLog_Click(sender, e);
    }
    private void FindingsSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.FindingsSearchBox_TextChanged(sender, e);
    }
    private void dgFindings_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.dgFindings_SelectionChanged(sender, e);
    }
}
