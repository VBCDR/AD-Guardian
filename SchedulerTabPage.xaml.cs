using System.Windows.Controls;

namespace AdHealthMonitor;

public partial class SchedulerTabPage : UserControl
{
    public SchedulerTabPage() { InitializeComponent(); Loaded += (_, _) => UpdateSchedulerLayout(); }
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
    private void SchedulerPage_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) => UpdateSchedulerLayout();
    private void UpdateSchedulerLayout()
    {
        double width = ActualWidth;
        if (ScheduleFieldsGrid.RowDefinitions.Count < 3 || ScheduleFieldsGrid.ColumnDefinitions.Count < 3)
        {
            return;
        }
        if (width < 800 && width > 0)
        {
            System.Windows.Controls.Grid.SetRow(FreqPanel, 0);
            System.Windows.Controls.Grid.SetColumn(FreqPanel, 0);
            FreqPanel.Margin = new System.Windows.Thickness(0, 0, 0, 10);
            System.Windows.Controls.Grid.SetRow(DatePanel, 1);
            System.Windows.Controls.Grid.SetColumn(DatePanel, 0);
            DatePanel.Margin = new System.Windows.Thickness(0, 0, 0, 10);
            System.Windows.Controls.Grid.SetRow(TimePanel, 2);
            System.Windows.Controls.Grid.SetColumn(TimePanel, 0);
            ScheduleFieldsGrid.RowDefinitions[0].Height = new System.Windows.GridLength(0, System.Windows.GridUnitType.Auto);
            ScheduleFieldsGrid.RowDefinitions[1].Height = new System.Windows.GridLength(0, System.Windows.GridUnitType.Auto);
            ScheduleFieldsGrid.RowDefinitions[2].Height = new System.Windows.GridLength(0, System.Windows.GridUnitType.Auto);
            ScheduleFieldsGrid.ColumnDefinitions[0].Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
            ScheduleFieldsGrid.ColumnDefinitions[1].Width = new System.Windows.GridLength(0, System.Windows.GridUnitType.Star);
            ScheduleFieldsGrid.ColumnDefinitions[2].Width = new System.Windows.GridLength(0, System.Windows.GridUnitType.Star);
        }
        else
        {
            System.Windows.Controls.Grid.SetRow(FreqPanel, 0);
            System.Windows.Controls.Grid.SetColumn(FreqPanel, 0);
            FreqPanel.Margin = new System.Windows.Thickness(0, 0, 10, 0);
            System.Windows.Controls.Grid.SetRow(DatePanel, 0);
            System.Windows.Controls.Grid.SetColumn(DatePanel, 1);
            DatePanel.Margin = new System.Windows.Thickness(0, 0, 10, 0);
            System.Windows.Controls.Grid.SetRow(TimePanel, 0);
            System.Windows.Controls.Grid.SetColumn(TimePanel, 2);
            ScheduleFieldsGrid.RowDefinitions[0].Height = new System.Windows.GridLength(0, System.Windows.GridUnitType.Auto);
            ScheduleFieldsGrid.RowDefinitions[1].Height = new System.Windows.GridLength(0, System.Windows.GridUnitType.Auto);
            ScheduleFieldsGrid.RowDefinitions[2].Height = new System.Windows.GridLength(0, System.Windows.GridUnitType.Auto);
            ScheduleFieldsGrid.ColumnDefinitions[0].Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
            ScheduleFieldsGrid.ColumnDefinitions[1].Width = new System.Windows.GridLength(1.3, System.Windows.GridUnitType.Star);
            ScheduleFieldsGrid.ColumnDefinitions[2].Width = new System.Windows.GridLength(1.1, System.Windows.GridUnitType.Star);
        }
    }
}
