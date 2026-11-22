using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Leo.Core.SplitTunnel;
using Microsoft.Win32.SafeHandles;

namespace Leo.Service.SplitTunnel;

/// <summary>
/// "Include programs it starts" (LEO.md §17): the engine matches by program path only, so LEO follows the
/// process tree of every ruled app and turns each new child program (a game started by a launcher, an updater…)
/// into a child rule for the parent's network. Children that have their own rule keep it.
/// </summary>
public sealed class ChildProcessTracker(ILogger<ChildProcessTracker> log) : IDisposable
{
    private const int MaxChildren = 300;

    private readonly Lock _gate = new();
    private readonly Dictionary<int, string> _trackedPids = []; // pid → rule id
    private readonly Dictionary<string, ChildProgram> _children = new(StringComparer.OrdinalIgnoreCase);
    private List<AppRule> _rules = [];
    private ManagementEventWatcher? _watcher;

    /// <summary>Raised (on a thread-pool thread) when a new child program was discovered.</summary>
    public event EventHandler? ChildrenChanged;

    public IReadOnlyList<ChildProgram> Children
    {
        get { lock (_gate) return [.. _children.Values]; }
    }

    /// <summary>Updates the rule set; (re)starts or stops watching as needed.</summary>
    public void SetRules(IReadOnlyList<AppRule> rules)
    {
        var active = rules.Where(r => r.Enabled && r.IncludeChildren).ToList();
        lock (_gate)
        {
            _rules = [.. rules.Where(r => r.Enabled)];
            var ids = active.Select(r => r.Id).ToHashSet();
            foreach (var k in _children.Where(c => !ids.Contains(c.Value.ParentRuleId)).Select(c => c.Key).ToList()) _children.Remove(k);
            foreach (var k in _trackedPids.Where(p => !ids.Contains(p.Value)).Select(p => p.Key).ToList()) _trackedPids.Remove(k);
        }

        if (active.Count == 0)
        {
            StopWatching();
            return;
        }
        StartWatching();
        ScanExisting(active);
    }

    private void StartWatching()
    {
        if (_watcher is not null) return;
        try
        {
            _watcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
            _watcher.EventArrived += OnProcessStarted;
            _watcher.Start();
            log.LogInformation("Watching for programs started by ruled apps");
        }
        catch (Exception ex)
        {
            _watcher = null;
            log.LogWarning("Can't watch process starts ({Message}); child programs won't be routed", ex.Message);
        }
    }

    private void StopWatching()
    {
        if (_watcher is null) return;
        try { _watcher.Stop(); } catch { }
        _watcher.Dispose();
        _watcher = null;
    }

    /// <summary>Seeds the tree from processes already running (ruled apps started before LEO).</summary>
    private void ScanExisting(IReadOnlyList<AppRule> active)
    {
        try
        {
            var procs = new List<(int Pid, int Parent, string? Path)>();
            using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, ExecutablePath FROM Win32_Process"))
            {
                foreach (ManagementObject o in searcher.Get())
                {
                    using (o) procs.Add((Convert.ToInt32(o["ProcessId"]), Convert.ToInt32(o["ParentProcessId"]), o["ExecutablePath"] as string));
                }
            }

            var byParent = procs.ToLookup(p => p.Parent);
            var changed = false;
            foreach (var root in procs.Where(p => p.Path is not null))
            {
                var rule = active.FirstOrDefault(r => AppRuleMatcher.Matches(r, root.Path!));
                if (rule is null) continue;
                var stack = new Stack<int>([root.Pid]);
                while (stack.Count > 0)
                {
                    var pid = stack.Pop();
                    lock (_gate) _trackedPids[pid] = rule.Id;
                    foreach (var child in byParent[pid])
                    {
                        if (child.Pid == pid) continue;
                        if (child.Path is not null) changed |= AddChild(child.Path, rule);
                        stack.Push(child.Pid);
                    }
                }
            }
            if (changed) ChildrenChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Initial process scan failed");
        }
    }

    private void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var pid = Convert.ToInt32(e.NewEvent["ProcessID"]);
            var parent = Convert.ToInt32(e.NewEvent["ParentProcessID"]);
            var path = ImagePath(pid);

            AppRule? viaParent = null;
            AppRule? asRoot = null;
            lock (_gate)
            {
                if (_trackedPids.TryGetValue(parent, out var ruleId)) viaParent = _rules.FirstOrDefault(r => r.Id == ruleId);
                if (path is not null) asRoot = _rules.FirstOrDefault(r => r.IncludeChildren && AppRuleMatcher.Matches(r, path));
                PruneIfLarge();
            }

            var rule = asRoot ?? viaParent;
            if (rule is null) return;
            lock (_gate) _trackedPids[pid] = rule.Id;
            if (asRoot is null && path is not null && AddChild(path, rule)) ChildrenChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Process start event failed");
        }
    }

    /// <summary>Adds a child program unless a rule already covers it. Returns true if the list changed.</summary>
    private bool AddChild(string path, AppRule parentRule)
    {
        lock (_gate)
        {
            if (AppRuleMatcher.FindBest(_rules, path) is not null) return false; // its own rule (or the parent's) applies
            // Child programs are routed by path (everywhere they run), so shared Windows tools (cmd, PowerShell,
            // curl, ping, conhost...) are never picked up this way: one launcher would drag them all along.
            if (IsWindowsProgram(path)) return false;
            if (_children.ContainsKey(path) || _children.Count >= MaxChildren) return false;
            _children[path] = new ChildProgram(path, parentRule.Id);
        }
        log.LogInformation("Child program of '{Rule}' routed with it: {Path}", parentRule.DisplayName, path);
        return true;
    }

    private static readonly string WindowsDir = AppRuleMatcher.NormalizeFolder(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + "\\";

    internal static bool IsWindowsProgram(string path) =>
        path.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase);

    private void PruneIfLarge()
    {
        if (_trackedPids.Count < 5000) return;
        var alive = System.Diagnostics.Process.GetProcesses().Select(p => { using (p) return p.Id; }).ToHashSet();
        foreach (var pid in _trackedPids.Keys.Where(k => !alive.Contains(k)).ToList()) _trackedPids.Remove(pid);
    }

    private static string? ImagePath(int pid)
    {
        using var h = OpenProcess(0x1000, false, (uint)pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h.IsInvalid) return null;
        var sb = new StringBuilder(1024);
        var size = sb.Capacity;
        return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
    }

    public void Dispose() => StopWatching();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
}
