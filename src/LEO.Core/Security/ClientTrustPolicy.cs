namespace Leo.Core.Security;

/// <summary>Result of checking a file's Authenticode signature (WinVerifyTrust + the signer identities on it).</summary>
public sealed record SignatureCheck(bool Valid, IReadOnlyList<SignerIdentity> Signers, string? Problem = null)
{
    public static SignatureCheck Unsigned(string problem) => new(false, [], problem);
}

/// <summary>What the signature rule decided for a pipe client or the engine binary (LEO.md §24.6).</summary>
public enum TrustDecision
{
    /// <summary>The service itself isn't signed (every dev build): the check is off.</summary>
    CheckOffServiceUnsigned,

    /// <summary>An administrator set HKLM\SOFTWARE\UNTERSTELLEN\LEO\AllowUnsignedClients = 1.</summary>
    CheckOffAllowed,

    /// <summary>Inside LEO's install folder and signed by the same signer as the service.</summary>
    Trusted,

    OutsideInstallFolder,
    Unsigned,
    OtherSigner,
}

/// <summary>
/// The pure rule behind the pipe-client check and the engine binary check (LEO.md §24.6 items 2 and 5). A client that
/// isn't trusted gets read-only access, never a closed connection, so diagnostics keep working.
/// </summary>
public static class ClientTrustPolicy
{
    /// <param name="serviceSigner">The running service's own signer; null when the service is unsigned.</param>
    /// <param name="allowUnsigned">The admin-only registry switch.</param>
    /// <param name="imagePath">Full path of the client's (or engine's) executable.</param>
    /// <param name="installDir">LEO's install folder (admin-only writable).</param>
    /// <param name="verify">Checks a file's signature; only called when it matters.</param>
    public static TrustDecision Decide(SignerIdentity? serviceSigner, bool allowUnsigned, string? imagePath, string installDir,
        Func<string, SignatureCheck> verify)
    {
        if (serviceSigner is null) return TrustDecision.CheckOffServiceUnsigned;
        if (allowUnsigned) return TrustDecision.CheckOffAllowed;
        if (string.IsNullOrWhiteSpace(imagePath) || !LeoPaths.IsInside(imagePath, installDir)) return TrustDecision.OutsideInstallFolder;
        var check = verify(imagePath);
        if (!check.Valid) return TrustDecision.Unsigned;
        return SignerIdentity.AnyMatches(serviceSigner, check.Signers) ? TrustDecision.Trusted : TrustDecision.OtherSigner;
    }

    /// <summary>True when the decision means "treat as untrusted" (read-only client, engine not started).</summary>
    public static bool IsUntrusted(TrustDecision d) =>
        d is TrustDecision.OutsideInstallFolder or TrustDecision.Unsigned or TrustDecision.OtherSigner;

    /// <summary>Short words for the log line ("unsigned client", LEO.md §24.6).</summary>
    public static string Describe(TrustDecision d) => d switch
    {
        TrustDecision.CheckOffServiceUnsigned => "check off (development build)",
        TrustDecision.CheckOffAllowed => "check off (AllowUnsignedClients)",
        TrustDecision.Trusted => "signed by LEO",
        TrustDecision.OutsideInstallFolder => "unsigned client (outside the install folder)",
        TrustDecision.Unsigned => "unsigned client",
        TrustDecision.OtherSigner => "unsigned client (other signer)",
        _ => d.ToString(),
    };
}
