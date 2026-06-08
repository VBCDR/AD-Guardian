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
<!DOCTYPE html>
<html>
  <head>
    <meta charset='utf-8'/>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'/>
    <style>
      body {{
        font-family: 'Segoe UI', -apple-system, BlinkMacSystemFont, Roboto, Arial, sans-serif;
        font-size: 14px;
        color: #1a1a2e;
        margin: 0;
        padding: 0;
        background-color: #f0f4f8;
        -webkit-text-size-adjust: 100%;
        -ms-text-size-adjust: 100%;
      }}
      table {{
        border-collapse: collapse;
        mso-table-lspace: 0pt;
        mso-table-rspace: 0pt;
      }}
      img {{
        border: 0;
        line-height: 100%;
        outline: none;
        text-decoration: none;
        -ms-interpolation-mode: bicubic;
      }}
      .wrapper {{
        max-width: 640px;
        margin: 0 auto;
        padding: 24px 16px;
        width: 100% !important;
      }}
      .card {{
        background: #ffffff;
        border-radius: 12px;
        box-shadow: 0 1px 3px rgba(0,0,0,0.08), 0 1px 2px rgba(0,0,0,0.06);
        overflow: hidden;
        width: 100% !important;
      }}
      .header-bar {{
        background: linear-gradient(135deg, {headerBg}, #ffffff);
        border-left: 5px solid {headerColor};
        padding: 20px 24px;
      }}
      .header-title {{
        font-size: 18px;
        font-weight: 700;
        color: {headerColor};
        letter-spacing: -0.2px;
      }}
      .header-time {{
        font-size: 12px;
        color: #64748b;
        margin-top: 4px;
      }}
      .status-badge {{
        display: inline-block;
        padding: 6px 14px;
        border-radius: 20px;
        font-size: 13px;
        font-weight: 600;
        background: {headerBg};
        color: {headerColor};
        margin: 20px 24px 0;
      }}
      .content {{
        padding: 16px 24px 24px;
      }}
      .details {{
        background-color: #f8fafc;
        padding: 16px 18px;
        border: 1px solid #e2e8f0;
        border-radius: 8px;
        margin: 12px 0;
        font-size: 13px;
        line-height: 1.6;
        color: #334155;
      }}
      .details p {{
        margin: 0 0 8px 0;
      }}
      .table-wrap {{
        overflow-x: auto;
        -webkit-overflow-scrolling: touch;
        margin: 12px 0;
        width: 100%;
      }}
      .cta {{
        display: inline-block;
        padding: 10px 20px;
        background: {headerColor};
        color: #ffffff;
        text-decoration: none;
        border-radius: 6px;
        font-weight: 600;
        font-size: 13px;
        margin-top: 8px;
      }}
      .footer {{
        font-size: 12px;
        color: #94a3b8;
        text-align: center;
        padding: 16px 24px;
        border-top: 1px solid #f1f5f9;
      }}
      .footer a {{
        color: #64748b;
        text-decoration: none;
      }}
      @media screen and (max-width: 480px) {{
        .wrapper {{
          padding: 12px 8px !important;
        }}
        .header-bar {{
          padding: 16px 16px !important;
        }}
        .header-title {{
          font-size: 16px !important;
        }}
        .status-badge {{
          margin: 16px 16px 0 !important;
          font-size: 12px !important;
          padding: 5px 12px !important;
        }}
        .content {{
          padding: 12px 16px 20px !important;
        }}
        .details {{
          padding: 12px 14px !important;
          font-size: 13px !important;
        }}
        .table-wrap {{
          margin: 12px -16px !important;
          padding: 0 16px !important;
        }}
        .table-wrap table {{
          min-width: 480px !important;
        }}
        .footer {{
          padding: 12px 16px !important;
          font-size: 11px !important;
        }}
      }}
    </style>
  </head>
  <body>
    <div class='wrapper'>
      <div class='card'>
        <div class='header-bar'>
          <div class='header-title'>{subject}</div>
          <div class='header-time'>{DateTime.Now:f}</div>
        </div>
        <div class='status-badge'>{(isFailed ? "\u2717 FAILED" : "\u2713 PASSED")}</div>
        <div class='content'>
          <p style='color:#475569;margin:0 0 4px 0;'>{statusText}</p>
          <div class='details'>
            {bodyDetail}
          </div>
          <p style='color:#64748b;font-size:13px;margin:12px 0 0 0;'>Please review the attached log file for detailed results.</p>
        </div>
        <div class='footer'>
          Automated message from <strong>AD Guardian</strong> &middot; Domain health monitoring
        </div>
      </div>
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
