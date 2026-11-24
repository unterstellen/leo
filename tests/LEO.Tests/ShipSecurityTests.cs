using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Leo.Core;
using Leo.Core.Security;
using Leo.Core.SplitTunnel;
using Leo.Ipc;
using Leo.Service;
using Leo.Service.Security;
using Leo.Service.Setup;
using Leo.Service.SplitTunnel;
using Microsoft.Extensions.Logging.Abstractions;

namespace Leo.Tests;

/// <summary>M8 hardening (LEO.md §24.6): signer comparison, client trust, Authenticode, engine secret, folder ACLs, pipes.</summary>
public sealed class ShipSecurityTests
{
    // ── SignerIdentity (§24.3) ───────────────────────────────────────────────

    private sealed class Ca : IDisposable
    {
        public required X509Certificate2 Root { get; init; }
        public required X509Certificate2 Issuer { get; init; }
        public void Dispose() { Root.Dispose(); Issuer.Dispose(); }

        public static Ca Create(string org)
        {
            using var rootKey = RSA.Create(2048);
            var rootReq = new CertificateRequest($"CN={org} Root, O={org}", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            var root = rootReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

            var issuerKey = RSA.Create(2048);
            var issuerReq = new CertificateRequest($"CN={org} Code Signing CA, O={org}", issuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            issuerReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var issuerPub = issuerReq.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5), RandomNumberGenerator.GetBytes(8));
            return new Ca { Root = root, Issuer = issuerPub.CopyWithPrivateKey(issuerKey) };
        }

