using System.Net;
using Leo.Core;
using Leo.Core.Config;
using Leo.Core.Security;
using Leo.Core.Updates;
using Leo.Ipc;
using Leo.Service;
using Leo.Service.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace Leo.Tests;

/// <summary>M8 updater (LEO.md §24.4) and Preview/version rules (§24.2, §24.7), diagnostics (§24.7).</summary>
public sealed class ShipUpdateTests
{
    private static ReleaseVersion V(string s) => ReleaseVersion.TryParse(s)!;

    // ── Versions and tags ────────────────────────────────────────────────────

    [Fact]
    public void Versions_Parse_And_Order()
    {
        Assert.Equal(new ReleaseVersion(0, 2, 1, "canary", 1), V("v0.2.1-canary.1"));
        Assert.Equal("v0.2.1-canary.1", V("0.2.1-canary.1").Tag);
        Assert.Equal("0.1.0", V("0.1.0+abc123").ToString()); // informational version with a commit
        Assert.True(V("v0.2.1-canary.1") < V("v0.2.1-canary.2"));
        Assert.True(V("v0.2.1-canary.2") < V("v0.2.1"));
        Assert.True(V("v0.2.1") < V("v0.2.2"));
        Assert.True(V("v0.2.2") < V("v1.0.0"));
        Assert.Null(ReleaseVersion.TryParse("v1.2"));
        Assert.Null(ReleaseVersion.TryParse("latest"));
        Assert.Null(ReleaseVersion.TryParse(null));
    }

    [Fact]
    public void Msi_Compares_Only_Three_Fields()
    {
        Assert.True(V("0.2.2").UpgradesOver(V("0.2.1")));
        Assert.False(V("0.2.1").UpgradesOver(V("0.2.1-canary.3"))); // same x.y.z: Windows Installer sees no upgrade
        Assert.False(V("0.2.1-canary.4").UpgradesOver(V("0.2.1-canary.3")));
        Assert.True(V("255.255.65535").FitsMsi);
        Assert.False(V("256.0.0").FitsMsi);
        Assert.False(V("0.256.0").FitsMsi);
    }

    [Fact]
    public void Tag_Check_Refuses_What_Windows_Installer_Couldnt_Upgrade()
    {
        string[] earlier = ["v0.1.0", "v0.2.0", "v0.2.1-canary.1"];
        Assert.Null(TagRules.Check("v0.2.2", earlier));
        Assert.Null(TagRules.Check("v0.2.2-canary.1", earlier));
        Assert.NotNull(TagRules.Check("v0.2.1", earlier));          // same x.y.z as the canary
        Assert.NotNull(TagRules.Check("v0.2.1-canary.2", earlier)); // canaries must move x.y.z too
        Assert.NotNull(TagRules.Check("v0.1.5", earlier));
        Assert.NotNull(TagRules.Check("0.3.0", earlier));           // no "v"
        Assert.NotNull(TagRules.Check("v0.3.0-beta.1", earlier));
        Assert.NotNull(TagRules.Check("v0.256.0", earlier));
        Assert.Null(TagRules.Check("v0.1.0", []));                  // the first public release
    }

    // ── Feed (no api.github.com) ─────────────────────────────────────────────

    [Fact]
    public void Latest_Redirect_Gives_The_Stable_Version()
    {
        Assert.Equal(V("0.3.0"), ReleaseFeed.FromLatestRedirect(new Uri("https://github.com/unterstellen/leo/releases/tag/v0.3.0")));
        Assert.Equal(V("0.3.0"), ReleaseFeed.FromLatestRedirect(new Uri("/unterstellen/leo/releases/tag/v0.3.0", UriKind.Relative)));
        Assert.Null(ReleaseFeed.FromLatestRedirect(new Uri("https://github.com/unterstellen/leo/releases")));
        Assert.Null(ReleaseFeed.FromLatestRedirect(new Uri("https://github.com/login")));
        Assert.Null(ReleaseFeed.FromLatestRedirect(null));
        Assert.Null(ReleaseFeed.FromLatestRedirect(new Uri("https://github.com/unterstellen/leo/releases/tag/v0.3.1-canary.1")));
        Assert.DoesNotContain("api.github.com", ReleaseFeed.LatestUrl + ReleaseFeed.AtomUrl);
    }

