using Microsoft.Win32;
using Leo.Core;

namespace Leo.App;

/// <summary>
/// "Start LEO at login" (LEO.md §7): a per-user HKCU Run entry that launches the UI into the tray.
/// This controls the UI only — the LEO service always starts at boot on its own.
/// </summary>
public static class StartupManager
{
    public const string TrayArgument = "--tray";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly string ValueName = LeoInfo.ProductName;

    private static string Command => $"\"{Environment.ProcessPath}\" {TrayArgument}";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value &&
                   value.Equals(Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
