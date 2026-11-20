using Leo.Core.Config;
using Leo.Ipc;
using Leo.Service.Interop;

namespace Leo.Service.Wifi;

/// <summary>
/// Per-adapter Wi-Fi control (LEO.md §5.5): scan, connect (reusing or copying saved profiles, or creating one
/// from a passphrase), and keep each pinned adapter on its preferred SSIDs. Passwords go straight into a
/// Windows WLAN profile (Windows encrypts them); LEO never stores or logs them.
/// </summary>
public sealed class WifiManager(ConfigStore config, NotificationHub notifications, ILogger<WifiManager> log) : BackgroundService
{
    private static readonly TimeSpan EnforceInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly Dictionary<Guid, DateTimeOffset> _nextAttempt = [];
    private readonly Dictionary<Guid, DateTimeOffset> _lastScan = [];
    private readonly HashSet<string> _passwordWarned = [];
    private WlanApi? _wlan;

    private volatile IReadOnlyDictionary<string, DualLinkState> _dualLink = new Dictionary<string, DualLinkState>();

    /// <summary>Raised after LEO connects/disconnects an adapter, so the network picture can refresh.</summary>
    public event EventHandler? ConnectionChanged;

    /// <summary>Dual-link state per adapter id "{GUID}" (primaries and their secondary stations).</summary>
    public IReadOnlyDictionary<string, DualLinkState> DualLink => _dualLink;

    // ── Dual link (DBS secondary station) ────────────────────────────────────

