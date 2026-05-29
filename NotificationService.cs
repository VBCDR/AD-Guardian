using System.Windows;

namespace AdHealthMonitor;

public static class NotificationService
{
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
