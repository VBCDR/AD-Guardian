using System;
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
        internal const string RunLogsDirectoryName = "runs";

        // Resolved at process startup so the runtime uses the same
        // CommonApplicationData (={commonappdata} in Inno Setup, typically
        // C:\ProgramData) path the installer creates. Static readonly, not
        // const, because Environment.GetFolderPath is a runtime call and
        // honours user folder-redirection policy that may map
        // CommonApplicationData away from C:\ProgramData. The historical
        // C:\ADCheckLogs root was a hardcoded system-root path that AV,
        // Defender Controlled Folder Access, and Smart App Control now
        // routinely lock or deny writes to.
        internal static readonly string LogDirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AdHealthMonitor",
            "Logs");

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
                // Subscribe BEFORE Show() so the Loaded handler is guaranteed
                // to fire on first paint. WPF semantics: Loaded fires once per
                // window instance after the visual tree is built and the
                // window is rendered, so this is single-shot -- re-opening the
                // window later will not re-fire the toast because the marker
                // file is deleted on first consumption.
                mainWindow.Loaded += OnMainWindowLoaded;
                mainWindow.Show();
            }
        }

        // Subscription target for MainWindow.Loaded. Drains the installer-
        // written MigrationMarker.json (see installer/AD Guardian Installer.iss::
        // CleanupLegacyAdCheckLogs) and surfaces a one-shot Migration Complete
        // (or Migration Cleanup Warning for failed cleanups) toast via the
        // existing modal SuccessNotification pattern. The marker is consumed
        // by MigrationMarker.TryReadAndDelete so the toast never reappears.
        //
        // Designed to NEVER crash startup -- any exception from the migration
        // pipeline is swallowed. The user gets to the main window either way.
        private static void OnMainWindowLoaded(object? sender, RoutedEventArgs e)
        {
            try
            {
                // `sender` is the MainWindow that fired Loaded (we subscribed before Show()).
                // Use it directly instead of Application.Current.MainWindow -- the static
                // base-class property `MainWindow` is shadowed by a same-named member in
                // some WPF versions, and `sender` is guaranteed-correct inside an event
                // handler without any base-class lookup ambiguity.
                Window? ownerWindow = sender as Window;

                MigrationMarker? marker = MigrationMarker.TryReadAndDelete();
                if (marker == null || !marker.IsSignificantForToast || ownerWindow == null)
                {
                    return;
                }

                // Fallback: if the marker carried BytesMigrated == 0 because it
                // was written by an installer version that pre-dates the byte-
                // summation feature in MigrateLegacyLogsTo, sum the size on disk
                // lazily here. Caps inside ComputeBytesMigratedFromDisk keep
                // this bounded (500 ms wall-clock / 5000 files) so we never
                // stall first launch on a sluggish redirected volume.
                //
                // Safe to mutate marker.BytesMigrated in-place below: the file
                // has already been deleted from disk by TryReadAndDelete above,
                // so this in-memory enrichment has no persistent side effect.
                // The marker object is local to this Loaded callback so no other
                // code touches it after we leave.
                if (marker.BytesMigrated == 0 && !string.IsNullOrEmpty(marker.DestinationRoot))
                {
                    marker.BytesMigrated = MigrationMarker.ComputeBytesMigratedFromDisk(marker.DestinationRoot);
                }

                bool isError = string.Equals(marker.CleanupStatus, "failed", StringComparison.OrdinalIgnoreCase);
                NotificationService.Show(
                    ownerWindow,
                    marker.ToToastTitle(),
                    marker.ToToastBody(),
                    isError,
                    marker.AutoDismissSeconds);
            }
            catch
            {
                // The migration toast must NEVER crash first launch.
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
