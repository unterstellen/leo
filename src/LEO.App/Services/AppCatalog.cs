using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Leo.Core.SplitTunnel;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace Leo.App.Services;

/// <summary>An app the user can pick for a rule.</summary>
public sealed record AppCandidate(string Name, string Path, AppMatchKind Kind, string Detail);

/// <summary>Finds apps for the Apps page pickers (runs as the signed-in user, in the UI process).</summary>
public static class AppCatalog
{
    /// <summary>Programs with network connections right now, plus programs with an open window.</summary>
    public static List<AppCandidate> RunningApps()
    {
        var netPids = NetworkPids();
        var result = new Dictionary<string, AppCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id is 0 or 4) continue;
                var hasNet = netPids.Contains(p.Id);
                bool hasWindow;
                try { hasWindow = p.MainWindowHandle != IntPtr.Zero; } catch { hasWindow = false; }
                if (!hasNet && !hasWindow) continue;
                var path = ImagePath(p.Id);
                if (path is null || result.ContainsKey(path)) continue;
                result[path] = new AppCandidate(FriendlyName(path), path, AppMatchKind.ExactPath, hasNet ? "Using the network now" : "Running");
            }
        }
        return [.. result.Values.OrderByDescending(a => a.Detail.StartsWith("Using", StringComparison.Ordinal)).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Start-menu apps (exe shortcuts) and Microsoft Store apps (by install folder).</summary>
    public static async Task<List<AppCandidate>> InstalledAppsAsync()
    {
        var result = new Dictionary<string, AppCandidate>(StringComparer.OrdinalIgnoreCase);

        await Task.Run(() =>
        {
            var dirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            };
            var shell = Type.GetTypeFromProgID("WScript.Shell") is { } t ? Activator.CreateInstance(t) : null;
            if (shell is null) return;
            try
            {
                foreach (var lnk in dirs.Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*.lnk", SearchOption.AllDirectories)))
                {
                    try
                    {
                        dynamic sc = ((dynamic)shell).CreateShortcut(lnk);
                        string target = sc.TargetPath;
                        Marshal.FinalReleaseComObject(sc);
                        if (string.IsNullOrEmpty(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) continue;
                        var name = System.IO.Path.GetFileNameWithoutExtension(lnk);
                        if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)) continue;
                        result.TryAdd(target, new AppCandidate(name, target, AppMatchKind.ExactPath, "Desktop app"));
                    }
                    catch { /* broken shortcut */ }
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        });

        try
        {
            var pm = new Windows.Management.Deployment.PackageManager();
            foreach (var pkg in pm.FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (pkg.IsFramework || pkg.IsResourcePackage || pkg.IsBundle) continue;
                    var entries = await pkg.GetAppListEntriesAsync();
                    if (entries.Count == 0) continue;
                    var folder = pkg.InstalledLocation?.Path;
                    if (string.IsNullOrEmpty(folder)) continue;
                    var name = entries[0].DisplayInfo.DisplayName;
                    result.TryAdd(folder, new AppCandidate(string.IsNullOrWhiteSpace(name) ? pkg.DisplayName : name, folder, AppMatchKind.Folder, "Microsoft Store app"));
                }
                catch { /* inaccessible package */ }
            }
        }
        catch { /* package API unavailable */ }

        return [.. result.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>File description (e.g. "Google Chrome") or the file name.</summary>
    public static string FriendlyName(string exePath)
    {
        try
        {
            var desc = FileVersionInfo.GetVersionInfo(exePath).FileDescription;
            if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
        }
        catch { }
        return System.IO.Path.GetFileNameWithoutExtension(exePath);
    }

    // ── Icons ────────────────────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, BitmapImage?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The program's icon (UI thread), cached; null when there isn't one.</summary>
    public static async Task<BitmapImage?> IconAsync(string exePath)
    {
        if (IconCache.TryGetValue(exePath, out var cached)) return cached;
        BitmapImage? image = null;
        try
        {
            var bytes = await Task.Run(() =>
            {
                if (!File.Exists(exePath)) return null;
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon is null) return null;
                using var bmp = icon.ToBitmap();
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            });
            if (bytes is not null)
            {
                image = new BitmapImage();
                using var stream = new MemoryStream(bytes);
                await image.SetSourceAsync(stream.AsRandomAccessStream());
            }
        }
        catch { image = null; }
        IconCache[exePath] = image;
        return image;
    }

    // ── Windows connection tables (owner PIDs) ───────────────────────────────

    private static HashSet<int> NetworkPids()
    {
        var pids = new HashSet<int>();
        Collect(pids, true, 2, 24, 20);   // TCP v4: MIB_TCPROW_OWNER_PID, pid at +20
        Collect(pids, true, 23, 56, 52);  // TCP v6: MIB_TCP6ROW_OWNER_PID, pid at +52
        Collect(pids, false, 2, 12, 8);   // UDP v4: MIB_UDPROW_OWNER_PID, pid at +8
        Collect(pids, false, 23, 28, 24); // UDP v6: MIB_UDP6ROW_OWNER_PID, pid at +24
        pids.Remove(0);
        pids.Remove(4);
        return pids;
    }

    private static void Collect(HashSet<int> pids, bool tcp, int family, int rowSize, int pidOffset)
    {
        var size = 0;
        _ = tcp ? GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 5, 0) : GetExtendedUdpTable(IntPtr.Zero, ref size, false, family, 1, 0);
        if (size <= 0) return;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            var rc = tcp ? GetExtendedTcpTable(buf, ref size, false, family, 5, 0) : GetExtendedUdpTable(buf, ref size, false, family, 1, 0);
            if (rc != 0) return;
            var count = Marshal.ReadInt32(buf);
            for (var i = 0; i < count; i++) pids.Add(Marshal.ReadInt32(buf + 4 + i * rowSize + pidOffset));
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static string? ImagePath(int pid)
    {
        using var h = OpenProcess(0x1000, false, (uint)pid);
        if (h.IsInvalid) return null;
        var sb = new StringBuilder(1024);
        var size = sb.Capacity;
        return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
    }

    [DllImport("iphlpapi.dll")] private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);
    [DllImport("iphlpapi.dll")] private static extern int GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
}
