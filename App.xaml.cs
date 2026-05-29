using System.IO;
using System.Runtime.Versioning;
using System.Windows;

namespace AdHealthMonitor
{
    [SupportedOSPlatform("windows")]
    public partial class App : System.Windows.Application
    {
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
                mainWindow.Loaded += async (_, _) =>
                {
                    await UpdateManager.CheckForUpdatesOnLaunchAsync(mainWindow).ConfigureAwait(true);
                };
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
