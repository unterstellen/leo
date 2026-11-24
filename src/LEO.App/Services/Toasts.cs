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

    /// <summary>
    /// Register before the first toast. <paramref name="onInvoked"/> runs when the user clicks one: with the button's
    /// action ("update", "later", "notes") or null for the toast itself.
    /// </summary>
    public static void Register(Action<string?> onInvoked)
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, e) =>
                onInvoked(e.Arguments.TryGetValue("action", out var action) ? action : null);
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
            var builder = new AppNotificationBuilder()
                .AddArgument("kind", n.Kind.ToString())
                .AddText(n.Title)
                .AddText(n.Message);
            // M8: an update the user installs by hand (automatic installing off), LEO.md §24.4.
            if (n.Kind == NotificationKind.UpdateAvailable)
            {
                builder.AddButton(new AppNotificationButton("Update now").AddArgument("action", "update"));
                builder.AddButton(new AppNotificationButton("Later").AddArgument("action", "later"));
            }
            else if (n.Kind == NotificationKind.Updated)
            {
                builder.AddButton(new AppNotificationButton("Release notes").AddArgument("action", "notes"));
            }
            var toast = builder.BuildNotification();
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
