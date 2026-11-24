using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Leo.Core.Security;

namespace Leo.Service.Security;

/// <summary>
/// Authenticode checks for executables and MSIs (LEO.md §24.3, §24.6): <c>WinVerifyTrust</c> decides whether the
/// signature is valid (incl. a timestamp keeping an expired certificate valid), then the signer identities are read
/// from the embedded PKCS#7 through crypt32, including a nested second signature (the one-release CA/name transition).
/// Revocation is checked from cache only: a SYSTEM service shouldn't block on the network to accept a pipe client.
/// </summary>
public static class Authenticode
{
    private static readonly Lazy<SignerIdentity?> Self = new(() =>
    {
        var path = Environment.ProcessPath;
        if (path is null) return null;
        var check = Verify(path);
        return check.Valid && check.Signers.Count > 0 ? check.Signers[0] : null;
    });

    /// <summary>The running process's own signer, or null for unsigned (development) builds. Checked once.</summary>
    public static SignerIdentity? SelfSigner => Self.Value;

    public static SignatureCheck Verify(string path)
    {
        try
        {
            if (!File.Exists(path)) return SignatureCheck.Unsigned("file not found");
            var rc = WinVerifyTrust(path);
            if (rc != 0) return SignatureCheck.Unsigned($"signature not valid (0x{rc:X8})");
            var signers = ReadSigners(path);
            return signers.Count == 0
                ? SignatureCheck.Unsigned("no signer certificate")
                : new SignatureCheck(true, signers);
        }
        catch (Exception ex)
        {
            return SignatureCheck.Unsigned(ex.Message);
        }
    }

    /// <summary>Signer identities of the embedded signature(s): the primary first, then nested ones.</summary>
    internal static IReadOnlyList<SignerIdentity> ReadSigners(string path)
    {
        var result = new List<SignerIdentity>();
        if (NativeMethods.CryptQueryObject(CertQueryObjectFile, path, ContentFlagPkcs7SignedEmbed, FormatFlagBinary, 0,
                out _, out _, out _, out var store, out var msg, IntPtr.Zero))
        {
            Collect(store, msg, result, depth: 0);
        }
        return result;
    }