    /// <summary>Turn Windows' synchronized dual-station mode on/off for a primary Wi-Fi adapter, and remember it.</summary>
    public async Task SetDualLinkAsync(string adapterId, bool enabled, string? mac, CancellationToken ct)
    {
        var iface = ParseId(adapterId);
        await WithWlan(w =>
        {
            if (w.GetSecondaries(iface).Count == 0)
                throw new InvalidOperationException("This Wi-Fi adapter has no second station, so it can't run a dual link.");
            w.SetSynchronizedConnections(iface, enabled);
            return 0;
        }, ct);
        config.Update(c =>
        {
            var entry = c.Adapters.FirstOrDefault(a => a.Id.Equals(adapterId, StringComparison.OrdinalIgnoreCase));
            if (entry is null) c.Adapters.Add(entry = new AdapterSettings { Id = adapterId, Mac = mac });
            entry.DualLink = enabled;
            return true;
        });
        log.LogInformation("Dual link on {Adapter} turned {State}", adapterId, enabled ? "on" : "off");
        await RefreshDualLinkAsync(applyDesired: false, ct);
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reads dual-link state from Windows and (optionally) re-applies the user's saved choice.</summary>
    private async Task RefreshDualLinkAsync(bool applyDesired, CancellationToken ct)
    {
        var desired = config.Read(c => c.Adapters.Where(a => a.DualLink is not null)
            .ToDictionary(a => a.Id, a => a.DualLink!.Value, StringComparer.OrdinalIgnoreCase));

        var states = await WithWlan(w =>
        {
            var map = new Dictionary<string, DualLinkState>(StringComparer.OrdinalIgnoreCase);
            foreach (var primary in w.GetInterfaces().Where(i => !i.IsSecondary))
            {
                var secondaries = w.GetSecondaries(primary.Id);
                if (secondaries.Count == 0) continue;
                var primaryId = Key(primary.Id);
                var enabled = w.GetSynchronizedConnections(primary.Id) ?? false;

                if (applyDesired && desired.TryGetValue(primaryId, out var want) && want != enabled)
                {
                    try
                    {
                        w.SetSynchronizedConnections(primary.Id, want);
                        enabled = want;
                        log.LogInformation("Re-applied dual link {State} on {Adapter} (Windows had reset it)", want ? "on" : "off", primaryId);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning("Couldn't re-apply dual link on {Adapter}: {Message}", primaryId, ex.Message);
                    }
                }

                var secondaryIds = secondaries.Select(Key).ToList();
                map[primaryId] = new DualLinkState(true, enabled, null, secondaryIds);
                foreach (var s in secondaryIds) map[s] = new DualLinkState(false, enabled, primaryId, []);
            }
            return map;
        }, ct);

        var changed = states.Count != _dualLink.Count ||
                      states.Any(kv => !_dualLink.TryGetValue(kv.Key, out var old) || old.Enabled != kv.Value.Enabled);
        _dualLink = states;
        if (changed) ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Key(Guid id) => id.ToString("B").ToUpperInvariant();

    // ── API ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<WifiNetworkInfo>> GetNetworksAsync(string adapterId, bool rescan, CancellationToken ct)
    {
        var iface = ParseId(adapterId);
        if (rescan)
        {
            await WithWlan(w => { w.Scan(iface); return 0; }, ct);
            await Task.Delay(TimeSpan.FromSeconds(3.5), ct); // scans complete in ~2–4 s
        }

        var pins = PinsFor(adapterId);
        return await WithWlan(w =>
        {
            var connected = w.GetConnectedSsid(iface);
            var visible = VisibleFor(w, iface)
                .GroupBy(n => n.Ssid, StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(n => n.HasProfile).ThenByDescending(n => n.SignalPercent).First())
                .ToList();
            var elsewhere = SsidsKnownOnOtherInterfaces(w, iface);

            var list = visible.Select(n => new WifiNetworkInfo(
                n.Ssid, n.SignalPercent, IsVisible: true, n.SecurityEnabled, WifiProfiles.SecurityLabel(n.AuthAlgorithm),
                CanJoinWithPassword: WifiProfiles.CanCreate(n.AuthAlgorithm),
                n.HasProfile, KnownElsewhere: !n.HasProfile && elsewhere.Contains(n.Ssid),
                IsConnected: n.Ssid == connected, IsPinned: pins.Contains(n.Ssid))).ToList();

            // Pinned networks that aren't in range right now still belong in the list.
            foreach (var pin in pins.Where(p => list.All(n => n.Ssid != p)))
            {
                list.Add(new WifiNetworkInfo(pin, 0, false, true, "", false, false, elsewhere.Contains(pin), false, true));
            }

            return (IReadOnlyList<WifiNetworkInfo>)list
                .OrderByDescending(n => n.IsConnected).ThenByDescending(n => n.IsPinned)
                .ThenByDescending(n => n.SignalPercent).ToList();
        }, ct);
    }

    public async Task SetPinsAsync(string adapterId, IReadOnlyList<string> ssids, string? mac, CancellationToken ct)
    {
        ParseId(adapterId);
        var clean = ssids.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).Take(10).ToList();
        config.Update(c =>
        {
            var entry = c.Adapters.FirstOrDefault(a => a.Id.Equals(adapterId, StringComparison.OrdinalIgnoreCase));
            if (entry is null) c.Adapters.Add(entry = new AdapterSettings { Id = adapterId, Mac = mac });
            entry.PinnedSsids = clean;
            return true;
        });
        lock (_nextAttempt) _nextAttempt.Remove(ParseId(adapterId));
        log.LogInformation("Wi-Fi pins for {Adapter}: [{Pins}]", adapterId, string.Join(", ", clean));
        _wake.Release();
        await Task.CompletedTask;
    }

    public async Task ConnectAsync(string adapterId, string ssid, string? password, CancellationToken ct)
    {
        var iface = ParseId(adapterId);
        var profile = await WithWlan(w => EnsureProfile(w, iface, ssid, password), ct);
        await WithWlan(w =>
        {
            try
            {
                w.Connect(iface, profile);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("error 1168", StringComparison.Ordinal) &&
                                                       w.GetInterfaces().Any(i => i.Id == iface && i.IsSecondary))
            {
                throw new InvalidOperationException(
                    "This is a secondary Wi-Fi station on the same card as your main Wi-Fi adapter. Windows doesn't let " +
                    "apps connect it on its own; it can only join the same network as the main adapter (on the other band) " +
                    "when Windows' synchronized dual-station mode is on.");
            }
            return 0;
        }, ct);
        log.LogInformation("Connecting {Adapter} to '{Ssid}'", adapterId, ssid);

        var deadline = DateTime.UtcNow + ConnectTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(750, ct);
            if (await WithWlan(w => w.GetConnectedSsid(iface), ct) == ssid)
            {
                ConnectionChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
        }
        throw new InvalidOperationException(
            $"Couldn't connect to '{ssid}'. If the password changed, connect again and enter the new one.");
    }

    public async Task DisconnectAsync(string adapterId, CancellationToken ct)
    {
        var iface = ParseId(adapterId);
        await WithWlan(w => { w.Disconnect(iface); return 0; }, ct);
        lock (_nextAttempt) _nextAttempt[iface] = DateTimeOffset.Now + TimeSpan.FromMinutes(2); // don't fight the user
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Pin enforcement loop ─────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshDualLinkAsync(applyDesired: true, stoppingToken);
                await EnforcePinsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Wi-Fi pin enforcement failed");
            }

            try { await _wake.WaitAsync(EnforceInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task EnforcePinsAsync(CancellationToken ct)
    {
        var pinned = config.Read(c => c.Adapters.Where(a => a.PinnedSsids.Count > 0)
            .Select(a => (a.Id, a.Nickname, Pins: a.PinnedSsids.ToList())).ToList());
        if (pinned.Count == 0) return;

        var interfaces = await WithWlan(w => w.GetInterfaces(), ct);
        foreach (var (id, nickname, pins) in pinned)
        {
            if (!Guid.TryParse(id, out var iface) || interfaces.All(i => i.Id != iface)) continue; // adapter absent

            var connected = await WithWlan(w => w.GetConnectedSsid(iface), ct);
            if (connected is not null && pins.Contains(connected)) continue; // on a pinned network: nothing to do

            var now = DateTimeOffset.Now;
            lock (_nextAttempt)
            {
                if (_nextAttempt.TryGetValue(iface, out var next) && now < next) continue;
                _nextAttempt[iface] = now + RetryBackoff;
            }

            if (!_lastScan.TryGetValue(iface, out var scanned) || now - scanned > ScanInterval)
            {
                _lastScan[iface] = now;
                await WithWlan(w => { w.Scan(iface); return 0; }, ct);
                await Task.Delay(TimeSpan.FromSeconds(3.5), ct);
            }

            var visible = await WithWlan(w => VisibleFor(w, iface), ct);
            var target = pins.FirstOrDefault(p => visible.Any(v => v.Ssid == p && v.Connectable));
            if (target is null) continue; // no pinned network in range

            try
            {
                await ConnectAsync(id, target, password: null, ct);
                var name = nickname ?? interfaces.First(i => i.Id == iface).Description;
                notifications.Publish(NotificationKind.WifiReconnected, "Wi-Fi reconnected",
                    $"{name} is back on '{target}'.", id);
            }
            catch (PasswordRequiredException)
            {
                if (_passwordWarned.Add($"{id}|{target}"))
                {
                    log.LogWarning("Pinned network '{Ssid}' needs a password on {Adapter}; open LEO to connect once.", target, id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogInformation("Auto-connect {Adapter} → '{Ssid}' failed: {Message}", id, target, ex.Message);
            }
        }
    }

    // ── Profiles ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a profile name on <paramref name="iface"/> for <paramref name="ssid"/>: an existing one, one copied
    /// from another Wi-Fi adapter, or a new one built from <paramref name="password"/>.
    /// </summary>
    private string EnsureProfile(WlanApi w, Guid iface, string ssid, string? password)
    {
        var network = VisibleFor(w, iface).Where(n => n.Ssid == ssid)
            .OrderByDescending(n => n.HasProfile).FirstOrDefault();

        if (password is null)
        {
            if (network is { HasProfile: true }) return network.ProfileName;

            foreach (var name in w.GetProfileNames(iface))
            {
                if (w.GetProfileXml(iface, name) is { } own && WifiProfiles.SsidOf(own) == ssid) return name;
            }

            // Saved on another adapter (e.g. the primary Wi-Fi port) → copy it here (key included, SYSTEM only).
            foreach (var other in w.GetInterfaces().Where(i => i.Id != iface))
            {
                foreach (var name in w.GetProfileNames(other.Id))
                {
                    if (w.GetProfileXml(other.Id, name) is { } xml && WifiProfiles.SsidOf(xml) == ssid)
                    {
                        w.SetProfile(iface, xml);
                        log.LogInformation("Copied Wi-Fi profile '{Profile}' to adapter {Adapter}", name, iface);
                        return name;
                    }
                }
            }
        }

        if (network is null)
        {
            throw new InvalidOperationException($"'{ssid}' isn't in range of this adapter right now.");
        }
        if (!WifiProfiles.CanCreate(network.AuthAlgorithm))
        {
            throw new InvalidOperationException($"'{ssid}' uses {WifiProfiles.SecurityLabel(network.AuthAlgorithm)} security; connect to it once in Windows Wi-Fi settings.");
        }
        if (WifiProfiles.NeedsPassword(network.AuthAlgorithm) && string.IsNullOrEmpty(password))
        {
            throw new PasswordRequiredException(ssid);
        }

        w.SetProfile(iface, WifiProfiles.Build(ssid, network.AuthAlgorithm, password));
        log.LogInformation("Created Wi-Fi profile for '{Ssid}' on adapter {Adapter}", ssid, iface);
        return ssid;
    }

    /// <summary>
    /// Networks visible to <paramref name="iface"/>. Windows 11 secondary STA interfaces (e.g. "WiFi 2") share
    /// the radio with their primary and often report no scan results of their own, so fall back to what the
    /// other interfaces on the system can see (profile flags reset — they don't apply to this interface).
    /// </summary>
    private static IReadOnlyList<WlanApi.AvailableNetwork> VisibleFor(WlanApi w, Guid iface)
    {
        var own = w.GetAvailableNetworks(iface);
        if (own.Count > 0) return own;
        return w.GetInterfaces().Where(i => i.Id != iface)
            .SelectMany(i => w.GetAvailableNetworks(i.Id))
            .Select(n => n with { IsConnected = false, HasProfile = false, ProfileName = "" })
            .ToList();
    }

    private static HashSet<string> SsidsKnownOnOtherInterfaces(WlanApi w, Guid iface)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var other in w.GetInterfaces().Where(i => i.Id != iface))
        {
            foreach (var name in w.GetProfileNames(other.Id)) set.Add(name); // profile names are SSIDs by default
        }
        return set;
    }

    private List<string> PinsFor(string adapterId) => config.Read(c =>
        c.Adapters.FirstOrDefault(a => a.Id.Equals(adapterId, StringComparison.OrdinalIgnoreCase))?.PinnedSsids.ToList() ?? []);

    private static Guid ParseId(string adapterId) =>
        Guid.TryParse(adapterId, out var g) ? g : throw new ArgumentException($"Not a Wi-Fi adapter id: {adapterId}");

    /// <summary>Serializes WLAN calls on one handle; reopens it if the WLAN service restarted.</summary>
    private async Task<T> WithWlan<T>(Func<WlanApi, T> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _wlan ??= new WlanApi();
            try
            {
                return action(_wlan);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("error 6", StringComparison.Ordinal)) // ERROR_INVALID_HANDLE
            {
                _wlan.Dispose();
                _wlan = new WlanApi();
                return action(_wlan);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public override void Dispose()
    {
        _wlan?.Dispose();
        base.Dispose();
    }
}

/// <param name="Supported">This adapter is a primary with a secondary station (can run a dual link).</param>
/// <param name="Enabled">Synchronized dual-station mode is on for the pair.</param>
/// <param name="PrimaryId">Set on a secondary station: the primary it follows.</param>
public sealed record DualLinkState(bool Supported, bool Enabled, string? PrimaryId, IReadOnlyList<string> SecondaryIds);

/// <summary>The network needs a passphrase LEO doesn't have.</summary>
public sealed class PasswordRequiredException(string ssid) : Exception($"'{ssid}' needs a password.")
{
    public string Ssid { get; } = ssid;
}
