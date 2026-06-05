using System.Windows.Controls;

namespace AdHealthMonitor;

public partial class HealthTabPage : UserControl
{
    public HealthTabPage() { InitializeComponent(); }
    private void ClearResults_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.ClearResults_Click(sender, e);
    }
    private void DataGrid_CellPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.DataGrid_CellPreviewMouseLeftButtonDown(sender, e);
    }
    private void ExportButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.ExportButton_Click(sender, e);
    }
    private void SearchBox_GotFocus(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.SearchBox_GotFocus(sender, e);
    }
    private void RunButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.RunButton_Click(sender, e);
    }
    private void SelectAllCheckBox_Unchecked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.SelectAllCheckBox_Unchecked(sender, e);
    }
    private void SelectAllCheckBox_Checked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.SelectAllCheckBox_Checked(sender, e);
    }
    private void ViewSelectedLog_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.ViewSelectedLog_Click(sender, e);
    }
    private void StopButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.StopButton_Click(sender, e);
    }
    private void ExecutiveSummary_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.ExecutiveSummary_Click(sender, e);
    }
    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.SearchBox_TextChanged(sender, e);
    }
    private void RowCheckBox_Checked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.RowCheckBox_Checked(sender, e);
    }
    private void RowCheckBox_Unchecked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.RowCheckBox_Unchecked(sender, e);
    }
    private void SearchBox_LostFocus(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.SearchBox_LostFocus(sender, e);
    }
    private void RowCheckBox_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.RowCheckBox_PreviewMouseLeftButtonDown(sender, e);
    }
    private void DataGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.DataGrid_SelectionChanged(sender, e);
    }
    private void SearchButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.SearchButton_Click(sender, e);
    }
}