    private const string Atom = """
        <?xml version="1.0" encoding="UTF-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <entry><id>tag:github.com,2008:Repository/1/v0.3.1-canary.1</id>
            <link rel="alternate" type="text/html" href="https://github.com/unterstellen/leo/releases/tag/v0.3.1-canary.1"/>
            <title>LEO 0.3.1-canary.1</title></entry>
          <entry><link rel="alternate" type="text/html" href="https://github.com/unterstellen/leo/releases/tag/v0.3.0"/></entry>
          <entry><link rel="alternate" type="text/html" href="https://github.com/unterstellen/leo/releases/tag/nightly"/></entry>
        </feed>
        """;

    [Fact]
    public void Atom_Feed_Lists_Prereleases_And_Ignores_Junk()
    {
        Assert.Equal([V("0.3.1-canary.1"), V("0.3.0")], ReleaseFeed.FromAtom(Atom));
        Assert.Empty(ReleaseFeed.FromAtom("<html>not a feed"));
        Assert.Empty(ReleaseFeed.FromAtom(""));
    }

    [Fact]
    public void Asset_Names_Are_Fixed_By_Convention()
    {
        var v = V("v0.3.1-canary.1");
        Assert.Equal("LEO-0.3.1-x64.msi", ReleaseFeed.MsiName(v));
        Assert.Equal("https://github.com/unterstellen/leo/releases/download/v0.3.1-canary.1/LEO-0.3.1-x64.msi", ReleaseFeed.MsiUrl(v));
        var hash = new string('a', 64);
        Assert.Equal(hash, ReleaseFeed.ShaFor($"{hash}  LEO-0.3.1-x64.msi\n{new string('b', 64)}  mihomo-source.tar.gz\n", "LEO-0.3.1-x64.msi"));
        Assert.Null(ReleaseFeed.ShaFor("garbage", "LEO-0.3.1-x64.msi"));
    }

    // ── Policy ───────────────────────────────────────────────────────────────

