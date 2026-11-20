using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Leo.App;

/// <summary>
/// Custom entry point: LEO's UI is single-instance. A second launch (Start menu, shortcut, Run key)
/// hands its activation to the running instance, which then shows its window.
/// </summary>
public static class Program
{
    private const string InstanceKey = "UNTERSTELLEN.LEO.App";

    /// <summary>Launched by the HKCU Run entry: start minimized to the tray.</summary>
    public static bool StartInTray { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        StartInTray = args.Contains(StartupManager.TrayArgument, StringComparer.OrdinalIgnoreCase);

        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!instance.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            // Don't block the STA thread on the WinRT async op.
            Task.Run(() => instance.RedirectActivationToAsync(activation).AsTask()).Wait();
            return 0;
        }

        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            var app = new App();
            instance.Activated += (_, _) => app.OnRedirectedActivation();
        });
        return 0;
    }
}
