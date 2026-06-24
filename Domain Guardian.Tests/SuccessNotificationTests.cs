using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;
using AdHealthMonitor;
using Xunit;

namespace Domain_Guardian.Tests;

/// <summary>
/// Regression guard: the auto-dismiss feature added to <see cref="SuccessNotification"/>
/// must NOT start a DispatcherTimer when autoDismissSeconds is 0 (or negative), so
/// that legacy callers (error popups, settings, export, email) don't silently
/// auto-close themselves.
/// </summary>
public class SuccessNotificationTests
{
    // The private _autoDismissTimer field is the only signal we need: if it is
    // null, no timer was started. Cached once for performance.
    private static readonly FieldInfo AutoDismissTimerField =
        typeof(SuccessNotification).GetField(
            "_autoDismissTimer",
            BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException(
            "SuccessNotification._autoDismissTimer field not found — constructor shape changed?");

    private static readonly FieldInfo AutoDismissRemainingSecondsField =
        typeof(SuccessNotification).GetField(
            "_autoDismissRemainingSeconds",
            BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException(
            "SuccessNotification._autoDismissRemainingSeconds field not found — constructor shape changed?");

    /// <summary>
    /// Runs <paramref name="action"/> on a fresh STA thread. WPF requires STA
    /// for any Window construction (<c>InitializeComponent</c> in particular),
    /// and xUnit's test thread may not be STA. The STA thread is joined
    /// synchronously; any exception it captures is re-thrown on the test
    /// thread via <see cref="ExceptionDispatchInfo"/> so xUnit attributes it
    /// to the test method.
    /// </summary>
    private static void RunOnSta(Action action)
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                WpfTestHelper.EnsureApplicationResources();
                action();
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
        {
            ExceptionDispatchInfo.Capture(threadEx).Throw();
        }
    }

    // ── Regression: timer must NOT exist when autoDismissSeconds is 0 ────

    [Fact]
    public void Constructor_AutoDismissSecondsZero_DoesNotStartDispatcherTimer()
    {
        DispatcherTimer? capturedTimer = null;

        RunOnSta(() =>
        {
            var popup = new SuccessNotification(
                "Email Sent",
                "Test email sent successfully!",
                isError: false,
                autoDismissSeconds: 0);
            try
            {
                capturedTimer = (DispatcherTimer?)AutoDismissTimerField.GetValue(popup);
            }
            finally
            {
                popup.Close();
            }
        });

        Assert.Null(capturedTimer);
    }

    [Fact]
    public void Constructor_AutoDismissSecondsNegative_DoesNotStartDispatcherTimer()
    {
        DispatcherTimer? capturedTimer = null;

        RunOnSta(() =>
        {
            var popup = new SuccessNotification(
                "Settings Error",
                "Failed to save settings",
                isError: true,
                autoDismissSeconds: -5);
            try
            {
                capturedTimer = (DispatcherTimer?)AutoDismissTimerField.GetValue(popup);
            }
            finally
            {
                popup.Close();
            }
        });

        Assert.Null(capturedTimer);
    }

    [Fact]
    public void Constructor_DefaultAutoDismissSeconds_DoesNotStartDispatcherTimer()
    {
        // Backwards-compat: callers that do not pass autoDismissSeconds at all
        // must get the same no-timer behavior.
        DispatcherTimer? capturedTimer = null;

        RunOnSta(() =>
        {
            var popup = new SuccessNotification(
                "Email Required",
                "Please enter a recipient email address first.",
                isError: true);
            try
            {
                capturedTimer = (DispatcherTimer?)AutoDismissTimerField.GetValue(popup);
            }
            finally
            {
                popup.Close();
            }
        });

        Assert.Null(capturedTimer);
    }

    // ── Positive case: timer IS created when autoDismissSeconds > 0 ──────
    // We cannot assert IsEnabled=true because popup.Close() in the test cleanup
    // runs OnWindowClosed which stops the timer; the captured DispatcherTimer
    // reference remains non-null but flips to IsEnabled=false. Asserting the
    // timer was instantiated (Start() is unconditional past the early-return)
    // and that the countdown was seeded is sufficient evidence the feature
    // is wired correctly.

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(60)]
    public void Constructor_AutoDismissSecondsPositive_CreatesDispatcherTimer(int seconds)
    {
        DispatcherTimer? capturedTimer = null;
        int capturedRemaining = -1;

        RunOnSta(() =>
        {
            var popup = new SuccessNotification(
                "Test Complete",
                "Tests completed.",
                isError: false,
                autoDismissSeconds: seconds);
            try
            {
                capturedTimer = (DispatcherTimer?)AutoDismissTimerField.GetValue(popup);
                capturedRemaining = (int)AutoDismissRemainingSecondsField.GetValue(popup)!;
            }
            finally
            {
                // Close triggers OnWindowClosed which stops the DispatcherTimer
                // cleanly so it isn't left enabled past the test.
                popup.Close();
            }
        });

        Assert.NotNull(capturedTimer);
        Assert.Equal(TimeSpan.FromSeconds(1), capturedTimer!.Interval);
        Assert.Equal(seconds, capturedRemaining);
    }
}
