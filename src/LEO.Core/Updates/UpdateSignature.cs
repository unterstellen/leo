using System.Security.Cryptography;

namespace Leo.Core.Updates;

/// <summary>
/// LEO's own update signature (LEO.md §26): every release MSI comes with "LEO-x.y.z-x64.msi.sig", an ECDSA P-256 /
/// SHA-256 signature over the MSI's bytes, made in the release workflow with the private key held only in the GitHub
/// environment "release" (and the user's password manager). The updater runs an MSI only if the signature matches one
/// of <see cref="UpdateKeys.Public"/>. This works without a code-signing certificate: Windows (SmartScreen, Smart App
/// Control) doesn't trust it, but LEO's updater does, so nobody but LEO's publisher can push an update.
/// The MSI's version is inside the signed bytes, so an old signed MSI can't be passed off as a new one.
/// <para>File format, one line of ASCII: <c>leo-update-signature-v1 &lt;base64 IEEE P1363 r|s&gt;</c>.</para>
/// </summary>
public static class UpdateSignature
{
    public const string Prefix = "leo-update-signature-v1";

    /// <summary>The ".sig" text for <paramref name="data"/>. Used by tools/sign-update.ps1's logic and by tests.</summary>
    public static string Sign(Stream data, ECDsa privateKey) =>
        $"{Prefix} {Convert.ToBase64String(privateKey.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))}";

    /// <summary>True when <paramref name="signatureText"/> is a valid signature of <paramref name="data"/> by any of the keys.</summary>
    public static bool Verify(Stream data, string? signatureText, IEnumerable<string> publicKeys)
    {
        var signature = Parse(signatureText);
        if (signature is null) return false;
        foreach (var key in publicKeys)
        {
            using var ecdsa = ImportPublic(key);
            if (ecdsa is null) continue;
            if (data.CanSeek) data.Position = 0;
            if (ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return true;
            if (!data.CanSeek) return false;
        }
        return false;
    }

    public static bool VerifyFile(string path, string? signatureText, IEnumerable<string> publicKeys)
    {
        using var s = File.OpenRead(path);
        return Verify(s, signatureText, publicKeys);
    }

    internal static byte[]? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0] != Prefix) return null;
        try
        {
            var bytes = Convert.FromBase64String(parts[1]);
            return bytes.Length == 64 ? bytes : null; // P-256: r and s, 32 bytes each
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>A public key as base64 SubjectPublicKeyInfo (what <see cref="UpdateKeys.Public"/> holds); null if it isn't P-256.</summary>
    internal static ECDsa? ImportPublic(string base64Spki)
    {
        try
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(base64Spki), out _);
            if (ecdsa.KeySize != 256) { ecdsa.Dispose(); return null; }
            return ecdsa;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Base64 SubjectPublicKeyInfo of a key (tools/new-update-key.ps1 prints the same).</summary>
    public static string PublicKeyText(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
}
