using Leo.Core;
using Leo.Core.Security;
using Leo.Core.Updates;

namespace Leo.Service.Updates;

/// <summary>
/// Everything that must pass before the updater runs a downloaded MSI (LEO.md §24.4, §26). Any failure means the file
/// is deleted and the UI says why:
/// <list type="number">
/// <item>Updates are on: an official build (release workflow) or a code-signed service. Dev builds never install.</item>
/// <item>LEO's own update signature ("….msi.sig") matches one of <see cref="UpdateKeys.Public"/>. Always required,
/// with or without a code-signing certificate.</item>
/// <item>When the running service is Authenticode-signed (a CA certificate, §24.3): WinVerifyTrust accepts the MSI and
/// one of its signers matches the service's signer (renewal-tolerant, dual signature allowed).</item>
/// <item>It is LEO: UpgradeCode and ProductName are LEO's, and its ProductVersion is the one found and higher than the
/// installed one (read from the Property table, nothing executed).</item>
/// <item>Its SHA-256 matches SHA256SUMS.txt when that file was available (an extra check, not the gate: it comes from
/// the same place as the MSI).</item>
/// </list>
/// </summary>
public sealed class UpdateVerifier(
    Func<bool> officialBuild,
    Func<SignerIdentity?> serviceSigner,
    IReadOnlyList<string> updateKeys,
    Func<string, SignatureCheck> verifySignature,
    Func<string, MsiIdentity?> readMsi,
    Func<string, string> sha256)
{
    public static UpdateVerifier ForThisService() => new(
        () => LeoInfo.IsOfficialBuild,
        () => Leo.Service.Security.Authenticode.SelfSigner,
        UpdateKeys.Public,
        Leo.Service.Security.Authenticode.Verify,
        MsiProperties.Read,
        path =>
        {
            using var s = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s)).ToLowerInvariant();
        });

    /// <summary>Null when the MSI may be installed, else the reason (shown in Settings and the log).</summary>
    /// <param name="updateSignature">Text of the release's "….msi.sig", or null when it couldn't be downloaded.</param>
    public string? Problem(string msiPath, string? updateSignature, ReleaseVersion expected, ReleaseVersion installed, string? expectedSha256)
    {
        var self = serviceSigner();
        if (!officialBuild() && self is null) return "This is a development build, so it doesn't install updates.";

        if (updateKeys.Count == 0) return "This build has no update key, so it can't check downloads.";
        if (updateSignature is null) return "The release has no LEO update signature.";
        if (!UpdateSignature.VerifyFile(msiPath, updateSignature, updateKeys)) return "The download doesn't carry LEO's update signature.";

        if (self is not null)
        {
            var signature = verifySignature(msiPath);
            if (!signature.Valid) return $"The download isn't validly signed ({signature.Problem}).";
            if (!SignerIdentity.AnyMatches(self, signature.Signers)) return "The download is signed by someone else than LEO's publisher.";
        }

        var msi = readMsi(msiPath);
        if (msi is null) return "The download isn't a readable installer.";
        if (!string.Equals(msi.UpgradeCode, LeoInfo.UpgradeCode, StringComparison.OrdinalIgnoreCase)) return "The download is a different product.";
        if (!string.Equals(msi.ProductName, LeoInfo.ProductName, StringComparison.Ordinal)) return "The download is a different product.";
        var version = ReleaseVersion.TryParse(msi.ProductVersion);
        if (version is null || version.Core != expected.Core) return $"The download has version {msi.ProductVersion}, not {expected.Core}.";
        if (!version.UpgradesOver(installed)) return $"The download ({version.Core}) isn't newer than LEO {installed.Core}.";

        if (expectedSha256 is not null && !string.Equals(sha256(msiPath), expectedSha256, StringComparison.OrdinalIgnoreCase))
            return "The download doesn't match the release's checksum.";
        return null;
    }
}
