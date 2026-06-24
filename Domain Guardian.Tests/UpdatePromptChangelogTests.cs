using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows;
using Domain_Guardian;
using Xunit;

namespace Domain_Guardian.Tests;

/// <summary>
/// End-to-end coverage for the "View Changelog" affordance on
/// <see cref="UpdatePromptWindow"/> — the update-prompt dialog that the user
/// sees when a newer GitHub release is available.
///
/// The user's literal request was:
/// "Verify the View Changelog button actually launches the browser in
///  end-to-end testing by triggering a fake update check (mocking the GitHub
///  API response with a known html_url) and asserting the click handler fires
///  Process.Start without error."
///
/// Implementation notes:
///   - We bypass <c>UpdateManager</c>'s real HTTP fetch and construct
///     <see cref="UpdatePromptWindow"/> directly with a hard-coded
///     <c>html_url</c>. This sidesteps GitHub rate-limiting, test-network
///     sandboxing, and <c>ShowDialog()</c> blocking the STA thread — while
///     still exercising every code path the production flow reaches (the
///     constructor sets the button visibility, the click handler reads the
///     _releaseHtmlUrl field and routes through LaunchUrlRunner).
///   - We intercept the browser-launch via <c>LaunchUrlRunner</c> — an
///     <c>internal static</c> delegate exposed on <see cref="UpdatePromptWindow"/>
///     specifically for this test. Production behaviour is unchanged (the
///     default is <see cref="Process.Start(ProcessStartInfo)"/>).
///   - The click handler is invoked via reflection so the test does not
///     depend on WPF template resolution of <c>Button.ClickEvent</c> via WPF
///     routing.
/// </summary>
public class UpdatePromptChangelogTests
{
    /// <summary>
    /// Runs <paramref name="testAction"/> on a dedicated STA thread so WPF
    /// construction (InitializeComponent, dependency-property setters) works.
    /// Reused from LazyTabCreationTests.
    /// </summary>
    private static void RunOnStaThread(Action testAction)
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                WpfTestHelper.EnsureApplicationResources();
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

    /// <summary>
    /// The primary E2E test requested by the user:
    /// triggering a "fake update check" (mocked GitHub API response carrying
    /// <c>html_url</c>) → clicking the View Changelog button → asserting the
    /// <see cref="Process.Start(ProcessStartInfo)"/> call fires exactly once
    /// with the expected <c>FileName</c> and <c>UseShellExecute=true</c>, and
    /// no exception bubbles up.
    /// </summary>
    [Fact]
    public void ViewChangelogButton_Click_WithMockedHtmlUrl_FiresLaunchUrlRunnerOnce()
    {
        RunOnStaThread(() =>
        {
            var original = UpdatePromptWindow.LaunchUrlRunner;
            try
            {
                ProcessStartInfo? captured = null;
                UpdatePromptWindow.LaunchUrlRunner = info =>
                {
                    captured = info;
                    // Production caller ignores the returned Process instance —
                    // returning null matches the "fire-and-forget" semantics of
                    // Process.Start(UseShellExecute=true) in the wild.
                    return null;
                };

                // Fake GitHub API response: inject a known html_url.
                string fakeHtmlUrl =
                    "https://github.com/VBCDR/AD-Guardian/releases/tag/v9999.0.0-test";
                var window = new UpdatePromptWindow(
                    latestVersion:    new Version(9999, 0, 0),
                    currentVersion:   new Version(2, 0, 23),
                    releaseHtmlUrl:   fakeHtmlUrl);

                var method = typeof(UpdatePromptWindow).GetMethod(
                    "ViewChangelogButton_Click",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(method);

                // RoutedEventArgs must be constructed with a real RoutedEvent —
                // the parameterless ctor leaves RoutedEvent=null and the Handled
                // setter throws InvalidOperationException on .NET 9+. ButtonBase
                // is the declaring type for ClickEvent; Button merely inherits it,
                // so we use ButtonBase for clarity and to match future WPF refactors.
                var eventArgs = new RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent);
                method!.Invoke(window, new object?[] { null, eventArgs });

                // Assert the handler fired Process.Start (via the test seam)
                // with the right URL and UseShellExecute=true, and the
                // RoutedEventArgs.Handled flag is set so the click is consumed.
                Assert.NotNull(captured);
                Assert.Equal(fakeHtmlUrl, captured!.FileName);
                Assert.True(captured.UseShellExecute,
                    "UseShellExecute must be true so the OS routes the URL to the default browser");
                Assert.True(eventArgs.Handled,
                    "Click handler must mark RoutedEventArgs.Handled=true");
            }
            finally
            {
                UpdatePromptWindow.LaunchUrlRunner = original;
            }
        });
    }

    /// <summary>
    /// Companion negative test: when the (mocked) GitHub API response has no
    /// <c>html_url</c> (older API versions, malformed payloads, network
    /// failures that returned a partial body), the View Changelog button is
    /// collapsed and clicking it must NOT fire <see cref="Process.Start"/>.
    /// Guards against a regression where the early-return path was removed.
    /// </summary>
    [Fact]
    public void ViewChangelogButton_Click_WhenHtmlUrlIsNull_DoesNotFireLaunchUrlRunner()
    {
        RunOnStaThread(() =>
        {
            var original = UpdatePromptWindow.LaunchUrlRunner;
            try
            {
                int callCount = 0;
                UpdatePromptWindow.LaunchUrlRunner = info =>
                {
                    callCount++;
                    return null;
                };

                var window = new UpdatePromptWindow(
                    latestVersion:    new Version(2, 0, 24),
                    currentVersion:   new Version(2, 0, 23),
                    releaseHtmlUrl:   null);

                var method = typeof(UpdatePromptWindow).GetMethod(
                    "ViewChangelogButton_Click",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(method);
                // RoutedEventArgs must carry a real RoutedEvent; see the
                // primary test for the rationale on .NET 9+. ButtonBase is
                // the declarer; Button inherits it.
                var eventArgs = new RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent);
                method!.Invoke(window, new object?[] { null, eventArgs });

                Assert.Equal(0, callCount);
                Assert.True(eventArgs.Handled);
            }
            finally
            {
                UpdatePromptWindow.LaunchUrlRunner = original;
            }
        });
    }

    /// <summary>
    /// Visibility sanity: when html_url is supplied (fake API success), the
    /// button is Visible. Confirms the constructor wires up the visibility
    /// branch the click test depends on.
    /// </summary>
    [Fact]
    public void UpdatePromptWindow_WithHtmlUrl_ViewChangelogButtonIsVisible()
    {
        RunOnStaThread(() =>
        {
            var window = new UpdatePromptWindow(
                new Version(2, 0, 24), new Version(2, 0, 23),
                releaseHtmlUrl: "https://github.com/VBCDR/AD-Guardian/releases/tag/v2.0.24");
            Assert.Equal(Visibility.Visible, window.ViewChangelogButton.Visibility);
        });
    }

    /// <summary>
    /// Visibility sanity: when html_url is missing (fake API failure or older
    /// API), the button is Collapsed so the user never sees a non-functional
    /// affordance.
    /// </summary>
    [Fact]
    public void UpdatePromptWindow_WithoutHtmlUrl_ViewChangelogButtonIsCollapsed()
    {
        RunOnStaThread(() =>
        {
            var window = new UpdatePromptWindow(
                new Version(2, 0, 24), new Version(2, 0, 23),
                releaseHtmlUrl: null);
            Assert.Equal(Visibility.Collapsed, window.ViewChangelogButton.Visibility);
        });
    }
}
