using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Leo.Core;
using Leo.Core.Config;
using Leo.Core.Updates;
using Leo.Ipc;

namespace Leo.Service.Updates;

/// <summary>
/// Silent automatic updates from GitHub Releases (LEO.md §24.4, user's decision 2026-11-24). The service (SYSTEM)
/// checks once a day, downloads into ProgramData\…\LEO\updates (SYSTEM + Administrators only), verifies the MSI
/// (<see cref="UpdateVerifier"/>) and runs <c>msiexec /i … /qn</c>; the MSI's own upgrade replaces the files and
/// restarts the service, and Restart Manager restarts the UI. Only admins can start an install from the UI.
/// Only official builds (release workflow) or code-signed services update; dev builds never do (LEO.md §26). GitHub is read without its REST API (see <see cref="ReleaseFeed"/>);
/// the requests carry only "LEO/&lt;version&gt;" as User-Agent, nothing that identifies the PC.
/// </summary>
public sealed class UpdateService : BackgroundService
{
    private readonly ConfigStore _config;
    private readonly NotificationHub _hub;
    private readonly ILogger<UpdateService> _log;
    private readonly UpdateVerifier _verifier;
    private readonly Func<bool> _updatesOn;
    private readonly HttpClient _noRedirect;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly ReleaseVersion _installed = ReleaseVersion.TryParse(LeoInfo.Version) ?? new ReleaseVersion(0, 0, 0);
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private readonly TimeSpan _offset = TimeSpan.FromMinutes(Random.Shared.Next(0, (int)UpdatePolicy.MaxRandomOffset.TotalMinutes));

    private UpdateStateFile _state = new();
    private UpdateState _phase = UpdateState.Idle;
    private string _phaseText = "";
    private ReleaseVersion? _latest;
    private string? _offeredVersion;

