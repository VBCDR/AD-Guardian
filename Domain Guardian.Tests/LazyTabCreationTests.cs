using System;
using System.Threading;
using System.Windows;
using Xunit;

namespace Domain_Guardian.Tests;

/// <summary>
/// Regression tests that ensure every lazy tab UserControl can be created
/// without throwing <see cref="System.Windows.Markup.XamlParseException"/>.
///
/// The original bug: tabs referenced StaticResource keys (e.g. PanelBorderBrush,
/// SoftFillBrush) defined in MainWindow.xaml's Window.Resources. When created
/// via <c>new XxxTabPage()</c> outside the MainWindow visual tree, those
/// resources could not be resolved, causing a crash on tab switch.
///
/// The fix: shared resources were moved to App.xaml's Application.Resources,
/// making them available application-wide during InitializeComponent().
/// These tests guard against that regression.
/// </summary>
public class LazyTabCreationTests
{
    // WPF allows only one Application instance per AppDomain.
    // The first test creates it; subsequent tests reuse it.
    private static readonly object AppLock = new();
    private static volatile bool _appCreated;
    private static Exception? _appInitError;

    /// <summary>
    /// Runs <paramref name="testAction"/> on a dedicated STA thread.
    /// Creates the WPF Application singleton once (guarded by lock) so that
    /// Application.Resources are available for StaticResource resolution.
    /// </summary>
    private static void RunOnStaThread(Action testAction)
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Ensure Application singleton exists so App.xaml resources
                // (brushes, styles, templates) are loaded into
                // Application.Current.Resources for StaticResource resolution.
                if (!_appCreated)
                {
                    lock (AppLock)
                    {
                        if (!_appCreated)
                        {
                            if (_appInitError != null)
                                throw new InvalidOperationException(
                                    "Application init previously failed.", _appInitError);
                            try
                            {
                                new AdHealthMonitor.App();

                                // Verify App.xaml resources were loaded.
                                // In the test runner context, the auto-generated
                                // InitializeComponent() may silently fail to
                                // resolve the relative pack URI.  Fall back to
                                // loading via an absolute pack URI.
                                if (!Application.Current.Resources.Contains("PanelBorderBrush"))
                                {
                                    // Two-arg LoadComponent loads resources into
                                    // the existing Application without creating a
                                    // new one (which would fail the singleton check).
                                    Application.LoadComponent(
                                        Application.Current,
                                        new Uri("/Domain Guardian;component/App.xaml",
                                                UriKind.Relative));
                                }
                            }
                            catch (Exception ex)
                            {
                                _appInitError = ex;
                                throw;
                            }
                            _appCreated = true;
                        }
                    }
                }

                testAction();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadEx != null)
            throw threadEx;
    }

    [Fact]
    public void HealthTabPage_CanBeCreated()
    {
        RunOnStaThread(() =>
        {
            var tab = new AdHealthMonitor.HealthTabPage();
            Assert.NotNull(tab);
        });
    }

    [Fact]
    public void FindingsTabPage_CanBeCreated()
    {
        RunOnStaThread(() =>
        {
            var tab = new AdHealthMonitor.FindingsTabPage();
            Assert.NotNull(tab);
        });
    }

    [Fact]
    public void InfrastructureTabPage_CanBeCreated()
    {
        RunOnStaThread(() =>
        {
            var tab = new AdHealthMonitor.InfrastructureTabPage();
            Assert.NotNull(tab);
        });
    }

    [Fact]
    public void HistoryTabPage_CanBeCreated()
    {
        RunOnStaThread(() =>
        {
            var tab = new AdHealthMonitor.HistoryTabPage();
            Assert.NotNull(tab);
        });
    }

    [Fact]
    public void LogsTabPage_CanBeCreated()
    {
        RunOnStaThread(() =>
        {
            var tab = new AdHealthMonitor.LogsTabPage();
            Assert.NotNull(tab);
        });
    }

    [Fact]
    public void SecurityTabPage_CanBeCreated()
    {
        RunOnStaThread(() =>
        {
            var tab = new AdHealthMonitor.SecurityTabPage();
            Assert.NotNull(tab);
        });
    }

    [Fact]
    public void SettingsTabPage_CanBeCreated()
    {
        RunOnStaThread(() =>
        {
            var tab = new AdHealthMonitor.SettingsTabPage();
            Assert.NotNull(tab);
        });
    }

    [Fact]
    public void SchedulerTabPage_CanBeCreated()
    {
        RunOnStaThread(() =>
        {
            var tab = new AdHealthMonitor.SchedulerTabPage();
            Assert.NotNull(tab);
        });
    }
}
