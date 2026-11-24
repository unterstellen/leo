using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Leo.Core;
using Leo.Core.Config;
using Leo.Core.Setup;
using Leo.Service.Interop;
using Microsoft.Win32;

namespace Leo.Service.Setup;

/// <summary>
/// <c>LEO.Service.exe cleanup</c> (LEO.md §24.5): run by the MSI as SYSTEM after the service has stopped, on a full
/// uninstall only (never on upgrade). Puts Windows networking back the way it was and deletes ALL of LEO's settings
/// and data: ProgramData\UNTERSTELLEN\LEO and every user's LocalAppData\UNTERSTELLEN\LEO.
/// Every step is best effort and the exit code is 0, so a failed step never blocks an uninstall.
/// The output names counts and ids only (it ends up in the MSI log).
/// </summary>
internal static class UninstallCleanup
{
    public static int Run(TextWriter output)
    {
        var plan = Plan(output);
        Execute(plan, output);
        return 0;
    }

    private static CleanupPlan Plan(TextWriter output)
    {
        LeoConfig? config = null;
        try
        {
            if (File.Exists(LeoPaths.ConfigFile)) config = new ConfigStore(LeoPaths.ConfigFile).Read(c => c);
        }
        catch (Exception ex) { output.WriteLine($"LEO cleanup: config unreadable ({ex.GetType().Name}); generic steps only"); }

        return CleanupPlan.Build(config, ScanAdapters(), SplitTunnel.MihomoEngine.TunAdapterPresent(),
            LeoPaths.MachineData, UserProfiles(), upgrading: false);
    }

    private static void Execute(CleanupPlan plan, TextWriter output)
    {
        Step(output, "engine processes", () => $"{KillEngineProcesses()} stopped");

        Step(output, "metrics", () =>
        {
            foreach (var a in plan.RestoreAutomaticMetric)
            {
                IpHelper.RestoreAutomaticMetric(a.InterfaceIndex, AddressFamily.InterNetwork);
                IpHelper.RestoreAutomaticMetric(a.InterfaceIndex, AddressFamily.InterNetworkV6);
            }
            return $"automatic metric back on {plan.RestoreAutomaticMetric.Count} adapter(s)";
        });

        Step(output, "dns", () =>
        {
            foreach (var a in plan.RemoveEngineDns)
            {
                Exec("netsh.exe", $"interface ipv4 delete dnsservers name=\"{a.Name}\" address={CleanupPlan.EngineDns} validate=no");
            }
            return $"engine resolver removed from {plan.RemoveEngineDns.Count} adapter(s)";
        });

        Step(output, "dual link", () =>
        {
            var n = 0;
            if (plan.DualLinkOff.Count > 0)
            {
                using var wlan = new WlanApi();
                foreach (var id in plan.DualLinkOff)
                {
                    if (!Guid.TryParse(id, out var guid)) continue;
                    try { wlan.SetSynchronizedConnections(guid, false); n++; } catch { /* adapter gone */ }
                }
            }
            return $"off on {n} adapter(s)";
        });

        if (plan.RemoveFirewallGroup)
        {
            Step(output, "firewall", () => $"{FirewallRules.RemoveAll()} LEO rule(s) removed");
        }

        if (plan.RemoveEngineAdapter)
        {
            Step(output, "engine adapter", () => RemoveEngineAdapter() ? "removed" : "not found");
        }

        Step(output, "data", () =>
        {
            var removed = plan.DeleteFolders.Count(SafeDelete.DeleteTree);
            foreach (var dir in plan.DeleteIfEmpty) SafeDelete.DeleteIfEmpty(dir);
            return $"{removed} folder(s) removed";
        });
    }

    private static void Step(TextWriter output, string name, Func<string> action)
    {
        try { output.WriteLine($"LEO cleanup: {name}: {action()}"); }
        catch (Exception ex) { output.WriteLine($"LEO cleanup: {name} failed ({ex.GetType().Name}: {ex.Message})"); }
    }

