using System.Windows.Controls;

namespace AdHealthMonitor;

public partial class SchedulerTabPage : UserControl
{
    public SchedulerTabPage() { InitializeComponent(); }
    private void SelectAllCheckBox_Checked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.SelectAllCheckBox_Checked(sender, e);
    }
    private void SchedulerRemoveButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.SchedulerRemoveButton_Click(sender, e);
    }
    private void SchedulerTaskList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.SchedulerTaskList_SelectionChanged(sender, e);
    }
    private void SchedulerSaveButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.SchedulerSaveButton_Click(sender, e);
    }
    private void SelectAllCheckBox_Unchecked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.SelectAllCheckBox_Unchecked(sender, e);
    }
    private void RowCheckBox_Unchecked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.RowCheckBox_Unchecked(sender, e);
    }
    private void RowCheckBox_Checked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.RowCheckBox_Checked(sender, e);
    }
    private void RowCheckBox_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.RowCheckBox_PreviewMouseLeftButtonDown(sender, e);
    }
    private void DataGrid_CellPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow mw) mw.DataGrid_CellPreviewMouseLeftButtonDown(sender, e);
    }
}
