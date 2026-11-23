using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Leo.Core.Config;
using Leo.Core.Networking;
using Leo.Core.SplitTunnel;
using Leo.Ipc;
using Leo.Service.Health;
using Leo.Service.Interop;
using Leo.Service.SplitTunnel;

namespace Leo.Service.Networking;

/// <summary>One VPN adapter as the monitor sees it. Counts and verdicts only: never prefixes, destinations or apps.</summary>
public sealed record VpnState(
    string AdapterId,
    string Key,
    bool Connected,
    VpnRouteAnalysis Routes,
    HealthReport? Probe,
    string? OwnTrafficAdapterId,   // the network carrying the VPN's own transport through the engine (app rule), else null
    int DnsServers,
    int NrptRules);

/// <summary>Everything the monitor knows right now (LEO.md §20).</summary>
public sealed record VpnPicture(
    IReadOnlyDictionary<string, VpnState> Vpns,
    KillSwitchResult Firewall,
    string? FirewallOwner,
    IReadOnlyList<NrptNamespace> Nrpt,
    bool? EngineRoutesPresent)    // null when the engine isn't running
{
    public static VpnPicture Empty { get; } = new(new Dictionary<string, VpnState>(), KillSwitchResult.None, null, [], null);
}

/// <summary>
/// VPN coexistence (LEO.md §20): watches route changes, analyses what each VPN routes, probes the internet through each
/// connected VPN, runs the tunnel self-check while a VPN is up and the engine runs, and decides kill-switch verdicts.
/// Publishes a <see cref="VpnPicture"/> to NetworkManager (VPN cards, BlockedByVpn) and SplitTunnelManager (outbounds,
/// unruled traffic, stand-aside). Everything is read-only: LEO never changes a VPN adapter's routes, metric, DNS or firewall.
/// Privacy: logs adapter ids, verdicts and counts only.
/// </summary>
public sealed class VpnMonitor(NetworkManager networks, ConfigStore config, MihomoEngine engine, NotificationHub notifications,
    ILogger<VpnMonitor> log) : BackgroundService
{
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly KillSwitchDetector _detector = new(TimeProvider.System);
    private readonly Dictionary<string, HealthReport> _vpnProbes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _ownTraffic = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _nextProbe = DateTimeOffset.MinValue;
    private VpnPicture _picture = VpnPicture.Empty;
    private string _fingerprint = "";
    private string? _owner;

    public event EventHandler<VpnPicture>? Changed;

    public VpnPicture Picture => Volatile.Read(ref _picture);

    /// <summary>Probes on/off (tests turn them off so a test run never sends traffic).</summary>
    public bool ProbesEnabled { get; init; } = true;

    public void RequestUpdate() => _signal.Release();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var watcher = new RouteWatcher();
        watcher.Changed += (_, _) => _signal.Release();
        if (!watcher.Start()) log.LogWarning("Route change notifications unavailable; checking routes every few seconds");
        networks.Changed += OnNetworksChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "VPN check failed");
                }

                var interval = TimeSpan.FromSeconds(Math.Clamp(config.Read(c => c.Health.ProbeIntervalSeconds), 2, 300));
                try { await _signal.WaitAsync(interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
                while (_signal.CurrentCount > 0) _signal.Wait(0);
            }
        }
        finally
        {
            networks.Changed -= OnNetworksChanged;
        }
    }

    private string _vpnSet = "";

    // Only a change in which VPNs exist/are connected matters here (route changes come from the watcher).
    private void OnNetworksChanged(object? sender, NetworkSnapshot e)
    {
        var set = string.Join("|", e.Adapters.Where(a => a.Kind == AdapterKind.Vpn)
            .Select(a => $"{a.Id}:{VpnConnection.IsConnected(a.Status == AdapterStatus.Up, a.IPv4)}:{string.Join(",", a.DnsServers)}"));
        if (set == _vpnSet) return;
        _vpnSet = set;
        _nextProbe = DateTimeOffset.MinValue; // probe a newly connected VPN right away
        _signal.Release();
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var scan = networks.LastScan;
        var vpns = scan.Where(a => a.Kind == AdapterKind.Vpn).ToList();
        var routes = RouteWatcher.ReadRoutes();
        var analysis = VpnRoutes.Analyze(routes, vpns.Select(v => v.Id));

        // LEO's own engine routes (tamper check) — only meaningful while the engine runs.
        bool? enginePresent = null;
        var leo = LeoAdapter();
        if (engine.IsRunning && leo is not null) enginePresent = VpnRoutes.EngineRoutesPresent(routes, leo.Id);

        // NRPT rules that point at a connected VPN's DNS servers.
        var nrpt = new List<NrptNamespace>();
        var nrptCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var connectedVpns = vpns.Where(v => VpnConnection.IsConnected(v.Status == AdapterStatus.Up, v.IPv4)).ToList();
        if (connectedVpns.Count > 0)
        {
            foreach (var rule in Nrpt.Read())
            {
                var owner = connectedVpns.FirstOrDefault(v => rule.Servers.Any(s => v.DnsServers.Contains(s, StringComparer.OrdinalIgnoreCase)));
                if (owner is null) continue;
                nrptCount[owner.Id] = nrptCount.GetValueOrDefault(owner.Id) + rule.Namespaces.Count;
                nrpt.AddRange(rule.Namespaces.Select(n => new NrptNamespace(n, owner.Id, rule.Servers)));
            }
        }

        // Probes (on the probe interval, not on every route change).
        bool? selfCheck = null;
        var now = DateTimeOffset.Now;
        if (ProbesEnabled && now >= _nextProbe)
        {
            var settings = config.Read(c => c.Health);
            _nextProbe = now + TimeSpan.FromSeconds(Math.Clamp(settings.ProbeIntervalSeconds, 2, 300)) - TimeSpan.FromMilliseconds(300);
            await ProbeVpnsAsync(connectedVpns, settings, ct);
            if (connectedVpns.Count > 0 && engine.IsRunning && leo is not null) selfCheck = await SelfCheckAsync(leo, settings, ct);
            await DetectOwnTrafficAsync(connectedVpns, ct);
            var verdictBefore = _detector.Current;
            UpdateDetector(vpns, selfCheck);
            if (_detector.Current != verdictBefore) OnVerdictChanged(verdictBefore, _detector.Current, vpns);
        }
        lock (_vpnProbes)
        {
            foreach (var id in _vpnProbes.Keys.Where(id => connectedVpns.All(v => v.Id != id)).ToList()) _vpnProbes.Remove(id);
        }

        var states = new Dictionary<string, VpnState>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in vpns)
        {
            var connected = connectedVpns.Contains(v);
            HealthReport? probe;
            lock (_vpnProbes) probe = connected ? _vpnProbes.GetValueOrDefault(v.Id) : null;
            states[v.Id] = new VpnState(v.Id, VpnIdentity.KeyOf(v.Description, v.Name), connected, analysis[v.Id], probe,
                connected ? _ownTraffic.GetValueOrDefault(v.Id) : null,
                v.DnsServers.Count(s => IPAddress.TryParse(s, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork),
                nrptCount.GetValueOrDefault(v.Id));
        }

        var picture = new VpnPicture(states, _detector.Current, _detector.Current.Verdict == KillSwitchVerdict.None ? null : _owner, nrpt, enginePresent);
        var fingerprint = Fingerprint(picture);
        Volatile.Write(ref _picture, picture);
        if (fingerprint == _fingerprint) return;

        foreach (var s in states.Values)
        {
            var old = _lastStates.GetValueOrDefault(s.AdapterId);
            if (old is null || old.Connected != s.Connected || old.Routes.TakesDefault != s.Routes.TakesDefault
                || old.Routes.PublicRanges4 != s.Routes.PublicRanges4 || (old.Probe?.State == HealthState.Up) != (s.Probe?.State == HealthState.Up))
            {
                // Counts only (privacy, LEO.md §20.5).
                log.LogInformation("VPN {Id}: connected {Connected}, takes default {Default4}/{Default6}, public ranges {Ranges}, internet {Internet}, NRPT {Nrpt}",
                    s.AdapterId, s.Connected, s.Routes.TakesDefault4, s.Routes.TakesDefault6, s.Routes.PublicRanges4 + s.Routes.PublicRanges6,
                    s.Probe?.State.ToString() ?? "-", s.NrptRules);
            }
        }
        _lastStates = states;
        _fingerprint = fingerprint;
        await networks.ApplyVpnPictureAsync(picture, ct);
        Changed?.Invoke(this, picture);
    }

    private Dictionary<string, VpnState> _lastStates = new(StringComparer.OrdinalIgnoreCase);

    private async Task ProbeVpnsAsync(IReadOnlyList<ScannedAdapter> connected, HealthSettings settings, CancellationToken ct)
    {
        var targets = settings.ProbeTargets.Count > 0 ? settings.ProbeTargets : new HealthSettings().ProbeTargets;
        await Task.WhenAll(connected.Where(v => v.InterfaceIndex >= 0).Select(async v =>
        {
            var local = v.IPv4.Select(IPAddress.Parse).First(ip => !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal));
            var dns = v.DnsServers.Select(s => IPAddress.TryParse(s, out var ip) ? ip : null).OfType<IPAddress>().ToList();
            var report = await InterfaceProbe.RunAsync(new ProbeTargetAdapter(v.Id, v.InterfaceIndex, local, dns), targets, settings.SlowLatencyMs, ct);
            lock (_vpnProbes) _vpnProbes[v.Id] = report;
        }));
    }

    /// <summary>
    /// Tunnel self-check (LEO.md §20.4): one TCP connect to the probe target from LEO's own adapter. It enters the engine
    /// and goes out per rules; a VPN firewall that blocks apps from sending into LEO's adapter makes it fail.
    /// </summary>
    private async Task<bool?> SelfCheckAsync(NetworkInterface leo, HealthSettings settings, CancellationToken ct)
    {
        var props = leo.GetIPProperties();
        var local = props.UnicastAddresses.Select(u => u.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        var index = NetworkInventory.InterfaceIndexOf(props);
        var target = (settings.ProbeTargets.Count > 0 ? settings.ProbeTargets : new HealthSettings().ProbeTargets)
            .Select(t => Uri.TryCreate(t.Url, UriKind.Absolute, out var u) ? u : null).OfType<Uri>().FirstOrDefault();
        if (local is null || index < 0 || target is null) return null;

        IPAddress[] addresses;
        try
        {
            using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dnsCts.CancelAfter(TimeSpan.FromSeconds(3));
            addresses = await Dns.GetHostAddressesAsync(target.Host, AddressFamily.InterNetwork, dnsCts.Token);
        }
        catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null; // no answer to test with: unknown, not a verdict
        }
        if (addresses.Length == 0) return null;

        using var socket = BoundSockets.Create(SocketType.Stream, ProtocolType.Tcp, local, index);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            await socket.ConnectAsync(addresses, target.Port, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return ProbeErrors.Classify(ex) switch
            {
                ProbeError.Denied or ProbeError.Timeout => false,
                ProbeError.Refused => true, // something answered: the path into the engine works
                _ => null,
            };
        }
    }

    /// <summary>
    /// Which kind of client a VPN is (LEO.md §20.1): does the engine carry its client program's connections? In memory only;
    /// only the outbound (network) is kept, never destinations.
    /// </summary>
    private async Task DetectOwnTrafficAsync(IReadOnlyList<ScannedAdapter> connected, CancellationToken ct)
    {
        _ownTraffic.Clear();
        if (connected.Count == 0 || !engine.IsRunning) return;
        IReadOnlyList<Leo.Core.Stats.EngineConnection> conns;
        try { conns = await engine.GetConnectionsAsync(ct); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException) { return; }

        var snap = networks.Snapshot;
        foreach (var v in connected)
        {
            var preset = VpnClients.Presets.FirstOrDefault(p => $"{v.Name} {v.Description}".Contains(p.Name.Split(' ')[0], StringComparison.OrdinalIgnoreCase));
            var carried = conns.FirstOrDefault(c => c.Chains.Count > 0 && c.Chains[0].StartsWith("net-", StringComparison.Ordinal)
                                                   && VpnClients.Match(c.ProcessPath) is { } p && (preset is null || p == preset));
            if (carried is null) continue;
            var adapter = snap.Adapters.FirstOrDefault(a => EnginePlan.OutboundName(a.Id) == carried.Chains[0]);
            _ownTraffic[v.Id] = adapter?.Id;
        }
    }

    private void UpdateDetector(IReadOnlyList<ScannedAdapter> vpns, bool? selfCheck)
    {
        var kvpns = vpns.Select(v =>
        {
            var connected = VpnConnection.IsConnected(v.Status == AdapterStatus.Up, v.IPv4);
            HealthReport? probe;
            lock (_vpnProbes) probe = _vpnProbes.GetValueOrDefault(v.Id);
            return new KillSwitchVpn(v.Id, connected, connected && probe?.State is HealthState.Up or HealthState.Degraded);
        }).ToList();

        var raw = networks.RawHealth;
        var physicals = networks.LastScan
            .Where(a => a.Kind.IsPrimaryCandidate() && a.Status == AdapterStatus.Up && a.Gateways.Count > 0
                        && VpnConnection.IsConnected(true, a.IPv4) && raw.ContainsKey(a.Id))
            .Select(a => raw[a.Id] is var r && r.State is HealthState.Up or HealthState.Degraded or HealthState.Captive
                ? new KillSwitchPhysical(a.Id, true, ProbeError.None)
                : new KillSwitchPhysical(a.Id, r.State == HealthState.Unknown, r.Error))
            .ToList();
        _detector.Update(kvpns, physicals, selfCheck);
    }

    private void OnVerdictChanged(KillSwitchResult before, KillSwitchResult after, IReadOnlyList<ScannedAdapter> vpns)
    {
        log.LogInformation("VPN firewall verdict: {Old} -> {New} ({Id})", before.Verdict, after.Verdict, after.VpnId ?? before.VpnId);
        if (after.Verdict == KillSwitchVerdict.None) { _owner = null; return; }
        var vpn = vpns.FirstOrDefault(v => v.Id == after.VpnId);
        try { _owner = vpn is null ? null : Wfp.OwnerFor(Wfp.ProviderNames(), vpn.Name, vpn.Description); }
        catch (Exception ex) { log.LogDebug(ex, "Reading firewall provider names failed"); _owner = null; }
        var name = _owner ?? vpn?.Name ?? "Your VPN";
        if (after.Verdict == KillSwitchVerdict.BlocksBypass)
        {
            notifications.Publish(NotificationKind.VpnFirewallDetected, $"{name} blocks traffic outside it",
                $"Rules that send apps or sites around {vpn?.Name ?? "the VPN"} can't connect while it's on. The VPN's own settings decide this.", after.VpnId);
        }
        // BlocksLeo is announced by SplitTunnelManager when it stands aside.
    }

    /// <summary>LEO's engine adapter ("LEO"), if present.</summary>
    public static NetworkInterface? LeoAdapter() =>
        NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name.Equals("LEO", StringComparison.OrdinalIgnoreCase));

    private static string Fingerprint(VpnPicture p) =>
        string.Join("\n", p.Vpns.Values.OrderBy(v => v.AdapterId).Select(v =>
            $"{v.AdapterId}|{v.Key}|{v.Connected}|{v.Routes}|{v.Probe?.State}|{v.Probe?.LatencyMs / 50}|{v.OwnTrafficAdapterId}|{v.DnsServers}|{v.NrptRules}"))
        + $"#{p.Firewall}#{p.FirewallOwner}#{string.Join(",", p.Nrpt.Select(n => n.Namespace + "@" + n.VpnId))}#{p.EngineRoutesPresent}";
}
