using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Microsoft.Win32.TaskScheduler;

namespace AdHealthMonitor;

[SupportedOSPlatform("windows")]
public static class WindowsTaskSchedulerInterop
{
    public static void CreateOrUpdateTask(ScheduledTask task)
    {
        try
        {
            RunOnStaThread(() => CreateViaTaskScheduler(task));
        }
        catch (Exception ex) when (ShouldFallbackToSchTasks(ex))
        {
            CreateViaSchTasks(task);
        }
    }

    public static void DeleteTask(string taskName)
    {
        try
        {
            RunOnStaThread(() => DeleteViaTaskScheduler(taskName));
        }
        catch (Exception ex) when (ShouldFallbackToSchTasks(ex))
        {
            DeleteViaSchTasks(taskName);
        }
    }

    private static void RunOnStaThread(System.Action action)
    {
        System.Threading.Thread thread = new(() => action())
        {
            IsBackground = true,
            Name = "TaskScheduler-STA"
        };
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    private static void CreateViaTaskScheduler(ScheduledTask task)
    {
        if (!TimeSpan.TryParse(task.StartTime, CultureInfo.InvariantCulture, out TimeSpan startOffset))
        {
            startOffset = TimeSpan.FromHours(8);
        }
        DateTime startBoundary = task.StartDate.Date + startOffset;

        using TaskService ts = new();
        TaskDefinition td = ts.NewTask();
        td.RegistrationInfo.Description = $"Scheduled task for AD Guardian: {task.TaskName}";
        td.Principal.RunLevel = TaskRunLevel.Highest;
        td.Principal.LogonType = TaskLogonType.S4U;

        if (task.Frequency.Equals("Hourly", StringComparison.OrdinalIgnoreCase))
        {
            TimeTrigger trigger = new() { StartBoundary = startBoundary };
            trigger.Repetition.Interval = TimeSpan.FromHours(1);
            trigger.Repetition.Duration = TimeSpan.FromDays(1);
            td.Triggers.Add(trigger);
        }
        else if (task.Frequency.Equals("Daily", StringComparison.OrdinalIgnoreCase))
        {
            td.Triggers.Add(new DailyTrigger(1) { StartBoundary = startBoundary, DaysInterval = 1 });
        }
        else if (task.Frequency.Equals("Weekly", StringComparison.OrdinalIgnoreCase))
        {
            DaysOfTheWeek day = startBoundary.DayOfWeek switch
            {
                DayOfWeek.Sunday => DaysOfTheWeek.Sunday,
                DayOfWeek.Monday => DaysOfTheWeek.Monday,
                DayOfWeek.Tuesday => DaysOfTheWeek.Tuesday,
                DayOfWeek.Wednesday => DaysOfTheWeek.Wednesday,
                DayOfWeek.Thursday => DaysOfTheWeek.Thursday,
                DayOfWeek.Friday => DaysOfTheWeek.Friday,
                _ => DaysOfTheWeek.Saturday
            };
            td.Triggers.Add(new WeeklyTrigger(day, 1) { StartBoundary = startBoundary, WeeksInterval = 1 });
        }
        else if (task.Frequency.Equals("Monthly", StringComparison.OrdinalIgnoreCase))
        {
            td.Triggers.Add(new MonthlyTrigger
            {
                StartBoundary = startBoundary,
                DaysOfMonth = new[] { startBoundary.Day },
                MonthsOfYear = MonthsOfTheYear.AllMonths
            });
        }
        else
        {
            TimeTrigger trigger = new() { StartBoundary = startBoundary };
            trigger.Repetition.Interval = TimeSpan.FromHours(1);
            trigger.Repetition.Duration = TimeSpan.FromDays(1);
            td.Triggers.Add(trigger);
        }

        string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        td.Actions.Add(new ExecAction(exePath, $"-scheduled \"{task.TaskName}\""));
        ts.RootFolder.RegisterTaskDefinition($"ADG_{task.TaskName}", td);
    }

    private static void DeleteViaTaskScheduler(string taskName)
    {
        using TaskService ts = new();
        ts.RootFolder.DeleteTask($"ADG_{taskName}", false);
    }

    private static void CreateViaSchTasks(ScheduledTask task)
    {
        if (!TimeSpan.TryParse(task.StartTime, CultureInfo.InvariantCulture, out TimeSpan startOffset))
        {
            startOffset = TimeSpan.FromHours(8);
        }
        DateTime startBoundary = task.StartDate.Date + startOffset;
        string exePath = Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Unable to resolve the application executable path.");
        string taskName = $"ADG_{task.TaskName}";
        string schedule = task.Frequency.ToUpperInvariant() switch
        {
            "HOURLY" => "HOURLY",
            "DAILY" => "DAILY",
            "WEEKLY" => "WEEKLY",
            "MONTHLY" => "MONTHLY",
            _ => "HOURLY"
        };

        string dayArgument = task.Frequency.Equals("Weekly", StringComparison.OrdinalIgnoreCase)
            ? $" /D {startBoundary.DayOfWeek.ToString()[..3].ToUpperInvariant()}"
            : string.Empty;
        string monthlyArgument = task.Frequency.Equals("Monthly", StringComparison.OrdinalIgnoreCase)
            ? $" /D {startBoundary.Day} /M *"
            : string.Empty;
        string modifierArgument = task.Frequency.Equals("Hourly", StringComparison.OrdinalIgnoreCase)
            ? " /MO 1"
            : " /MO 1";
        string taskRun = $"\\\"{exePath}\\\" -scheduled \\\"{task.TaskName}\\\"";
        string arguments =
            $"/Create /TN \"{taskName}\" /TR \"{taskRun}\" /SC {schedule}{modifierArgument} /ST {startBoundary:HH\\:mm} /SD {startBoundary:MM/dd/yyyy}{dayArgument}{monthlyArgument} /RU SYSTEM /RL HIGHEST /F";

        RunSchTasks(arguments, "create");
    }

    private static void DeleteViaSchTasks(string taskName)
    {
        RunSchTasks($"/Delete /TN \"ADG_{taskName}\" /F", "delete");
    }

    private static void RunSchTasks(string arguments, string operation)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            string message = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException($"Failed to {operation} Windows scheduled task via schtasks.exe: {message}".Trim());
        }
    }

    private static bool ShouldFallbackToSchTasks(Exception ex)
    {
        if (ex is Win32Exception or TypeLoadException or FileNotFoundException or DllNotFoundException)
        {
            return true;
        }

        return ex.Message.Contains("Class not registered", StringComparison.OrdinalIgnoreCase);
    }
}
