using System;
using System.Net;
using System.Net.Mail;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using System.Windows;

namespace AdHealthMonitor;

[SupportedOSPlatform("windows")]
public partial class TestParametersWindow : Window
{
    private readonly AppStateStore appStateStore;
    public string DomainControllers { get; private set; } = string.Empty;
    public string RecipientEmail { get; private set; } = string.Empty;
    public bool TestDnsCheck { get; private set; } = true;
    public bool TestReplication { get; private set; } = true;
    public bool TestTimeSkew { get; private set; } = true;
    public bool TestLdapBind { get; private set; } = true;
    public bool TestCertDhcp { get; private set; } = true;
    public bool TestSmbLdapSigning { get; private set; } = true;

    public TestParametersWindow()
    {
        appStateStore = AppStateStore.CreateDefault();
        appStateStore.Initialize();
        InitializeComponent();
        Loaded += Window_Loaded;
        LoadSettings();
    }

    public TestParametersWindow(string currentDcs, string currentEmail) : this()
    {
        DomainControllers = currentDcs ?? string.Empty;
        RecipientEmail = currentEmail ?? string.Empty;
        dcTextBox.Text = DomainControllers;
        emailTextBox.Text = RecipientEmail;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
    }

    private void LoadSettings()
    {
        PersistedAppSettings settings = appStateStore.LoadSettings();
        dcTextBox.Text = settings.DomainControllers;
        emailTextBox.Text = settings.RecipientEmail;
        chkDnsCheck.IsChecked = settings.TestDnsCheck;
        chkReplication.IsChecked = settings.TestReplication;
        chkTimeSkew.IsChecked = settings.TestTimeSkew;
        chkLdapBind.IsChecked = settings.TestLdapBind;
        chkCertDhcp.IsChecked = settings.TestCertDhcp;
        chkSmbLdapSigning.IsChecked = settings.TestSmbLdapSigning;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        DomainControllers = dcTextBox.Text.Trim();
        RecipientEmail = emailTextBox.Text.Trim();
        TestDnsCheck = chkDnsCheck.IsChecked ?? true;
        TestReplication = chkReplication.IsChecked ?? true;
        TestTimeSkew = chkTimeSkew.IsChecked ?? true;
        TestLdapBind = chkLdapBind.IsChecked ?? true;
        TestCertDhcp = chkCertDhcp.IsChecked ?? true;
        TestSmbLdapSigning = chkSmbLdapSigning.IsChecked ?? true;

        PersistedAppSettings settings = appStateStore.LoadSettings();
        settings.DomainControllers = DomainControllers;
        settings.RecipientEmail = RecipientEmail;
        settings.TestDnsCheck = TestDnsCheck;
        settings.TestReplication = TestReplication;
        settings.TestTimeSkew = TestTimeSkew;
        settings.TestLdapBind = TestLdapBind;
        settings.TestCertDhcp = TestCertDhcp;
        settings.TestSmbLdapSigning = TestSmbLdapSigning;
        appStateStore.SaveSettings(settings);

        NotificationService.Show(this, "Success", "Settings saved successfully!");
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DomainControllers = dcTextBox.Text;
        RecipientEmail = emailTextBox.Text;
        DialogResult = false;
        Close();
    }

    private async void TestEmailButton_Click(object sender, RoutedEventArgs e)
    {
        string recipient = emailTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(recipient))
        {
            NotificationService.Show(this, "Email Required", "Please enter a recipient email address first.", isError: true);
            return;
        }

        TestEmailButton.IsEnabled = false;
        TestEmailButton.Content = "Sending...";

        try
        {
            await RunWithLoadingWindowAsync(
                "Sending test email",
                "Connecting to SMTP and sending the verification email.",
                () => Task.Run(() =>
                {
                    using SmtpClient client = new();
                    using MailMessage message = new()
                    {
                        From = new MailAddress(recipient),
                        Subject = "AD Guardian - Test Email",
                        Body = "This is a test email from AD Guardian. If you received this, your email configuration is working correctly."
                    };
                    message.To.Add(recipient);
                    client.Send(message);
                }));

            NotificationService.Show(this, "Success", "Test email sent successfully!");
        }
        catch (Exception ex)
        {
            NotificationService.Show(this, "Send Failed", $"Failed to send test email:\n{ex.Message}", isError: true);
        }
        finally
        {
            TestEmailButton.IsEnabled = true;
            TestEmailButton.Content = "Send Test Email";
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
                    Owner = this
                };
                IsEnabled = false;
                loadingWindow.Show();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
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