    private static IReadOnlyList<CleanupAdapter> ScanAdapters()
    {
        var list = new List<CleanupAdapter>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var props = nic.GetIPProperties();
                var index = props.GetIPv4Properties()?.Index ?? -1;
                if (index < 0) continue;
                var automatic = IpHelper.GetMetric(index, AddressFamily.InterNetwork)?.Automatic ?? true;
                list.Add(new CleanupAdapter(nic.Id, index, nic.Name, automatic,
                    props.DnsAddresses.Select(d => d.ToString()).ToList()));
            }
            catch { /* adapter without IPv4 */ }
        }
        return list;
    }

    /// <summary>Profile folders of real users (S-1-5-21-…), from the registry's profile list.</summary>
    internal static IReadOnlyList<string> UserProfiles()
    {
        var result = new List<string>();
        using var list = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        if (list is null) return result;
        foreach (var sid in list.GetSubKeyNames().Where(s => s.StartsWith("S-1-5-21-", StringComparison.Ordinal)))
        {
            using var key = list.OpenSubKey(sid);
            if (key?.GetValue("ProfileImagePath") is string path && !string.IsNullOrWhiteSpace(path))
                result.Add(Environment.ExpandEnvironmentVariables(path));
        }
        return result;
    }

    private static int KillEngineProcesses()
    {
        var n = 0;
        foreach (var p in Process.GetProcessesByName("mihomo"))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path is not null && (LeoPaths.IsInside(path, AppContext.BaseDirectory) || LeoPaths.IsInside(path, LeoPaths.DevServiceDir)))
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                    n++;
                }
            }
            catch { /* gone or access denied */ }
            finally { p.Dispose(); }
        }
        return n;
    }

    /// <summary>Removes the one "LEO" engine adapter device an engine crash may have left (the Wintun driver stays).</summary>
    private static bool RemoveEngineAdapter()
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT PNPDeviceID FROM Win32_NetworkAdapter WHERE NetConnectionID = '{CleanupPlan.EngineAdapterName}'");
        foreach (var o in searcher.Get())
        {
            using (o)
            {
                if (o["PNPDeviceID"] is string id && id.Length > 0)
                {
                    var (rc, _) = Exec("pnputil.exe", $"/remove-device \"{id}\"");
                    return rc == 0;
                }
            }
        }
        return false;
    }

    internal static (int ExitCode, string Output) Exec(string file, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }
}

/// <summary>
/// Deletes LEO's folders as SYSTEM without ever following a junction or symlink (LEO.md §24.5): a user owns their
/// own LocalAppData, so a planted link must only lose the link itself, never what it points to.
/// </summary>
internal static class SafeDelete
{
    /// <summary>Deletes <paramref name="root"/> and everything below it. False when it didn't exist or was refused.</summary>
    public static bool DeleteTree(string root)
    {
        var dir = new DirectoryInfo(root);
        if (!dir.Exists) return false;
        if (HasReparseAncestor(dir)) return false; // e.g. C:\Users\x\AppData\Local\UNTERSTELLEN is a junction
        if (IsLink(dir))
        {
            dir.Delete(recursive: false); // the link only
            return true;
        }
        DeleteContents(dir);
        dir.Attributes = FileAttributes.Directory;
        dir.Delete(recursive: false);
        return true;
    }

    public static void DeleteIfEmpty(string path)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            if (dir.Exists && !IsLink(dir) && !HasReparseAncestor(dir) && !dir.EnumerateFileSystemInfos().Any())
                dir.Delete(recursive: false);
        }
        catch { /* in use or not empty */ }
    }

    private static void DeleteContents(DirectoryInfo dir)
    {
        foreach (var entry in dir.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo sub)
            {
                if (!IsLink(sub)) DeleteContents(sub);
                sub.Attributes = FileAttributes.Directory | (IsLink(sub) ? FileAttributes.ReparsePoint : 0);
                sub.Delete(recursive: false);
            }
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
        }
    }

    internal static bool IsLink(FileSystemInfo info) => info.Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>
    /// True when a folder strictly between the trusted base and <paramref name="dir"/> is a reparse point. The base is
    /// %ProgramData% for machine data, else the user's profile folder (the part before \AppData\); those may be
    /// redirected by an administrator, everything below them is LEO's or the user's.
    /// </summary>
    internal static bool HasReparseAncestor(DirectoryInfo dir) => HasReparseBetween(dir, TrustedBase(dir.FullName));

    internal static bool HasReparseBetween(DirectoryInfo dir, string? trustedBase)
    {
        if (trustedBase is null) return true; // not a place LEO deletes from
        var stop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedBase));
        for (var p = dir.Parent; p is not null; p = p.Parent)
        {
            if (string.Equals(Path.TrimEndingDirectorySeparator(p.FullName), stop, StringComparison.OrdinalIgnoreCase)) return false;
            if (p.Exists && IsLink(p)) return true;
        }
        return true; // never reached the base: refuse
    }

    private static string? TrustedBase(string path)
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (LeoPaths.IsInside(path, programData)) return programData;
        var i = path.IndexOf(@"\AppData\", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? path[..i] : null;
    }
}