        public SignerIdentity Sign(string subject)
        {
            using var key = RSA.Create(2048);
            var req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var leaf = req.Create(Issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), RandomNumberGenerator.GetBytes(8));
            return SignerIdentity.FromChain(leaf, [Issuer, Root]);
        }
    }

    private const string Leo = "CN=Open Source Developer Jane Doe, O=Open Source Developer";

    [Fact]
    public void Signer_Renewal_By_Same_Ca_Matches()
    {
        using var ca = Ca.Create("Certum");
        var year1 = ca.Sign(Leo);
        var year2 = ca.Sign(Leo); // new key, new serial, same name and CA
        Assert.True(year1.Matches(year2));
        Assert.Equal("Open Source Developer Jane Doe (Open Source Developer)", year1.DisplayName);
    }

    [Fact]
    public void Signer_Other_Subject_Or_Other_Ca_Does_Not_Match()
    {
        using var ca = Ca.Create("Certum");
        using var other = Ca.Create("Sectigo");
        var leo = ca.Sign(Leo);
        Assert.False(leo.Matches(ca.Sign("CN=Someone Else, O=Open Source Developer")));
        Assert.False(leo.Matches(other.Sign(Leo)));
        Assert.False(leo.Matches(null));
    }

    [Fact]
    public void Self_Signed_Never_Matches_Even_Itself()
    {
        using var key = RSA.Create(2048);
        using var self = new CertificateRequest(Leo, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var id = SignerIdentity.FromChain(self, []);
        Assert.True(id.IsSelfSigned);
        Assert.False(id.Matches(id));
    }

    [Fact]
    public void Dual_Signature_Transition_Matches_Old_Identity()
    {
        using var oldCa = Ca.Create("Certum");
        using var newCa = Ca.Create("Microsoft ID Verified");
        var running = oldCa.Sign(Leo);
        var newOnly = newCa.Sign("CN=INFERSTELLEN, O=INFERSTELLEN");
        Assert.False(SignerIdentity.AnyMatches(running, [newOnly]));
        Assert.True(SignerIdentity.AnyMatches(running, [newOnly, oldCa.Sign(Leo)])); // /as second signature
        Assert.False(SignerIdentity.AnyMatches(null, [newOnly]));
    }

    // ── ClientTrustPolicy / ClientTrust (§24.6 item 2) ───────────────────────

    private static readonly string InstallDir = Path.Combine(Path.GetTempPath(), "leo-install-test");
    private static string InInstall => Path.Combine(InstallDir, "LEO.exe");

    private static SignerIdentity TestSigner()
    {
        using var ca = Ca.Create("Certum");
        return ca.Sign(Leo);
    }

    [Fact]
    public void Trust_Check_Is_Off_For_Unsigned_Service_And_AllowUnsignedClients()
    {
        var calls = 0;
        SignatureCheck Verify(string _) { calls++; return SignatureCheck.Unsigned("x"); }
        Assert.Equal(TrustDecision.CheckOffServiceUnsigned, ClientTrustPolicy.Decide(null, false, @"C:\x\evil.exe", InstallDir, Verify));
        Assert.Equal(TrustDecision.CheckOffAllowed, ClientTrustPolicy.Decide(TestSigner(), true, @"C:\x\evil.exe", InstallDir, Verify));
        Assert.Equal(0, calls);
        Assert.False(ClientTrustPolicy.IsUntrusted(TrustDecision.CheckOffServiceUnsigned));
    }

    [Fact]
    public void Trust_Requires_Install_Folder_And_Same_Signer()
    {
        using var ca = Ca.Create("Certum");
        var service = ca.Sign(Leo);
        var renewed = ca.Sign(Leo);
        var stranger = ca.Sign("CN=Mallory, O=Mallory");

        Assert.Equal(TrustDecision.OutsideInstallFolder,
            ClientTrustPolicy.Decide(service, false, @"C:\Users\x\Downloads\LEO.exe", InstallDir, _ => new SignatureCheck(true, [renewed])));
        Assert.Equal(TrustDecision.OutsideInstallFolder, ClientTrustPolicy.Decide(service, false, null, InstallDir, _ => new SignatureCheck(true, [renewed])));
        // A sibling folder whose name starts the same isn't "inside".
        Assert.Equal(TrustDecision.OutsideInstallFolder,
            ClientTrustPolicy.Decide(service, false, InstallDir + "-evil\\LEO.exe", InstallDir, _ => new SignatureCheck(true, [renewed])));
        Assert.Equal(TrustDecision.Unsigned, ClientTrustPolicy.Decide(service, false, InInstall, InstallDir, _ => SignatureCheck.Unsigned("none")));
        Assert.Equal(TrustDecision.OtherSigner, ClientTrustPolicy.Decide(service, false, InInstall, InstallDir, _ => new SignatureCheck(true, [stranger])));
        Assert.Equal(TrustDecision.Trusted, ClientTrustPolicy.Decide(service, false, InInstall, InstallDir, _ => new SignatureCheck(true, [renewed])));
        Assert.Contains("unsigned client", ClientTrustPolicy.Describe(TrustDecision.Unsigned));
    }

    [Fact]
    public void Untrusted_Client_Gets_ReadOnly_Trusted_Keeps_Its_Users_Access()
    {
        var service = TestSigner();
        var admin = new ClientIdentity(1, InInstall, @"PC\admin", ClientAccess.Full);
        var user = new ClientIdentity(2, InInstall, @"PC\user", ClientAccess.ReadOnly);

        var untrusted = new ClientTrust(() => service, () => false, InstallDir, _ => SignatureCheck.Unsigned("none"));
        Assert.Equal(ClientAccess.ReadOnly, untrusted.Apply(admin).Client.Access);
        Assert.Equal(TrustDecision.Unsigned, untrusted.Apply(admin).Decision);

        var trusted = new ClientTrust(() => service, () => false, InstallDir, _ => new SignatureCheck(true, [service]));
        Assert.Equal(ClientAccess.Full, trusted.Apply(admin).Client.Access);
        Assert.Equal(ClientAccess.ReadOnly, trusted.Apply(user).Client.Access); // a signature never raises access

        var devBuild = new ClientTrust(() => null, () => false, InstallDir, _ => SignatureCheck.Unsigned("none"));
        Assert.Equal(ClientAccess.Full, devBuild.Apply(admin with { ImagePath = @"C:\anywhere\LEO.exe" }).Client.Access);

        var allowed = new ClientTrust(() => service, () => true, InstallDir, _ => SignatureCheck.Unsigned("none"));
        Assert.Equal(ClientAccess.Full, allowed.Apply(admin).Client.Access);
    }

    [Fact]
    public async Task Pipe_Server_Gives_An_Untrusted_Admin_Client_ReadOnly()
    {
        var pipeName = $"LEO.Test.{Guid.NewGuid():N}";
        var service = TestSigner();
        var server = new ControlPipeServer(new EngineStatusProvider(), null!, NullLoggerFactory.Instance)
        {
            PipeName = pipeName,
            Trust = new ClientTrust(() => service, () => false, InstallDir, _ => SignatureCheck.Unsigned("test")),
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);
        try
        {
            await using var stream = await PipeTransport.ConnectClientAsync(pipeName, 5000, cts.Token);
            using var rpc = PipeTransport.CreateRpc(stream);
            var control = rpc.Attach<ILeoControl>();
            rpc.StartListening();
            var hello = await control.HelloAsync(new HelloRequest(LeoInfo.ProtocolVersion, "tests", "1"), cts.Token);
            Assert.Equal(ClientAccess.ReadOnly, hello.Access);
            var ex = await Assert.ThrowsAsync<StreamJsonRpc.RemoteInvocationException>(() => control.SetAutoFailoverAsync(true, cts.Token));
            Assert.Equal(LeoErrorCodes.AccessDenied, ex.ErrorCode);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    // ── Authenticode (real files) ────────────────────────────────────────────

    [Fact]
    public void Authenticode_Accepts_A_Microsoft_Signed_Runtime_File_And_Reads_The_Signer()
    {
        var corelib = typeof(object).Assembly.Location; // System.Private.CoreLib.dll, embedded Microsoft signature
        var check = Authenticode.Verify(corelib);
        Assert.True(check.Valid, check.Problem);
        Assert.Contains(check.Signers, s => s.Organization == "Microsoft Corporation" && s.RootSubject.Contains("Microsoft Root"));
        Assert.All(check.Signers, s => Assert.False(s.IsSelfSigned));
    }

    [Fact]
    public void Authenticode_Refuses_Unsigned_Tampered_And_Missing_Files()
    {
        Assert.False(Authenticode.Verify(typeof(ShipSecurityTests).Assembly.Location).Valid); // our own test build: unsigned
        Assert.False(Authenticode.Verify(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe")).Valid);

        var copy = Path.Combine(Path.GetTempPath(), $"leo-tamper-{Guid.NewGuid():N}.dll");
        File.Copy(typeof(object).Assembly.Location, copy);
        try
        {
            using (var fs = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite))
            {
                fs.Position = fs.Length / 2;
                var b = fs.ReadByte();
                fs.Position = fs.Length / 2;
                fs.WriteByte((byte)(b ^ 0xFF));
            }
            var check = Authenticode.Verify(copy);
            Assert.False(check.Valid);
            Assert.NotNull(check.Problem);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    // ── Engine secret (§24.6 item 1) ─────────────────────────────────────────

    [Fact]
    public void Engine_Config_Has_No_Secret_And_The_Command_Line_Carries_It()
    {
        var plan = EnginePlan.Build([], null, [], []);
        var yaml = plan.ToConfigYaml("127.0.0.1:9");
        Assert.DoesNotContain("secret", yaml, StringComparison.OrdinalIgnoreCase);

        var args = MihomoEngine.Arguments(@"C:\data\engine", @"C:\data\engine\config.yaml", "ABC123");
        var i = args.ToList().IndexOf("-secret");
        Assert.True(i >= 0);
        Assert.Equal("ABC123", args[i + 1]);
        Assert.Equal(["-d", @"C:\data\engine", "-f", @"C:\data\engine\config.yaml"], args.Take(4));
        Assert.Equal(Path.Combine(LeoPaths.MachineData, "engine"), MihomoEngine.WorkDir);
    }

    // ── ProgramData ACLs, owner, reparse points (§24.6 items 1, 4, 7) ─────────

    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    private static IReadOnlyList<FileSystemAccessRule> Rules(DirectorySecurity s) =>
        s.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

    [Fact]
    public void Engine_And_Updates_Folders_Are_System_And_Admins_Only()
    {
        var engine = MachineDataSecurity.PrivateFolderSecurity();
        Assert.True(engine.AreAccessRulesProtected);
        Assert.DoesNotContain(Rules(engine), r => r.IdentityReference.Equals(Users));
        Assert.Equal(2, Rules(engine).Count);
        Assert.All(Rules(engine), r => Assert.True(
            r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)) ||
            r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null))));

        var data = MachineDataSecurity.MachineFolderSecurity();
        var users = Assert.Single(Rules(data), r => r.IdentityReference.Equals(Users));
        Assert.Equal(FileSystemRights.ReadAndExecute, users.FileSystemRights & ~FileSystemRights.Synchronize);
        Assert.Equal(Path.Combine(LeoPaths.MachineData, "updates"), LeoPaths.UpdatesDir);
    }

    [Fact]
    public void Only_System_Or_Administrators_May_Own_The_Data_Folders()
    {
        Assert.True(MachineDataSecurity.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.True(MachineDataSecurity.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
        Assert.False(MachineDataSecurity.IsTrustedOwner(Users));
        using var me = WindowsIdentity.GetCurrent();
        Assert.False(MachineDataSecurity.IsTrustedOwner(me.User!));
    }

    private static void Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "mklink /J failed");
    }

    [Fact]
    public void A_Junction_Is_Seen_As_A_Reparse_Point()
    {
        var root = Path.Combine(Path.GetTempPath(), $"leo-rp-{Guid.NewGuid():N}");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(target);
        try
        {
            Junction(Path.Combine(root, "UNTERSTELLEN"), target);
            Assert.True(MachineDataSecurity.IsReparsePoint(new DirectoryInfo(Path.Combine(root, "UNTERSTELLEN"))));
            Assert.False(MachineDataSecurity.IsReparsePoint(new DirectoryInfo(target)));
        }
        finally
        {
            foreach (var d in new DirectoryInfo(root).EnumerateDirectories().Where(SafeDelete.IsLink)) d.Delete();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SafeDelete_Removes_A_Planted_Link_But_Never_Its_Target()
    {
        var root = Path.Combine(Path.GetTempPath(), $"leo-sd-{Guid.NewGuid():N}");
        var precious = Path.Combine(root, "precious");
        var leo = Path.Combine(root, "UNTERSTELLEN", "LEO");
        Directory.CreateDirectory(precious);
        File.WriteAllText(Path.Combine(precious, "keep.txt"), "keep");
        Directory.CreateDirectory(Path.Combine(leo, "logs"));
        File.WriteAllText(Path.Combine(leo, "ui.json"), "{}");
        Junction(Path.Combine(leo, "logs", "planted"), precious);
        try
        {
            Assert.True(SafeDelete.DeleteTree(leo));
            Assert.False(Directory.Exists(leo));
            Assert.True(File.Exists(Path.Combine(precious, "keep.txt")));

            // A junction above LEO's folder (…\UNTERSTELLEN → elsewhere): refused entirely.
            var other = Path.Combine(root, "other");
            Directory.CreateDirectory(Path.Combine(other, "LEO"));
            File.WriteAllText(Path.Combine(other, "LEO", "keep.txt"), "keep");
            Directory.Delete(Path.Combine(root, "UNTERSTELLEN"));
            Junction(Path.Combine(root, "UNTERSTELLEN"), other);
            Assert.False(SafeDelete.DeleteTree(Path.Combine(root, "UNTERSTELLEN", "LEO")));
            Assert.True(File.Exists(Path.Combine(other, "LEO", "keep.txt")));
        }
        finally
        {
            foreach (var d in new DirectoryInfo(root).EnumerateDirectories().Where(SafeDelete.IsLink)) d.Delete();
            Directory.Delete(root, recursive: true);
        }
    }

    // ── Pipes (§24.6 item 3) ─────────────────────────────────────────────────

    [Fact]
    public void FirstPipeInstance_Refuses_A_Name_Someone_Already_Owns()
    {
        var name = $"LEO.Test.{Guid.NewGuid():N}";
        using var squatter = PipeTransport.CreateServerStream(name);
        var ex = Record.Exception(() => PipeTransport.CreateServerStream(name, firstInstance: true).Dispose());
        Assert.True(ex is UnauthorizedAccessException or IOException, ex?.GetType().Name ?? "no exception");
    }

    [Fact]
    public void Ui_Refuses_A_Pipe_Server_That_Isnt_LEOs_Service()
    {
        var install = @"C:\Program Files\UNTERSTELLEN\LEO";
        Assert.Null(PipeServerCheck.Problem(new PipeServerInfo(0, install + @"\LEO.Service.exe"), install));
        Assert.Null(PipeServerCheck.Problem(new PipeServerInfo(0, install + @"\Service\LEO.Service.exe"), install)); // dev deploy
        Assert.Contains("session 1", PipeServerCheck.Problem(new PipeServerInfo(1, install + @"\LEO.Service.exe"), install));
        Assert.Contains("not LEO.Service.exe", PipeServerCheck.Problem(new PipeServerInfo(0, @"C:\Windows\evil.exe"), install));
        Assert.Contains("install folder", PipeServerCheck.Problem(new PipeServerInfo(0, @"C:\Users\x\LEO.Service.exe"), install));
        Assert.NotNull(PipeServerCheck.Problem(null, install));

        Assert.True(PipeServerCheck.ShouldEnforce(install + @"\LEO.exe", install));
        Assert.False(PipeServerCheck.ShouldEnforce(@"C:\src\leo\bin\Debug\LEO.exe", install));
    }

    [Fact]
    public async Task Ui_Identifies_The_Real_Pipe_Server()
    {
        var name = $"LEO.Test.{Guid.NewGuid():N}";
        await using var server = PipeTransport.CreateServerStream(name);
        var accept = server.WaitForConnectionAsync();
        await using var client = await PipeTransport.ConnectClientAsync(name, 5000);
        await accept;
        var info = PipeServerCheck.Identify(client);
        Assert.NotNull(info);
        Assert.Equal(Environment.ProcessPath, info!.ImagePath, ignoreCase: true);
        Assert.Equal((uint)Process.GetCurrentProcess().SessionId, info.SessionId);
        Assert.NotNull(PipeServerCheck.Problem(info, LeoPaths.InstallDir)); // the test host is not LEO's service
    }
}
