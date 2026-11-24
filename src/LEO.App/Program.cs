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
        // DLLs only from System32 and the install folder (LEO.md Â§24.6).
        // Not SetDefaultDllDirectories: it breaks WinUI resource loading (themeresources.xaml not found, found in M8).
        // Removing the current directory from the DLL search is the part that matters for an app in Program Files.
        Leo.Core.Security.DllSearch.RemoveCurrentDirectory();
        // The Start menu shortcut carries the same AppUserModelID, so toasts and the taskbar group are LEO's (Â§24.2).
        SetCurrentProcessExplicitAppUserModelID(Leo.Core.LeoInfo.AppUserModelId);

        StartInTray = args.Contains(StartupManager.TrayArgument, StringComparer.OrdinalIgnoreCase);

        // The MSI's machine-wide login entry starts LEO for every user; a user who switched it off exits here (Â§24.2).
        if (StartupManager.ShouldExitAtTrayStart(Services.UiSettings.Load().StartAtLogin, StartInTray)) return 0;

        WinRT.ComWrappersSupport.InitializeComWrappers();

        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!instance.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            // Don't block the STA thread on the WinRT async op.
            Task.Run(() => instance.RedirectActivationToAsync(activation).AsTask()).Wait();
            return 0;
        }

        // An update (MSI + Restart Manager) closes LEO and starts it again in the tray afterwards (Â§24.2).
        // Only restarts asked for by Restart Manager: not after a crash, a hang or a reboot.
        RegisterApplicationRestart(StartupManager.TrayArgument, RestartNoCrash | RestartNoHang | RestartNoReboot);

        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            var app = new App();
            instance.Activated += (_, _) => app.OnRedirectedActivation();
        });
        return 0;
    }

    private const int RestartNoCrash = 1;
    private const int RestartNoHang = 2;
    private const int RestartNoReboot = 8;

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string commandLine, int flags);
}
