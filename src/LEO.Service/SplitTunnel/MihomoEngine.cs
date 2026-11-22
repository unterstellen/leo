using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Leo.Core;
using Leo.Core.SplitTunnel;
using Leo.Service.Interop;

namespace Leo.Service.SplitTunnel;

/// <summary>
/// Runs and controls the mihomo engine (LEO.md §17): writes its config, starts it in a kill-on-close job,
/// reloads/hot-switches through its loopback API, and stops it — always leaving Windows networking clean.
/// </summary>
public sealed class MihomoEngine(ILogger<MihomoEngine> log) : IDisposable
{
    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "engine", "mihomo.exe");
    public static string WorkDir => Path.Combine(LeoPaths.MachineData, "engine");
    private static string ConfigPath => Path.Combine(WorkDir, "config.yaml");
    private const string TunDeviceName = "LEO";

    private readonly Lock _gate = new();
    private Process? _process;
    private JobObject? _job;
    private HttpClient? _api;
    private bool _stopping;
    private Dictionary<string, string> _appliedSelections = new(StringComparer.Ordinal);
    private Dictionary<string, string> _writtenProviders = new(StringComparer.Ordinal);

    /// <summary>Raised when the engine exits without LEO asking it to.</summary>
    public event EventHandler<int>? UnexpectedExit;

    public bool IsInstalled => File.Exists(ExePath);
    public bool IsRunning { get { lock (_gate) return _process is { HasExited: false }; } }

    public async Task StartAsync(EnginePlan plan, CancellationToken ct)
    {
        if (!IsInstalled) throw new FileNotFoundException("The split-tunneling engine isn't installed.", ExePath);
        await StopAsync(ct);
        KillStrays();

        Directory.CreateDirectory(Path.Combine(WorkDir, "rules"));
        var port = FreeLoopbackPort();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        WriteFiles(plan, $"127.0.0.1:{port}", secret);

        var psi = new ProcessStartInfo(ExePath)
        {
            ArgumentList = { "-d", WorkDir, "-f", ConfigPath },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = WorkDir,
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => LogEngineLine(e.Data);
        process.ErrorDataReceived += (_, e) => LogEngineLine(e.Data);
        process.Exited += OnExited;

        _job ??= new JobObject();
        process.Start();
        _job.Assign(process); // dies with LEO.Service, whatever happens
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var api = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(5) };
        api.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
        lock (_gate)
        {
            _process = process;
            _api = api;
            _stopping = false;
            _appliedSelections = new(StringComparer.Ordinal);
        }

        // Wait for the controller API.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            if (process.HasExited) throw new InvalidOperationException($"The engine exited during start (code {process.ExitCode}).");
            try
            {
                using var r = await api.GetAsync("version", ct);
                if (r.IsSuccessStatusCode) break;
            }
            catch (HttpRequestException) { }
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The engine didn't come up within 15 s.");
            await Task.Delay(250, ct);
        }

        log.LogInformation("Engine started (pid {Pid}, {Outbounds} network(s), {Providers} rule list(s))",
            process.Id, plan.Outbounds.Count, plan.Providers.Count);
        await ApplySelectionsAsync(plan, ct);
    }

    /// <summary>Full reload (outbounds/groups changed). Keeps the process and its tunnel.</summary>
    public async Task ReloadAsync(EnginePlan plan, CancellationToken ct)
    {
        var (api, controller, secret) = Api();
        WriteFiles(plan, controller, secret);
        using var r = await api.PutAsJsonAsync("configs?force=true", new { path = ConfigPath }, ct);
        if (!r.IsSuccessStatusCode)
            throw new InvalidOperationException($"Engine reload failed: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync(ct)}");
        lock (_gate) _appliedSelections = new(StringComparer.Ordinal);
        log.LogInformation("Engine reloaded ({Outbounds} network(s), {Providers} rule list(s))", plan.Outbounds.Count, plan.Providers.Count);
        await ApplySelectionsAsync(plan, ct);
    }

    /// <summary>Rewrites changed rule lists and tells the engine to re-read only those.</summary>
    public async Task UpdateProvidersAsync(EnginePlan plan, CancellationToken ct)
    {
        var (api, _, _) = Api();
        foreach (var (name, content) in plan.ProviderFiles())
        {
            if (_writtenProviders.TryGetValue(name, out var old) && old == content) continue;
            await File.WriteAllTextAsync(Path.Combine(WorkDir, "rules", name + ".yaml"), content, ct);
            _writtenProviders[name] = content;
            using var r = await api.PutAsync($"providers/rules/{Uri.EscapeDataString(name)}", null, ct);
            if (!r.IsSuccessStatusCode) log.LogWarning("Refreshing rule list {Name} failed: {Status}", name, (int)r.StatusCode);
        }
    }

    /// <summary>Switches select-groups (Primary, per-target fallback/block) live. Only changed ones are sent.</summary>
    public async Task ApplySelectionsAsync(EnginePlan plan, CancellationToken ct)
    {
        var (api, _, _) = Api();
        foreach (var s in plan.Selections)
        {
            if (_appliedSelections.TryGetValue(s.Group, out var cur) && cur == s.Choice) continue;
            using var r = await api.PutAsJsonAsync($"proxies/{Uri.EscapeDataString(s.Group)}", new { name = s.Choice }, ct);
            if (r.IsSuccessStatusCode)
            {
                _appliedSelections[s.Group] = s.Choice;
                log.LogInformation("Engine route {Group} -> {Choice}", s.Group, s.Choice);
            }
            else
            {
                log.LogWarning("Engine route {Group} -> {Choice} failed: {Status}", s.Group, s.Choice, (int)r.StatusCode);
            }
        }
    }

    /// <summary>Stops the engine and waits until its virtual adapter is gone.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        Process? p;
        lock (_gate)
        {
            p = _process;
            _process = null;
            _api?.Dispose();
            _api = null;
            _stopping = true;
        }
        if (p is null) return;

        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { }
        finally
        {
            p.Dispose();
        }

        // Routing is only clean once the tunnel adapter has disappeared.
        for (var i = 0; i < 60 && TunAdapterPresent(); i++) await Task.Delay(250, ct);
        if (TunAdapterPresent()) log.LogInformation("Engine stopped; Windows is still removing its adapter");
        else log.LogInformation("Engine stopped; networking restored");
    }

    public static bool TunAdapterPresent() =>
        NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name.Equals(TunDeviceName, StringComparison.OrdinalIgnoreCase));

    private void WriteFiles(EnginePlan plan, string controller, string secret)
    {
        Directory.CreateDirectory(Path.Combine(WorkDir, "rules"));
        var files = plan.ProviderFiles();
        foreach (var (name, content) in files) File.WriteAllText(Path.Combine(WorkDir, "rules", name + ".yaml"), content);
        _writtenProviders = new Dictionary<string, string>(files, StringComparer.Ordinal);
        File.WriteAllText(ConfigPath, plan.ToConfigYaml(controller, secret));
        _controller = controller;
        _secret = secret;
    }

    private string _controller = "";
    private string _secret = "";

    private (HttpClient Api, string Controller, string Secret) Api()
    {
        lock (_gate)
        {
            if (_api is null || _process is not { HasExited: false }) throw new InvalidOperationException("The engine isn't running.");
            return (_api, _controller, _secret);
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        var p = (Process)sender!;
        bool expected;
        int code;
        lock (_gate)
        {
            expected = _stopping || !ReferenceEquals(p, _process);
            try { code = p.ExitCode; } catch { code = -1; }
            if (!expected) { _process = null; _api?.Dispose(); _api = null; }
        }
        if (!expected)
        {
            log.LogWarning("Engine exited unexpectedly (code {Code})", code);
            UnexpectedExit?.Invoke(this, code);
        }
    }

    /// <summary>
    /// Only warnings/errors are logged, and never per-connection lines: those name the app and the address it
    /// talked to, which LEO doesn't record (privacy, LEO.md §17).
    /// </summary>
    private void LogEngineLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (line.Contains("[TCP]", StringComparison.Ordinal) || line.Contains("[UDP]", StringComparison.Ordinal)
            || line.Contains(" dial ", StringComparison.Ordinal) || line.Contains("-->", StringComparison.Ordinal)) return;
        if (line.Contains("level=error", StringComparison.Ordinal) || line.Contains("level=fatal", StringComparison.Ordinal))
            log.LogWarning("engine: {Line}", Trim(line));
        else if (line.Contains("level=warning", StringComparison.Ordinal))
            log.LogInformation("engine: {Line}", Trim(line));
    }

    private static string Trim(string line)
    {
        var i = line.IndexOf("msg=", StringComparison.Ordinal);
        return i >= 0 ? line[(i + 4)..] : line;
    }

    /// <summary>Kills engine processes left over from a previous LEO run (e.g. after a crash before job objects existed).</summary>
    private void KillStrays()
    {
        foreach (var p in Process.GetProcessesByName("mihomo"))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, ExePath, StringComparison.OrdinalIgnoreCase))
                {
                    log.LogWarning("Stopping a leftover engine process (pid {Pid})", p.Id);
                    p.Kill(entireProcessTree: true);
                }
            }
            catch { /* access denied / already gone */ }
            finally { p.Dispose(); }
        }
    }

    private static int FreeLoopbackPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
        _job?.Dispose();
    }
}