    public UpdateService(ConfigStore config, NotificationHub hub, ILogger<UpdateService> log,
        UpdateVerifier? verifier = null, Func<bool>? updatesOn = null, HttpMessageHandler? handler = null)
    {
        _config = config;
        _hub = hub;
        _log = log;
        _verifier = verifier ?? UpdateVerifier.ForThisService();
        _updatesOn = updatesOn ?? (() => LeoInfo.IsOfficialBuild || Leo.Service.Security.Authenticode.SelfSigner is not null);
        _noRedirect = Client(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }, TimeSpan.FromSeconds(30));
        _http = Client(handler ?? new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }, TimeSpan.FromMinutes(15));
    }

    private static HttpClient Client(HttpMessageHandler handler, TimeSpan timeout)
    {
        var c = new HttpClient(handler, disposeHandler: false) { Timeout = timeout };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LEO", LeoInfo.Version));
        return c;
    }

    public event EventHandler<UpdateInfo>? Changed;

    public string StatePath { get; init; } = Path.Combine(LeoPaths.UpdatesDir, "state.json");
    public string DownloadDir { get; init; } = LeoPaths.UpdatesDir;

    public UpdateInfo Info
    {
        get
        {
            var settings = _config.Read(c => c.Updates);
            var on = _updatesOn();
            var signer = Leo.Service.Security.Authenticode.SelfSigner?.DisplayName; // null: no code-signing certificate
            var signed = signer is not null;
            var phase = on ? _phase : UpdateState.Off;
            var text = on ? _phaseText : "Development build: updates are off.";
            return new UpdateInfo(LeoInfo.Version, signed, signer, phase, text, _latest?.ToString(),
                _latest is null ? null : ReleaseFeed.NotesUrl(_latest), _state.LastCheck,
                settings.AutoCheck, settings.AutoInstall, UpdateChannels.Normalize(settings.Channel), _state.LastResult);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _state = LoadState();
        ReadPendingResult();
        if (!_updatesOn())
        {
            _log.LogInformation("Development build: updates are off");
            Publish();
            return;
        }
        Publish();

        while (!stoppingToken.IsCancellationRequested)
        {
            var due = UpdatePolicy.NextCheck(_started, _state.LastCheck, _offset);
            var wait = due - DateTimeOffset.Now;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            if (wait > TimeSpan.FromHours(1)) wait = TimeSpan.FromHours(1); // re-evaluate after settings changes and sleep
            try { await _wake.WaitAsync(wait, stoppingToken); }
            catch (OperationCanceledException) { break; }
            if (DateTimeOffset.Now < due) continue;
            if (!_config.Read(c => c.Updates.AutoCheck))
            {
                _state.LastCheck = DateTimeOffset.Now; // don't spin; checked again tomorrow if switched back on
                continue;
            }
            try { await CheckAsync(automatic: true, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning("Update check failed: {Message}", ex.Message); }
        }
    }

    /// <summary>"Check now" from Settings, or the daily check.</summary>
    public async Task<UpdateInfo> CheckAsync(bool automatic, CancellationToken ct)
    {
        if (!_updatesOn()) return Info;
        if (!await _work.WaitAsync(0, ct)) return Info; // a check, download or install is already running
        ReleaseVersion? found;
        try
        {
            SetPhase(UpdateState.Checking, "Checking for updates");
            var settings = _config.Read(c => c.Updates);
            var stable = await LatestStableAsync(ct);
            IReadOnlyList<ReleaseVersion> feed = [];
            if (UpdateChannels.Normalize(settings.Channel) == UpdateChannels.Canary) feed = await FeedAsync(ct);
            found = UpdatePolicy.Pick(_installed, settings.Channel, stable, feed);
            _state.LastCheck = DateTimeOffset.Now;
            SaveState();
            _latest = found;
            _log.LogInformation("Update check: installed {Installed}, found {Found}", _installed, found?.ToString() ?? "nothing newer");
            if (found is null)
            {
                SetPhase(UpdateState.UpToDate, $"LEO {_installed} is up to date.");
                return Info;
            }
            SetPhase(UpdateState.Available, $"LEO {found} is available.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            SetPhase(UpdateState.Failed, "Couldn't reach GitHub to check for updates.");
            _log.LogWarning("Update check failed: {Message}", ex.Message);
            return Info;
        }
        finally
        {
            _work.Release();
        }

        var action = UpdatePolicy.Decide(_config.Read(c => c.Updates), _updatesOn(), found);
        if (action == UpdateAction.Install && automatic)
        {
            await InstallAsync(ct);
        }
        else if (action != UpdateAction.Nothing && automatic && _offeredVersion != found.ToString())
        {
            _offeredVersion = found.ToString();
            _hub.Publish(NotificationKind.UpdateAvailable, $"LEO {found} is available",
                "Open LEO's Settings to update now.");
        }
        return Info;
    }

    /// <summary>Download, verify, install. Callers check the client's access (admins only).</summary>
    public async Task InstallAsync(CancellationToken ct)
    {
        if (!_updatesOn()) throw new InvalidOperationException("This is a development build, so it doesn't install updates.");
        var version = _latest ?? throw new InvalidOperationException("No update was found. Check for updates first.");
        if (!await _work.WaitAsync(0, ct)) throw new InvalidOperationException("An update is already being checked or installed.");
        var msi = Path.Combine(DownloadDir, ReleaseFeed.MsiName(version));
        try
        {
            Directory.CreateDirectory(DownloadDir);
            SetPhase(UpdateState.Downloading, $"Downloading LEO {version}");
            await DownloadAsync(ReleaseFeed.MsiUrl(version), msi, ct);
            string? sig = null;
            try { sig = await _http.GetStringAsync(ReleaseFeed.SigUrl(version), ct); }
            catch (HttpRequestException) { /* missing: the verifier refuses the MSI and says why */ }
            string? sha = null;
            try { sha = ReleaseFeed.ShaFor(await _http.GetStringAsync(ReleaseFeed.SumsUrl(version), ct), ReleaseFeed.MsiName(version)); }
            catch (HttpRequestException) { /* optional extra check */ }

            SetPhase(UpdateState.Verifying, $"Checking LEO {version}");
            if (_verifier.Problem(msi, sig, version, _installed, sha) is { } problem)
            {
                TryDelete(msi);
                Fail(version, problem);
                return;
            }

            var log = Path.Combine(DownloadDir, $"install-{version.Core}.log");
            _state.Pending = new PendingInstall(version.ToString(), log, DateTimeOffset.Now, msi);
            SaveState();
            SetPhase(UpdateState.Installing, $"Installing LEO {version}. Split tunneling pauses for a few seconds.");
            _log.LogInformation("Installing update {Version}", version);
            StartMsiexec(msi, log);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Fail(version, ex is HttpRequestException or TaskCanceledException ? "The download didn't finish. It continues at the next check." : ex.Message);
        }
        finally
        {
            _work.Release();
        }
    }

    public void SetSettings(bool autoCheck, bool autoInstall, string channel)
    {
        _config.Update(c =>
        {
            c.Updates.AutoCheck = autoCheck;
            c.Updates.AutoInstall = autoInstall;
            c.Updates.Channel = UpdateChannels.Normalize(channel);
            return true;
        });
        _log.LogInformation("Update settings: check {Check}, install {Install}, channel {Channel}", autoCheck, autoInstall, UpdateChannels.Normalize(channel));
        _wake.Release();
        Publish();
    }

    // ── GitHub ───────────────────────────────────────────────────────────────

    internal async Task<ReleaseVersion?> LatestStableAsync(CancellationToken ct)
    {
        using var r = await _noRedirect.GetAsync(ReleaseFeed.LatestUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        if (r.StatusCode is HttpStatusCode.NotFound) return null; // no release published yet
        return ReleaseFeed.FromLatestRedirect(r.Headers.Location);
    }

    private async Task<IReadOnlyList<ReleaseVersion>> FeedAsync(CancellationToken ct) =>
        ReleaseFeed.FromAtom(await _noRedirect.GetStringAsync(ReleaseFeed.AtomUrl, ct));

    /// <summary>Downloads to "&lt;file&gt;.part", resuming a partial download with a Range request.</summary>
    private async Task DownloadAsync(string url, string target, CancellationToken ct)
    {
        var part = target + ".part";
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(part); have = 0; }
        response.EnsureSuccessStatusCode();
        var append = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        await using (var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        {
            await input.CopyToAsync(output, ct);
        }
        File.Move(part, target, overwrite: true);
    }

    // ── Install and its result ───────────────────────────────────────────────

    /// <summary>
    /// Runs Windows Installer. Not in the engine's kill-on-close job object (that only holds mihomo), so msiexec keeps
    /// running while the MSI stops and replaces this service.
    /// </summary>
    private static void StartMsiexec(string msi, string log)
    {
        var msiexec = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
        Process.Start(new ProcessStartInfo(msiexec)
        {
            ArgumentList = { "/i", msi, "/qn", "/norestart", "/l*v", log },
            UseShellExecute = false,
            CreateNoWindow = true,
        })?.Dispose();
    }

    /// <summary>After a restart: did the install we started succeed? (The new version is running if it did.)</summary>
    private void ReadPendingResult()
    {
        if (_state.Pending is not { } pending) return;
        var expected = ReleaseVersion.TryParse(pending.Version);
        // The MSI stops this service before it replaces anything and starts it again at the end (the new version on
        // success, the old one after a rollback), so at the next start the install is always over.
        var ok = expected is not null && expected.Core == _installed.Core;
        _state.LastResult = new UpdateResult(pending.Version, ok, DateTimeOffset.Now, ok ? null : pending.Log,
            expected is null ? null : ReleaseFeed.NotesUrl(expected));
        _state.Pending = null;
        if (ok) TryDelete(pending.Msi);
        SaveState();
        _log.LogInformation(ok ? "Updated to {Version}" : "Update to {Version} failed; see the install log", pending.Version);
        if (!ok) SetPhase(UpdateState.Failed, $"Update to {pending.Version} failed. Install log: {pending.Log}");
    }

    private void Fail(ReleaseVersion version, string reason)
    {
        _log.LogWarning("Update to {Version} not installed: {Reason}", version, reason);
        SetPhase(UpdateState.Failed, reason);
        _hub.Publish(NotificationKind.UpdateFailed, $"LEO {version} wasn't installed", reason);
    }

    private void SetPhase(UpdateState phase, string text)
    {
        _phase = phase;
        _phaseText = text;
        Publish();
    }

    private void Publish()
    {
        try { Changed?.Invoke(this, Info); } catch (Exception ex) { _log.LogDebug(ex, "Update listener failed"); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ── State file (updates\state.json) ──────────────────────────────────────

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private UpdateStateFile LoadState()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<UpdateStateFile>(File.ReadAllText(StatePath), Json) ?? new() : new(); }
        catch { return new(); }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(_state, Json));
        }
        catch (Exception ex) { _log.LogWarning("Saving the update state failed: {Message}", ex.Message); }
    }

    public override void Dispose()
    {
        _noRedirect.Dispose();
        _http.Dispose();
        base.Dispose();
    }
}

internal sealed class UpdateStateFile
{
    public DateTimeOffset? LastCheck { get; set; }
    public PendingInstall? Pending { get; set; }
    public UpdateResult? LastResult { get; set; }
}

internal sealed record PendingInstall(string Version, string Log, DateTimeOffset At, string Msi);
