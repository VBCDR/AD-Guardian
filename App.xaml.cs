using System.IO;
using System.Runtime.Versioning;
using System.Windows;

namespace AdHealthMonitor
{
    // The app.manifest specifies requireAdministrator, so the process is always
    // elevated. No runtime admin checks or re-launch logic are needed.
    [SupportedOSPlatform("windows")]
    public partial class App : System.Windows.Application
    {
        internal const string LogDirectoryPath = @"C:\ADCheckLogs";
        internal const string RunLogsDirectoryName = "runs";

        protected override void OnStartup(StartupEventArgs e)
        {
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

        private static void InitializeAppInfrastructure()
        {
            AppStateStore.CreateDefault().Initialize();
            Directory.CreateDirectory(LogDirectoryPath);
            Directory.CreateDirectory(Path.Combine(LogDirectoryPath, RunLogsDirectoryName));
        }
    }
}
