using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Windows;


namespace AdHealthMonitor
{
    [SupportedOSPlatform("windows")]
    public partial class App : System.Windows.Application
    {
        /// <summary>
        /// True when the process is running with administrator privileges.
        /// </summary>
        public static bool IsRunningAsAdmin { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            IsRunningAsAdmin = CheckIsAdmin();

            bool isInitializationLaunch = e.Args.Length > 0 &&
                                          e.Args[0].Equals("-initialize-state", System.StringComparison.OrdinalIgnoreCase);
            bool isScheduledLaunch = e.Args.Length > 0 &&
                                     e.Args[0].Equals("-scheduled", System.StringComparison.OrdinalIgnoreCase);

            if (isInitializationLaunch)
            {
                InitializeAppInfrastructure();
                System.Environment.Exit(0);
                return;
            }

            // Scheduled runs require admin for dcdiag/repadmin.  The Task Scheduler
            // definition already sets RunLevel.Highest, but as a safety-net we
            // re-launch elevated if the current process is not admin.
            if (isScheduledLaunch && !IsRunningAsAdmin)
            {
                if (TryRelaunchAsAdmin(e.Args))
                {
                    System.Environment.Exit(0);
                    return;
                }
                // If elevation fails, fall through – tests will report errors.
            }

            base.OnStartup(e);

            if (isScheduledLaunch)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }

            MainWindow mainWindow = new();
            MainWindow = mainWindow;

            if (!isScheduledLaunch)
            {
                mainWindow.Show();
            }
        }

        /// <summary>
        /// Attempt to re-launch the current executable with the runas verb so it
        /// runs elevated.  Returns true if the new process was started.
        /// </summary>
        public static bool TryRelaunchAsAdmin(string[]? args = null)
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (string.IsNullOrEmpty(exePath))
                {
                    return false;
                }

                string arguments = args is { Length: > 0 }
                    ? string.Join(" ", System.Array.ConvertAll(args, a => $"\"{a}\""))
                    : string.Empty;

                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    UseShellExecute = true,
                    Verb = "runas"
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool CheckIsAdmin()
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static void InitializeAppInfrastructure()
        {
            AppStateStore.CreateDefault().Initialize();
            Directory.CreateDirectory(@"C:\ADCheckLogs");
            Directory.CreateDirectory(Path.Combine(@"C:\ADCheckLogs", "runs"));
        }
    }
}
