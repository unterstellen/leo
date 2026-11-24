using Leo.Core;
using Leo.Core.Security;
using Leo.Ipc;
using Microsoft.Win32;

namespace Leo.Service.Security;

/// <summary>
/// The pipe-client signature check (LEO.md §24.6 item 2): in signed builds a client gets the access its user has
/// (admins Full, standard users read-only) only when its image is inside LEO's install folder and signed by the
/// service's own signer; every other client is read-only. Off when the service itself is unsigned (dev builds) or
/// when an admin set <c>HKLM\SOFTWARE\UNTERSTELLEN\LEO\AllowUnsignedClients = 1</c>.
/// Honest limit: this stops other programs from pretending to be LEO, not a same-user program tampering with a
/// running LEO.exe; the real boundary stays the pipe ACL plus the per-user access level (§4.2).
/// </summary>
public sealed class ClientTrust(
    Func<SignerIdentity?> serviceSigner,
    Func<bool> allowUnsigned,
    string installDir,
    Func<string, SignatureCheck> verify)
{
    /// <summary>The production rule: this process's signer, the registry switch, this process's folder.</summary>
    public static ClientTrust ForThisService() => new(
        () => Authenticode.SelfSigner,
        ReadAllowUnsignedClients,
        AppContext.BaseDirectory,
        Authenticode.Verify);

    public TrustDecision Evaluate(string? imagePath) =>
        ClientTrustPolicy.Decide(serviceSigner(), allowUnsigned(), imagePath, installDir, verify);

    /// <summary>The access the client finally gets: its user's access, capped at read-only when untrusted.</summary>
    public (ClientIdentity Client, TrustDecision Decision) Apply(ClientIdentity client)
    {
        var decision = Evaluate(client.ImagePath == "?" ? null : client.ImagePath);
        return ClientTrustPolicy.IsUntrusted(decision) && client.Access == ClientAccess.Full
            ? (client with { Access = ClientAccess.ReadOnly }, decision)
            : (client, decision);
    }

    public static bool ReadAllowUnsignedClients()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(LeoInfo.MachineRegistryKey);
            return key?.GetValue(LeoInfo.AllowUnsignedClientsValue) is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }
}
