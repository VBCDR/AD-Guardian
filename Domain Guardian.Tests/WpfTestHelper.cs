using System;
using System.Threading;
using System.Windows;

namespace Domain_Guardian.Tests;

/// <summary>
/// Shared helper that ensures the WPF Application singleton exists and has
/// App.xaml resources loaded, regardless of which test class runs first.
///
/// xUnit may run test classes in parallel, so this uses a static lock and
/// flag to prevent multiple App instances (which would throw).
/// </summary>
internal static class WpfTestHelper
{
    private static readonly object AppLock = new();
    private static volatile bool _appCreated;
    private static Exception? _appInitError;

    /// <summary>
    /// Ensures <see cref="Application.Current"/> is created on an STA thread
    /// and App.xaml resources are loaded. Safe to call from any test class;
    /// subsequent calls after the first are no-ops.
    /// </summary>
    internal static void EnsureApplicationResources()
    {
        if (_appCreated)
            return;

        lock (AppLock)
        {
            if (_appCreated)
                return;

            if (_appInitError != null)
                throw new InvalidOperationException(
                    "Application init previously failed.", _appInitError);

            Exception? initEx = null;
            var initThread = new Thread(() =>
            {
                try
                {
                    new AdHealthMonitor.App();

                    // Verify App.xaml resources were loaded. In the test
                    // runner, auto-generated InitializeComponent may silently
                    // fail to resolve the relative pack URI. Fall back to an
                    // explicit load if needed.
                    if (!Application.Current.Resources.Contains("PanelBorderBrush"))
                    {
                        Application.LoadComponent(
                            Application.Current,
                            new Uri("/Domain Guardian;component/App.xaml",
                                    UriKind.Relative));
                    }
                }
                catch (Exception ex)
                {
                    initEx = ex;
                }
            });
            initThread.SetApartmentState(ApartmentState.STA);
            initThread.Start();
            initThread.Join();

            if (initEx != null)
            {
                _appInitError = initEx;
                throw new InvalidOperationException(
                    "Failed to create Application for tests.", initEx);
            }

            _appCreated = true;
        }
    }
}
