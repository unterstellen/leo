using Leo.Ipc;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Leo.App.Services;

/// <summary>
/// Windows notifications (LEO.md §6.10) via the Windows App SDK. The service raises events; the tray app is
/// the one that shows them, because a session-0 service can't.
/// </summary>
public static class Toasts
{
    private static bool _registered;

    /// <summary>Register before the first toast. <paramref name="onClicked"/> runs when the user clicks one.</summary>
    public static void Register(Action onClicked)
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, _) => onClicked();
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            UiLog.Write("Notification registration failed; toasts disabled", ex);
        }
    }

    public static void Show(LeoNotification n)
    {
        if (!_registered) return;
        try
        {
            var toast = new AppNotificationBuilder()
                .AddArgument("kind", n.Kind.ToString())
                .AddText(n.Title)
                .AddText(n.Message)
                .BuildNotification();
            toast.Group = "leo";
            toast.Tag = n.Kind.ToString(); // a newer failover toast replaces the previous one
            AppNotificationManager.Default.Show(toast);
        }
        catch (Exception ex)
        {
            UiLog.Write("Showing a notification failed", ex);
        }
    }

    public static void Unregister()
    {
        if (!_registered) return;
        try { AppNotificationManager.Default.Unregister(); } catch { }
        _registered = false;
    }
}
