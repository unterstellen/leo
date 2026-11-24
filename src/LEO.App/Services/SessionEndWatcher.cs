using System.Runtime.InteropServices;

namespace Leo.App.Services;

/// <summary>
/// Closes LEO cleanly when Windows Restart Manager asks (an MSI update replacing LEO.exe, LEO.md §24.2), or when the
/// user signs out: WM_ENDSESSION reaches the main window even while it's hidden in the tray. The app registered
/// itself with <c>RegisterApplicationRestart("--tray")</c>, so the installer starts it again afterwards.
/// </summary>
internal static class SessionEndWatcher
{
    private const uint WmEndSession = 0x0016;
    private static SubclassProc? _proc; // kept alive for the window's lifetime
    private static Action? _onEnd;

    public static void Attach(IntPtr hwnd, Action onEnd)
    {
        if (hwnd == IntPtr.Zero || _proc is not null) return;
        _onEnd = onEnd;
        _proc = WndProc;
        try { SetWindowSubclass(hwnd, _proc, 1, IntPtr.Zero); }
        catch (Exception ex) { UiLog.Write("Couldn't watch for session end", ex); }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        if (msg == WmEndSession && wParam != IntPtr.Zero)
        {
            try { _onEnd?.Invoke(); } catch { /* closing anyway */ }
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id, IntPtr data);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
