using Leo.Core;
using Microsoft.Win32;

namespace Leo.Service.Setup;

/// <summary>
/// <c>LEO.Service.exe remove-dev-install</c> (LEO.md §24.2): run by the MSI (deferred, as SYSTEM) before it installs
/// its own service. A service named "LEO" that isn't the one in this install folder (the old
/// <c>tools\dev-service.ps1 deploy</c> layout, <c>…\LEO\Service\LEO.Service.exe</c>) is removed the way
/// <c>LEO.Service.exe uninstall</c> does it: stop (metrics handed back, §5.9), delete, LEO's firewall group. The dev
/// folder is deleted. Config in ProgramData is kept and used by the new service.
/// </summary>
internal static class DevInstallRemover
{
    public static int Run(TextWriter output)
    {
        var imagePath = ServiceImagePath();
        if (imagePath is not null && ShouldRemove(imagePath, AppContext.BaseDirectory))
        {
            output.WriteLine("LEO: removing a development service registration");
            var rc = ServiceInstaller.RemoveService(output);
            if (rc != 0) output.WriteLine($"LEO: removing the development service returned {rc}");
        }

        if (Directory.Exists(LeoPaths.DevServiceDir) && !LeoPaths.IsInside(AppContext.BaseDirectory, LeoPaths.DevServiceDir))
        {
            try
            {
                Directory.Delete(LeoPaths.DevServiceDir, recursive: true);
                output.WriteLine("LEO: development folder removed");
            }
            catch (Exception ex) { output.WriteLine($"LEO: development folder not removed ({ex.GetType().Name})"); }
        }
        return 0;
    }

    /// <summary>A registered "LEO" service that runs an exe outside this install folder is a dev (or stale) one.</summary>
    internal static bool ShouldRemove(string serviceImagePath, string installDir)
    {
        var exe = serviceImagePath.Trim();
        if (exe.StartsWith('"'))
        {
            var end = exe.IndexOf('"', 1);
            exe = end > 1 ? exe[1..end] : exe.Trim('"');
        }
        else
        {
            var i = exe.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (i > 0) exe = exe[..(i + 4)];
        }
        return !LeoPaths.IsInside(exe, installDir) || LeoPaths.IsInside(exe, Path.Combine(installDir, "Service"));
    }

    private static string? ServiceImagePath()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{LeoInfo.ServiceName}");
        return key?.GetValue("ImagePath") as string;
    }
}
