using System.Windows;

namespace AdHealthMonitor;

public static class NotificationService
{
    /// <summary>Shows a modal notification dialog. Must be called from the UI thread; the call blocks until the dialog is dismissed.</summary>
    /// <param name="autoDismissSeconds">If &gt; 0, the dialog auto-closes after this many seconds; before that it surfaces a "Auto-closing in Ns — click OK to keep this open" hint. If 0 (default) the user must click OK.</param>
    public static void Show(Window? owner, string title, string message, bool isError = false, int autoDismissSeconds = 0)
    {
        SuccessNotification notification = new(title, message, isError, autoDismissSeconds);
        if (owner != null)
        {
            notification.Owner = owner;
        }

        notification.ShowDialog();
    }
}
