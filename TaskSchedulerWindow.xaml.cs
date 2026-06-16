using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace AdHealthMonitor;

[SupportedOSPlatform("windows")]
public partial class TaskSchedulerWindow : Window
{
    private readonly AppStateStore appStateStore;
    private readonly ObservableCollection<ScheduledTask> scheduledTasks = new();
    private int selectedTaskIndex = -1;

    public TaskSchedulerWindow(string email)
    {
        appStateStore = AppStateStore.CreateDefault();
        appStateStore.Initialize();
        InitializeComponent();
        TaskListView.SelectionChanged += TaskListView_SelectionChanged;
        LoadScheduledTasks();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        TaskListView.SelectionChanged -= TaskListView_SelectionChanged;
        try
        {
            appStateStore.SaveScheduledTasks(scheduledTasks);
        }
        catch
        {
            // Silently persist during close; not showing a modal here avoids re-entrancy deadlocks.
        }
        base.OnClosing(e);
    }

    private void LoadScheduledTasks()
    {
        try
        {
            scheduledTasks.Clear();
            foreach (var task in appStateStore.LoadScheduledTasks())
            {
                scheduledTasks.Add(task);
            }
        }
        catch (Exception ex)
        {
            NotificationService.Show(this, "Error", "Error loading tasks: " + ex.Message, isError: true);
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        string taskName = TaskNameTextBox.Text.Trim();
        // Manual split+trim+filter: avoids LINQ Select/Where/ToList allocations
        List<string> dcEntries = new();
        string[] parts = DomainControllerTextBox.Text.Trim().Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string trimmed = parts[i].Trim();
            if (!string.IsNullOrEmpty(trimmed))
                dcEntries.Add(trimmed);
        }

        if (dcEntries.Count == 0)
        {
            new SuccessNotification("Validation Error", "Please enter at least one domain controller.", isError: true) { Owner = this }.ShowDialog();
            return;
        }

        string domainControllers = string.Join(", ", dcEntries);
        string frequency = (FrequencyComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
        DateTime? startDate = StartDatePicker.SelectedDate;
        string startTime = StartTimeTextBox.Text.Trim();

        if (string.IsNullOrEmpty(taskName) || string.IsNullOrEmpty(domainControllers) || string.IsNullOrEmpty(frequency) || !startDate.HasValue || string.IsNullOrEmpty(startTime))
        {
            new SuccessNotification("Validation Error", "Please fill in all fields.", isError: true) { Owner = this }.ShowDialog();
            return;
        }

        ScheduledTask newTask = new()
        {
            TaskName = taskName,
            DomainController = domainControllers,
            Frequency = frequency,
            StartDate = startDate.Value,
            StartTime = startTime
        };

        try
        {
            if (selectedTaskIndex >= 0)
            {
                ScheduledTask oldTask = scheduledTasks[selectedTaskIndex];
                await RunWithLoadingWindowAsync(
                    "Updating scheduled task",
                    "Updating the Windows scheduled task and saved app state.",
                    async () =>
                    {
                        await Task.Run(() => WindowsTaskSchedulerInterop.DeleteTask(oldTask.TaskName)).ConfigureAwait(true);
                        await Task.Run(() => WindowsTaskSchedulerInterop.CreateOrUpdateTask(newTask)).ConfigureAwait(true);
                    });

                scheduledTasks[selectedTaskIndex] = newTask;
                selectedTaskIndex = -1;
            }
            else
            {
                await RunWithLoadingWindowAsync(
                    "Saving scheduled task",
                    "Creating the Windows scheduled task and saving it locally.",
                    () => Task.Run(() => WindowsTaskSchedulerInterop.CreateOrUpdateTask(newTask)));
                scheduledTasks.Add(newTask);
            }
        }
        catch (Exception ex)
        {
            new SuccessNotification("Save Failed", $"Failed to save the scheduled task:\n{ex.Message}", isError: true) { Owner = this }.ShowDialog();
            return;
        }

        RefreshTaskList();
        ClearInputFields();
        new SuccessNotification("Task Saved", $"Task \"{taskName}\" has been saved successfully.") { Owner = this }.ShowDialog();
    }

    private bool CreateWindowsScheduledTask(ScheduledTask task)
    {
        try
        {
            WindowsTaskSchedulerInterop.CreateOrUpdateTask(task);
            return true;
        }
        catch (Exception ex)
        {
            new SuccessNotification("Task Scheduler Error", $"Failed to create Windows scheduled task:\n{ex.Message}", isError: true) { Owner = this }.ShowDialog();
            return false;
        }
    }

    private bool RemoveWindowsScheduledTask(ScheduledTask task)
    {
        try
        {
            WindowsTaskSchedulerInterop.DeleteTask(task.TaskName);
            return true;
        }
        catch (Exception ex)
        {
            new SuccessNotification("Task Scheduler Error", $"Failed to remove Windows scheduled task:\n{ex.Message}", isError: true) { Owner = this }.ShowDialog();
            return false;
        }
    }

    private async void RemoveTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (TaskListView.SelectedIndex >= 0)
        {
            ScheduledTask task = scheduledTasks[TaskListView.SelectedIndex];
            try
            {
                await RunWithLoadingWindowAsync(
                    "Removing scheduled task",
                    "Deleting the Windows scheduled task and updating saved state.",
                    () => Task.Run(() => WindowsTaskSchedulerInterop.DeleteTask(task.TaskName)));
            }
            catch (Exception ex)
            {
                new SuccessNotification("Delete Failed", $"Failed to remove the scheduled task:\n{ex.Message}", isError: true) { Owner = this }.ShowDialog();
                return;
            }

            scheduledTasks.RemoveAt(TaskListView.SelectedIndex);
            RefreshTaskList();
            ClearInputFields();
            new SuccessNotification("Task Deleted", $"Task \"{task.TaskName}\" has been removed successfully.") { Owner = this }.ShowDialog();
        }
        else
        {
            new SuccessNotification("No Selection", "Please select a task to remove.", isError: true) { Owner = this }.ShowDialog();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RefreshTaskList()
    {
        TaskListView.ItemsSource ??= scheduledTasks;
    }

    private void ClearInputFields()
    {
        TaskNameTextBox.Text = string.Empty;
        DomainControllerTextBox.Text = string.Empty;
        FrequencyComboBox.SelectedIndex = -1;
        StartDatePicker.SelectedDate = null;
        StartTimeTextBox.Text = string.Empty;
        TaskListView.SelectedIndex = -1;
        selectedTaskIndex = -1;
    }

    private void TaskListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RemoveTaskButton.IsEnabled = TaskListView.SelectedIndex >= 0;

        if (TaskListView.SelectedIndex >= 0)
        {
            ScheduledTask task = scheduledTasks[TaskListView.SelectedIndex];
            TaskNameTextBox.Text = task.TaskName;
            DomainControllerTextBox.Text = task.DomainController;

            foreach (ComboBoxItem item in FrequencyComboBox.Items)
            {
                if (string.Equals(item.Content?.ToString(), task.Frequency, StringComparison.OrdinalIgnoreCase))
                {
                    FrequencyComboBox.SelectedItem = item;
                    break;
                }
            }

            StartDatePicker.SelectedDate = task.StartDate;
            StartTimeTextBox.Text = task.StartTime;
            selectedTaskIndex = TaskListView.SelectedIndex;
        }
        else
        {
            selectedTaskIndex = -1;
        }
    }

    private async Task RunWithLoadingWindowAsync(string title, string message, Func<Task> operation)
    {
        const int loadingWindowDelayMs = 100;
        bool previousEnabledState = IsEnabled;
        LoadingWindow? loadingWindow = null;
        try
        {
            Task operationTask = operation();
            Task delayTask = Task.Delay(loadingWindowDelayMs);
            Task completedTask = await Task.WhenAny(operationTask, delayTask).ConfigureAwait(true);
            if (completedTask != operationTask)
            {
                loadingWindow = new(title, message)
                {
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                loadingWindow.Show();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                IsEnabled = false;
            }

            await operationTask.ConfigureAwait(true);
        }
        finally
        {
            if (loadingWindow != null && loadingWindow.IsVisible)
            {
                loadingWindow.Close();
            }

            IsEnabled = previousEnabledState;
            Activate();
        }
    }
}
