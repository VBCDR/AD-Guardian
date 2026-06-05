// MainWindow partial class - Email functionality
// Extracted from MainWindow.xaml.cs during partial class refactoring.

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Navigation;
using System.Windows.Threading;
using Domain_Guardian;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace AdHealthMonitor;

public partial class MainWindow
{
    internal async void TestEmailButton_Click(object sender, RoutedEventArgs e)
    {
        string recipient = SettingsEmailTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(recipient))
        {
            new SuccessNotification("Email Required", "Please enter a recipient email address first.", isError: true).ShowDialog();
            return;
        }

        SettingsTestEmailButton.IsEnabled = false;
        SettingsTestEmailButton.Content = "Sending...";

        try
        {
            await RunWithLoadingWindowAsync(
                "Sending test email",
                "Connecting to SMTP and sending the verification email.",
                () => SendConfiguredTestEmailAsync(recipient)).ConfigureAwait(true);

            new SuccessNotification("Email Sent", "Test email sent successfully!").ShowDialog();
        }
        catch (Exception ex)
        {
            new SuccessNotification("Email Failed", $"Failed to send test email:\n{ex.Message}", isError: true).ShowDialog();
        }
        finally
        {
            SettingsTestEmailButton.IsEnabled = true;
            SettingsTestEmailButton.Content = "Send Test Email";
        }
    }

    private async Task SendScheduledEmailSafelyAsync(string subject, string bodyDetail, string attachmentPath)
    {
        Task emailTask = Task.Run(() => SendEmailWithAttachment(subject, bodyDetail, attachmentPath));
        Task completedTask = await Task.WhenAny(emailTask, Task.Delay(ScheduledEmailTimeout)).ConfigureAwait(true);

        if (completedTask == emailTask)
        {
            await emailTask.ConfigureAwait(true);
            return;
        }

        Debug.WriteLine($"Scheduled email send timed out after {ScheduledEmailTimeout.TotalSeconds:0} seconds.");
    }

    private static Task SendConfiguredTestEmailAsync(string recipient)
    {
        return Task.Run(() =>
        {
            using SmtpClient client = new("smtp.gmail.com", 587)
            {
                Credentials = new NetworkCredential("adguardianutility@gmail.com", "ihai btfi qeja nbqp"),
                EnableSsl = true
            };
            using MailMessage message = new("adguardianutility@gmail.com", recipient)
            {
                Subject = "AD Guardian - Test Email",
                Body = "This is a test email from AD Guardian. If you received this, your email configuration is working correctly."
            };
            client.Send(message);
        });
    }

    private void SendEmailWithAttachment(string subject, string bodyDetail, string attachmentPath)
    {
        try
        {
            string toAddress = recipientEmail;
            string fingerprint = string.Join("|", toAddress, subject, attachmentPath ?? string.Empty, bodyDetail ?? string.Empty);
            DateTime nowUtc = DateTime.UtcNow;
            if (string.Equals(lastEmailFingerprint, fingerprint, StringComparison.Ordinal) &&
                (nowUtc - lastEmailSentUtc) < TimeSpan.FromSeconds(30))
            {
                Debug.WriteLine("Duplicate email send suppressed.");
                return;
            }

            using MailMessage mail = new("ADGuardian@funasset.com", toAddress)
            {
                Subject = subject,
                IsBodyHtml = true
            };

            bool isFailed = subject.Contains("[FAILED]", StringComparison.OrdinalIgnoreCase);
            string headerColor = isFailed ? "#C62828" : "#2E7D32";
            string headerBg = isFailed ? "#FFEBEE" : "#E8F5E9";
            string statusText = isFailed ? "Some tests failed — review the details below." : "All tests completed successfully.";

            string htmlBody = $@"
<html>
  <head>
    <style>
      body {{
        font-family: 'Segoe UI', Arial, sans-serif;
        font-size: 14px;
        color: #333;
        margin: 0;
        padding: 0;
      }}
      .header-bar {{
        background-color: {headerBg};
        border-left: 5px solid {headerColor};
        padding: 14px 18px;
        margin-bottom: 16px;
        border-radius: 4px;
      }}
      .header-title {{
        font-size: 17px;
        font-weight: bold;
        color: {headerColor};
      }}
      .header-time {{
        font-size: 12px;
        color: #666;
        margin-top: 2px;
      }}
      .content {{
        margin-bottom: 15px;
        padding: 0 4px;
      }}
      .details {{
        background-color: #f7f7f7;
        padding: 12px 14px;
        border: 1px solid #ddd;
        border-radius: 5px;
      }}
      .footer {{
        font-size: 12px;
        color: #777;
        margin-top: 20px;
        border-top: 1px solid #eee;
        padding-top: 10px;
      }}
    </style>
  </head>
  <body>
    <div class='header-bar'>
      <div class='header-title'>{subject}</div>
      <div class='header-time'>{DateTime.Now:f}</div>
    </div>
    <div class='content'>
      <p>{statusText}</p>
      <div class='details'>
         {bodyDetail}
      </div>
      <p>Please review the attached log file for detailed information.</p>
    </div>
    <div class='footer'>
      This is an automated message from AD Guardian.
    </div>
  </body>
</html>";

            mail.Body = htmlBody;
            if (!string.IsNullOrWhiteSpace(attachmentPath) && File.Exists(attachmentPath))
            {
                mail.Attachments.Add(new Attachment(attachmentPath));
            }

            using SmtpClient client = new("smtp.gmail.com", 587)
            {
                Credentials = new NetworkCredential("adguardianutility@gmail.com", "ihai btfi qeja nbqp"),
                EnableSsl = true,
                Timeout = (int)ScheduledEmailTimeout.TotalMilliseconds
            };
            client.Send(mail);
            lastEmailFingerprint = fingerprint;
            lastEmailSentUtc = nowUtc;
        }
        catch (Exception ex)
        {
            if (isScheduledLaunch)
            {
                Debug.WriteLine("Failed to send scheduled email: " + ex);
            }
            else
            {
                NotificationService.Show(this, "Email Error", "Failed to send email: " + ex.Message, isError: true);
            }
        }
    }
}
