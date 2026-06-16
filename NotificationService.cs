using System.Windows;

namespace AdHealthMonitor;

public static class NotificationService
{
    /// <summary>Shows a modal notification dialog. Must be called from the UI thread; the call blocks until the dialog is dismissed.</summary>
    public static void Show(Window? owner, string title, string message, bool isError = false)
    {
        SuccessNotification notification = new(title, message, isError);
        if (owner != null)
        {
            notification.Owner = owner;
        }

        notification.ShowDialog();
    }
}