    internal static SignerIdentity Identify(X509Certificate2 signer, X509Certificate2Collection extra)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid | X509VerificationFlags.IgnoreCtlNotTimeValid;
        chain.ChainPolicy.ExtraStore.AddRange(extra);
        chain.Build(signer); // validity was WinVerifyTrust's job; this only finds the issuers
        var issuers = chain.ChainElements.Skip(1).Select(e => e.Certificate).ToList();
        return SignerIdentity.FromChain(signer, issuers);
    }

    // ── PKCS#7 walking ───────────────────────────────────────────────────────

    private const uint CertQueryObjectFile = 1;
    private const uint CertQueryObjectBlob = 2;
    private const uint ContentFlagPkcs7Signed = 1 << 8;
    private const uint ContentFlagPkcs7SignedEmbed = 1 << 10;
    private const uint FormatFlagBinary = 1 << 1;
    private const uint CmsgSignerCountParam = 5;
    private const uint CmsgSignerCertInfoParam = 7;
    private const uint CmsgSignerUnauthAttrParam = 10;
    private const uint CertEncoding = 0x00000001 | 0x00010000; // X509_ASN_ENCODING | PKCS_7_ASN_ENCODING
    private const uint CertFindSubjectCert = 11 << 16;
    private const string NestedSignatureOid = "1.3.6.1.4.1.311.2.4.1";

    /// <summary>Reads every signer of one PKCS#7 message, then follows nested signatures. Closes store and msg.</summary>
    private static void Collect(IntPtr store, IntPtr msg, List<SignerIdentity> into, int depth)
    {
        try
        {
            using var x509Store = new X509Store(store);
            var extra = x509Store.Certificates;
            var count = GetUInt(msg, CmsgSignerCountParam);
            for (uint i = 0; i < count; i++)
            {
                using (var certInfo = NativeParam.Get(msg, CmsgSignerCertInfoParam, i))
                {
                    if (certInfo is not null)
                    {
                        var ctx = NativeMethods.CertFindCertificateInStore(store, CertEncoding, 0, CertFindSubjectCert, certInfo.Pointer, IntPtr.Zero);
                        if (ctx != IntPtr.Zero)
                        {
                            try
                            {
                                using var cert = new X509Certificate2(ctx);
                                into.Add(Identify(cert, extra));
                            }
                            finally { NativeMethods.CertFreeCertificateContext(ctx); }
                        }
                    }
                }
                if (depth < 3) CollectNested(msg, i, into, depth);
            }
        }
        finally
        {
            if (msg != IntPtr.Zero) NativeMethods.CryptMsgClose(msg);
            if (store != IntPtr.Zero) NativeMethods.CertCloseStore(store, 0);
        }
    }

    private static void CollectNested(IntPtr msg, uint signerIndex, List<SignerIdentity> into, int depth)
    {
        using var attrs = NativeParam.Get(msg, CmsgSignerUnauthAttrParam, signerIndex);
        if (attrs is null) return;
        var header = Marshal.PtrToStructure<CryptAttributes>(attrs.Pointer);
        for (var a = 0; a < header.cAttr; a++)
        {
            var attr = Marshal.PtrToStructure<CryptAttribute>(header.rgAttr + a * Marshal.SizeOf<CryptAttribute>());
            if (Marshal.PtrToStringAnsi(attr.pszObjId) != NestedSignatureOid) continue;
            for (var v = 0; v < attr.cValue; v++)
            {
                var blobPtr = attr.rgValue + v * Marshal.SizeOf<CryptBlob>();
                if (NativeMethods.CryptQueryObjectBlob(CertQueryObjectBlob, blobPtr, ContentFlagPkcs7Signed, FormatFlagBinary, 0,
                        out _, out _, out _, out var store, out var nested, IntPtr.Zero))
                {
                    Collect(store, nested, into, depth + 1);
                }
            }
        }
    }

    private static uint GetUInt(IntPtr msg, uint param)
    {
        using var p = NativeParam.Get(msg, param, 0);
        return p is { Size: >= 4 } ? (uint)Marshal.ReadInt32(p.Pointer) : 0;
    }

    /// <summary>CryptMsgGetParam into unmanaged memory, so pointers inside the result stay valid while it's read.</summary>
    private sealed class NativeParam : IDisposable
    {
        public IntPtr Pointer { get; private set; }
        public uint Size { get; private init; }

        public static NativeParam? Get(IntPtr msg, uint param, uint index)
        {
            uint size = 0;
            if (!NativeMethods.CryptMsgGetParam(msg, param, index, IntPtr.Zero, ref size) || size == 0) return null;
            var p = Marshal.AllocHGlobal((int)size);
            if (!NativeMethods.CryptMsgGetParam(msg, param, index, p, ref size))
            {
                Marshal.FreeHGlobal(p);
                return null;
            }
            return new NativeParam { Pointer = p, Size = size };
        }

        public void Dispose()
        {
            if (Pointer != IntPtr.Zero) Marshal.FreeHGlobal(Pointer);
            Pointer = IntPtr.Zero;
        }
    }

    // ── WinVerifyTrust ───────────────────────────────────────────────────────

    private static uint WinVerifyTrust(string path)
    {
        var fileInfo = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = path };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 0,         // WTD_STATEACTION_IGNORE
                dwProvFlags = 0x00001000 | 0x00000010, // WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return NativeMethods.WinVerifyTrust(new IntPtr(-1), ref action, ref data);
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptAttributes { public int cAttr; public IntPtr rgAttr; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptAttribute { public IntPtr pszObjId; public int cValue; public IntPtr rgValue; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptBlob { public uint cbData; public IntPtr pbData; }

    private static class NativeMethods
    {
        [DllImport("wintrust.dll")]
        public static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CryptQueryObject(uint objectType, [MarshalAs(UnmanagedType.LPWStr)] string obj,
            uint expectedContentTypeFlags, uint expectedFormatTypeFlags, uint flags,
            out uint encoding, out uint contentType, out uint formatType, out IntPtr certStore, out IntPtr msg, IntPtr context);

        [DllImport("crypt32.dll", EntryPoint = "CryptQueryObject", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CryptQueryObjectBlob(uint objectType, IntPtr blob,
            uint expectedContentTypeFlags, uint expectedFormatTypeFlags, uint flags,
            out uint encoding, out uint contentType, out uint formatType, out IntPtr certStore, out IntPtr msg, IntPtr context);

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CryptMsgGetParam(IntPtr msg, uint paramType, uint index, IntPtr data, ref uint size);

        [DllImport("crypt32.dll", SetLastError = true)]
        public static extern IntPtr CertFindCertificateInStore(IntPtr store, uint encoding, uint flags, uint findType, IntPtr findPara, IntPtr prev);

        [DllImport("crypt32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CertFreeCertificateContext(IntPtr ctx);

        [DllImport("crypt32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CryptMsgClose(IntPtr msg);

        [DllImport("crypt32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CertCloseStore(IntPtr store, uint flags);
    }
}