    [Fact]
    public void Checks_A_Few_Minutes_After_Start_Then_Daily_With_Offset()
    {
        var start = new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);
        var offset = TimeSpan.FromMinutes(37);
        Assert.Equal(start.AddMinutes(5), UpdatePolicy.NextCheck(start, null, offset));
        Assert.Equal(start.AddMinutes(5), UpdatePolicy.NextCheck(start, start.AddDays(-3), offset)); // overdue
        var last = start.AddHours(-2);
        Assert.Equal(last.AddDays(1) + offset, UpdatePolicy.NextCheck(start, last, offset));
    }

    [Fact]
    public void Pick_Takes_The_Newest_Upgrade_Of_The_Channel()
    {
        var installed = V("0.3.0");
        var feed = ReleaseFeed.FromAtom(Atom);
        Assert.Null(UpdatePolicy.Pick(installed, "stable", V("0.3.0"), feed));
        Assert.Equal(V("0.3.1-canary.1"), UpdatePolicy.Pick(installed, "canary", V("0.3.0"), feed));
        Assert.Equal(V("0.4.0"), UpdatePolicy.Pick(installed, "canary", V("0.4.0"), feed));
        Assert.Null(UpdatePolicy.Pick(V("0.5.0"), "stable", V("0.4.0"), feed)); // newer-only
        Assert.Equal(V("0.4.0"), UpdatePolicy.Pick(installed, "anything-else", V("0.4.0"), feed)); // unknown channel = stable
    }

    [Fact]
    public void Decide_Respects_Settings_And_Never_Updates_Dev_Builds()
    {
        var found = V("0.4.0");
        Assert.Equal(UpdateAction.Install, UpdatePolicy.Decide(new UpdateSettings(), serviceSigned: true, found));
        Assert.Equal(UpdateAction.Offer, UpdatePolicy.Decide(new UpdateSettings { AutoInstall = false }, true, found));
        Assert.Equal(UpdateAction.Nothing, UpdatePolicy.Decide(new UpdateSettings(), serviceSigned: false, found));
        Assert.Equal(UpdateAction.Nothing, UpdatePolicy.Decide(new UpdateSettings(), true, null));
    }

    [Fact]
    public void Later_Silences_That_Version_For_The_Day()
    {
        var today = new DateOnly(2026, 11, 24);
        Assert.True(UpdatePolicy.ShouldToast("0.4.0", null, null, today));
        Assert.False(UpdatePolicy.ShouldToast("0.4.0", "0.4.0", today, today));
        Assert.True(UpdatePolicy.ShouldToast("0.4.0", "0.4.0", today.AddDays(-1), today));
        Assert.True(UpdatePolicy.ShouldToast("0.4.1", "0.4.0", today, today));
        Assert.False(UpdatePolicy.ShouldToast(null, null, null, today));
    }

    [Fact]
    public void Old_Configs_Get_Silent_Automatic_Updates()
    {
        var c = System.Text.Json.JsonSerializer.Deserialize<LeoConfig>("""{"schemaVersion":2}""")!;
        Assert.True(c.Updates.AutoCheck);
        Assert.True(c.Updates.AutoInstall);
        Assert.Equal("stable", c.Updates.Channel);
    }

    // ── Verification ─────────────────────────────────────────────────────────

    private static readonly SignerIdentity LeoSigner = new("Open Source Developer Jane Doe", "Open Source Developer", "Certum Code Signing CA", "Asseco", "CN=Certum Trusted Network CA");
    private static readonly SignerIdentity Other = LeoSigner with { CommonName = "Mallory" };
    private static readonly MsiIdentity GoodMsi = new("LEO", "0.4.0", LeoInfo.UpgradeCode, "UNTERSTELLEN");

    private static UpdateVerifier Verifier(SignerIdentity? self, SignatureCheck sig, MsiIdentity? msi, string sha = "abc") =>
        new(() => self, _ => sig, _ => msi, _ => sha);

    [Fact]
    public void Verifier_Accepts_Only_LEOs_Signed_Newer_Msi()
    {
        var signed = new SignatureCheck(true, [LeoSigner]);
        Assert.Null(Verifier(LeoSigner, signed, GoodMsi).Problem("x.msi", V("0.4.0"), V("0.3.0"), "abc"));
        Assert.Null(Verifier(LeoSigner, signed, GoodMsi).Problem("x.msi", V("0.4.0"), V("0.3.0"), null)); // no SHA256SUMS: still fine

        Assert.Contains("development build", Verifier(null, signed, GoodMsi).Problem("x.msi", V("0.4.0"), V("0.3.0"), null));
        Assert.Contains("validly signed", Verifier(LeoSigner, SignatureCheck.Unsigned("tampered"), GoodMsi).Problem("x.msi", V("0.4.0"), V("0.3.0"), null));
        Assert.Contains("someone else", Verifier(LeoSigner, new SignatureCheck(true, [Other]), GoodMsi).Problem("x.msi", V("0.4.0"), V("0.3.0"), null));
        Assert.Null(Verifier(LeoSigner, new SignatureCheck(true, [Other, LeoSigner]), GoodMsi).Problem("x.msi", V("0.4.0"), V("0.3.0"), null)); // dual-signed
        Assert.Contains("different product", Verifier(LeoSigner, signed, GoodMsi with { UpgradeCode = "{00000000-0000-0000-0000-000000000000}" }).Problem("x.msi", V("0.4.0"), V("0.3.0"), null));
        Assert.Contains("different product", Verifier(LeoSigner, signed, GoodMsi with { ProductName = "Evil" }).Problem("x.msi", V("0.4.0"), V("0.3.0"), null));
        Assert.Contains("not 0.4.0", Verifier(LeoSigner, signed, GoodMsi with { ProductVersion = "0.3.5" }).Problem("x.msi", V("0.4.0"), V("0.3.0"), null));
        Assert.Contains("isn't newer", Verifier(LeoSigner, signed, GoodMsi).Problem("x.msi", V("0.4.0"), V("0.4.0"), null));
        Assert.Contains("readable", Verifier(LeoSigner, signed, null).Problem("x.msi", V("0.4.0"), V("0.3.0"), null));
        Assert.Contains("checksum", Verifier(LeoSigner, signed, GoodMsi, sha: "def").Problem("x.msi", V("0.4.0"), V("0.3.0"), "abc"));
    }

    [Fact]
    public void Msi_Properties_Are_Read_From_A_Real_Msi_When_One_Was_Built()
    {
        // tools\build-release.ps1 output; skipped quietly on machines that haven't built it (CI builds it separately).
        var msi = Directory.Exists(Path.Combine(RepoRoot(), "artifacts", "release", "msi"))
            ? Directory.EnumerateFiles(Path.Combine(RepoRoot(), "artifacts", "release", "msi"), "LEO-*-x64.msi").FirstOrDefault()
            : null;
        if (msi is null) return;
        var id = MsiProperties.Read(msi);
        Assert.NotNull(id);
        Assert.Equal("LEO", id!.ProductName);
        Assert.Equal(LeoInfo.UpgradeCode, id.UpgradeCode, ignoreCase: true);
        Assert.NotNull(ReleaseVersion.TryParse(id.ProductVersion));
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "LEO.slnx"))) d = d.Parent;
        return d?.FullName ?? AppContext.BaseDirectory;
    }

    // ── Service (fake GitHub) ────────────────────────────────────────────────

    private sealed class FakeGitHub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Redirect(string to) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(to) } };

    private sealed class Env : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"leo-upd-{Guid.NewGuid():N}");
        public ConfigStore Config { get; }
        public NotificationHub Hub { get; } = new(NullLogger<NotificationHub>.Instance);
        public List<LeoNotification> Notes { get; } = [];
        public Env()
        {
            Directory.CreateDirectory(Dir);
            Config = new ConfigStore(Path.Combine(Dir, "config.json"));
            Hub.Raised += (_, n) => Notes.Add(n);
        }
        public UpdateService Service(FakeGitHub gh, bool signed = true, UpdateVerifier? verifier = null) =>
            new(Config, Hub, NullLogger<UpdateService>.Instance, verifier ?? Verifier(LeoSigner, SignatureCheck.Unsigned("x"), null), () => signed, gh)
            {
                StatePath = Path.Combine(Dir, "state.json"),
                DownloadDir = Path.Combine(Dir, "updates"),
            };
        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    [Fact]
    public async Task Dev_Build_Never_Asks_GitHub()
    {
        using var env = new Env();
        var gh = new FakeGitHub(_ => Redirect("https://github.com/unterstellen/leo/releases/tag/v99.0.0"));
        using var svc = env.Service(gh, signed: false);
        var info = await svc.CheckAsync(automatic: false, CancellationToken.None);
        Assert.Equal(UpdateState.Off, info.State);
        Assert.Empty(gh.Requests);
    }

    [Fact]
    public async Task Check_Uses_The_Redirect_Without_Following_It_And_Sends_No_PC_Details()
    {
        using var env = new Env();
        env.Config.Update(c => { c.Updates.AutoInstall = false; return true; });
        var gh = new FakeGitHub(_ => Redirect("https://github.com/unterstellen/leo/releases/tag/v99.0.0"));
        using var svc = env.Service(gh);

        var info = await svc.CheckAsync(automatic: true, CancellationToken.None);
        Assert.Equal(UpdateState.Available, info.State);
        Assert.Equal("99.0.0", info.LatestVersion);
        var request = Assert.Single(gh.Requests);
        Assert.Equal("https://github.com/unterstellen/leo/releases/latest", request.RequestUri!.ToString());
        Assert.Equal($"LEO/{LeoInfo.Version}", request.Headers.UserAgent.ToString());
        Assert.Null(request.Headers.Authorization);
        var note = Assert.Single(env.Notes);
        Assert.Equal(NotificationKind.UpdateAvailable, note.Kind);

        await svc.CheckAsync(automatic: true, CancellationToken.None);
        Assert.Single(env.Notes); // offered once per version
    }

    [Fact]
    public async Task No_Release_Yet_Or_Same_Version_Is_Up_To_Date()
    {
        using var env = new Env();
        using var none = env.Service(new FakeGitHub(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Equal(UpdateState.UpToDate, (await none.CheckAsync(false, CancellationToken.None)).State);

        using var same = env.Service(new FakeGitHub(_ => Redirect($"https://github.com/unterstellen/leo/releases/tag/v{LeoInfo.Version}")));
        Assert.Equal(UpdateState.UpToDate, (await same.CheckAsync(false, CancellationToken.None)).State);
    }

    [Fact]
    public async Task A_Download_That_Fails_Verification_Is_Deleted_And_Reported()
    {
        using var env = new Env();
        var gh = new FakeGitHub(r => r.RequestUri!.AbsolutePath.EndsWith("/latest")
            ? Redirect("https://github.com/unterstellen/leo/releases/tag/v99.0.0")
            : r.RequestUri.AbsolutePath.EndsWith(".msi")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var svc = env.Service(gh, verifier: Verifier(LeoSigner, SignatureCheck.Unsigned("bad signature"), GoodMsi));

        await svc.CheckAsync(automatic: true, CancellationToken.None); // auto-install is the default
        var info = svc.Info;
        Assert.Equal(UpdateState.Failed, info.State);
        Assert.Contains("validly signed", info.StateText);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(env.Dir, "updates"), "*.msi"));
        Assert.Contains(env.Notes, n => n.Kind == NotificationKind.UpdateFailed);
        Assert.Contains(gh.Requests, r => r.RequestUri!.ToString() == "https://github.com/unterstellen/leo/releases/download/v99.0.0/LEO-99.0.0-x64.msi");
    }

    [Fact]
    public async Task Canary_Reads_The_Atom_Feed()
    {
        using var env = new Env();
        env.Config.Update(c => { c.Updates.Channel = "canary"; c.Updates.AutoInstall = false; return true; });
        var gh = new FakeGitHub(r => r.RequestUri!.AbsolutePath.EndsWith(".atom")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Atom.Replace("0.3.1-canary.1", "98.0.1-canary.1")) }
            : Redirect("https://github.com/unterstellen/leo/releases/tag/v98.0.0"));
        using var svc = env.Service(gh);
        var info = await svc.CheckAsync(false, CancellationToken.None);
        Assert.Equal("98.0.1-canary.1", info.LatestVersion);
        Assert.Equal("canary", info.Channel);
    }

    // ── Preview label, diagnostics ───────────────────────────────────────────

    [Fact]
    public void Preview_Until_1_0()
    {
        Assert.True(LeoInfo.IsPreviewVersion("0.1.0"));
        Assert.True(LeoInfo.IsPreviewVersion("v0.9.3"));
        Assert.False(LeoInfo.IsPreviewVersion("1.0.0"));
        Assert.Equal("LEO 0.1.0 (Preview)", LeoInfo.DisplayVersion("0.1.0"));
        Assert.Equal("LEO 1.2.0", LeoInfo.DisplayVersion("1.2.0"));
        Assert.Equal("https://github.com/unterstellen/leo/tree/v0.1.0", LeoInfo.SourceUrl("0.1.0"));
    }

    [Fact]
    public void Diagnostics_Leave_Out_Names_And_Addresses_By_Default()
    {
        var wifi = new AdapterInfo("{1}", "WiFi", "Qualcomm FastConnect 6900", Leo.Core.Networking.AdapterKind.WiFi, "Home laptop", "AA:BB:CC:DD:EE:FF",
            AdapterStatus.Up, 866_000_000, ["192.168.1.23"], ["fe80::1"], ["192.168.1.1"], ["192.168.1.1"],
            new NetworkInfo("wifi:Kickstart", "Kickstart Alpha", "Home", Connectivity.Internet, false),
            25, false, 0, true, true, Leo.Core.Networking.HealthState.Up, 12, "Gateway 192.168.1.1 answered", false, null, []);
        var snapshot = new NetworkSnapshot([wifi], "{1}", true, DateTimeOffset.Now);
        string[] log =
        [
            "2026-11-24 15:03:22.334 +05:00 INFO Leo.Service.X: fine",
            "2026-11-24 15:03:23.000 +05:00 WARN Leo.Service.X: probe to 10.0.0.1 via AA-BB-CC-DD-EE-FF failed, fe80::1:2:3 too",
        ];
        var input = new DiagnosticsReport.Input("0.1.0", "0.1.0", false, null, "10.0.26200", snapshot, SplitTunnelInfo.Empty, log, false);
        var text = DiagnosticsReport.Build(input);

        Assert.Contains("Qualcomm FastConnect 6900", text);
        Assert.Contains("development build", text);
        Assert.Contains("15:03:23.000", text);         // times aren't mistaken for addresses
        Assert.DoesNotContain("15:03:22.334", text);    // INFO lines aren't included
        foreach (var secret in new[] { "Kickstart", "Home", "AA:BB", "AA-BB", "192.168", "10.0.0.1", "fe80" })
            Assert.DoesNotContain(secret, text);

        var full = DiagnosticsReport.Build(input with { IncludePersonal = true });
        Assert.Contains("network \"Home\"", full);
        Assert.Contains("192.168.1.23", full);
        Assert.Contains("AA:BB:CC:DD:EE:FF", full);
    }
}
