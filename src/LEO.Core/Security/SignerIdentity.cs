using System.Security.Cryptography.X509Certificates;

namespace Leo.Core.Security;

/// <summary>
/// Who signed a file, in the terms LEO compares (LEO.md §24.3): the signer's subject (CN and O) plus the CA that issued
/// it and the root it chains to. Deliberately not the certificate thumbprint, so a yearly renewal by the same CA for
/// the same name keeps matching; a new CA or a new name is a deliberate event (one dual-signed transition release).
/// </summary>
public sealed record SignerIdentity(
    string CommonName,
    string? Organization,
    string IssuerCommonName,
    string? IssuerOrganization,
    string RootSubject)
{
    /// <summary>A self-signed certificate (no CA vouches for the name): never trusted as LEO's signer.</summary>
    public bool IsSelfSigned { get; init; }

    /// <summary>Text for Settings → About and the log: "Signed by …".</summary>
    public string DisplayName => string.IsNullOrEmpty(Organization) || Organization == CommonName
        ? CommonName
        : $"{CommonName} ({Organization})";

    /// <summary>
    /// Same publisher, same CA: CN and O equal, the issuing CA has the same organisation (its own CA certificate may
    /// be renewed), and the chain ends at the same root. Self-signed identities never match anything.
    /// </summary>
    public bool Matches(SignerIdentity? other)
    {
        if (other is null || IsSelfSigned || other.IsSelfSigned) return false;
        return Eq(CommonName, other.CommonName)
               && Eq(Organization, other.Organization)
               && Eq(IssuerOrganization ?? IssuerCommonName, other.IssuerOrganization ?? other.IssuerCommonName)
               && Eq(RootSubject, other.RootSubject);
    }

    /// <summary>
    /// True when any signer of a file matches the expected one. A file can carry two signatures (old and new identity)
    /// for the one release that moves LEO to a new CA or name (LEO.md §24.3).
    /// </summary>
    public static bool AnyMatches(SignerIdentity? expected, IEnumerable<SignerIdentity> signers) =>
        expected is not null && signers.Any(expected.Matches);

    /// <summary>
    /// Builds the identity from the signing certificate and its issuers, leaf's issuer first and the root last
    /// (as an X509Chain lists them after the leaf).
    /// </summary>
    public static SignerIdentity FromChain(X509Certificate2 signer, IReadOnlyList<X509Certificate2> issuers)
    {
        var selfSigned = signer.SubjectName.RawData.AsSpan().SequenceEqual(signer.IssuerName.RawData) && issuers.Count == 0;
        var issuer = issuers.Count > 0 ? issuers[0] : null;
        var root = issuers.Count > 0 ? issuers[^1] : signer;
        return new SignerIdentity(
            Part(signer.SubjectName, "2.5.4.3") ?? signer.Subject,
            Part(signer.SubjectName, "2.5.4.10"),
            issuer is null ? Part(signer.IssuerName, "2.5.4.3") ?? signer.Issuer : Part(issuer.SubjectName, "2.5.4.3") ?? issuer.Subject,
            issuer is null ? Part(signer.IssuerName, "2.5.4.10") : Part(issuer.SubjectName, "2.5.4.10"),
            root.Subject)
        {
            IsSelfSigned = selfSigned,
        };
    }

    private static string? Part(X500DistinguishedName name, string oid)
    {
        foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
        {
            if (!rdn.HasMultipleElements && rdn.GetSingleElementType().Value == oid) return rdn.GetSingleElementValue();
        }
        return null;
    }

    private static bool Eq(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
