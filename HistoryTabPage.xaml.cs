using System.Windows.Controls;

namespace AdHealthMonitor;

public partial class HistoryTabPage : UserControl
{
    public HistoryTabPage() { InitializeComponent(); }
    private void SelectAllCheckBox_Checked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.SelectAllCheckBox_Checked(sender, e);
    }
    private void DataGrid_CellPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.DataGrid_CellPreviewMouseLeftButtonDown(sender, e);
    }
    private void dpHistoryFilter_SelectedDateChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.dpHistoryFilter_SelectedDateChanged(sender, e);
    }
    private void dgTestHistory_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.dgTestHistory_SelectionChanged(sender, e);
    }
    private void RowCheckBox_Unchecked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.RowCheckBox_Unchecked(sender, e);
    }
    private void ClearHistoryFilters_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.ClearHistoryFilters_Click(sender, e);
    }
    private void SelectAllCheckBox_Unchecked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.SelectAllCheckBox_Unchecked(sender, e);
    }
    private void ViewSelectedHistoryRun_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.ViewSelectedHistoryRun_Click(sender, e);
    }
    private void RowCheckBox_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.RowCheckBox_PreviewMouseLeftButtonDown(sender, e);
    }
    private void RowCheckBox_Checked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.RowCheckBox_Checked(sender, e);
    }
    private void DeleteSelectedHistory_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.DeleteSelectedHistory_Click(sender, e);
    }
    private void txtHistorySearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.txtHistorySearch_TextChanged(sender, e);
    }
    private void CompareRuns_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.CompareRuns_Click(sender, e);
    }
}
