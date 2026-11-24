#:project ../src/LEO.Core/LEO.Core.csproj
#:property TargetFramework=net10.0-windows10.0.26100
// LEO's own update key (LEO.md §26). A one-file .NET app: run it with `dotnet run tools/update-key.cs -- <verb>`.
//
//   new                     Makes a new key pair and PRINTS it (writes nothing to disk):
//                           - the public key  → paste into src/LEO.Core/Updates/UpdateKeys.cs
//                           - the private key → your password manager AND the GitHub secret LEO_UPDATE_KEY
//                             (Settings → Environments → release). Never commit it, never paste it into a chat.
//   sign <msi>              Release workflow: signs <msi> with the private key from the environment variable
//                           LEO_UPDATE_KEY, writes <msi>.sig, and checks the result against UpdateKeys.Public
//                           (fails if the secret doesn't belong to a key LEO accepts).
//   verify <msi> [<sig>]    Checks <msi> against <sig> (default <msi>.sig) with UpdateKeys.Public.
//   selftest                Makes a throwaway key in memory, signs and verifies a sample (prints no key).
using System.Security.Cryptography;
using Leo.Core.Updates;

return args.FirstOrDefault() switch
{
    "new" => New(),
    "sign" when args.Length == 2 => Sign(args[1]),
    "verify" when args.Length is 2 or 3 => Verify(args[1], args.Length == 3 ? args[2] : args[1] + ".sig"),
    "selftest" => SelfTest(),
    _ => Usage(),
};

static int New()
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    Console.WriteLine("LEO update key (ECDSA P-256). Nothing was written to disk.");
    Console.WriteLine();
    Console.WriteLine("PUBLIC key: paste into src/LEO.Core/Updates/UpdateKeys.cs (it isn't secret):");
    Console.WriteLine($"  \"{UpdateSignature.PublicKeyText(key)}\",");
    Console.WriteLine();
    Console.WriteLine("PRIVATE key: save in your password manager, then add it as the GitHub secret LEO_UPDATE_KEY");
    Console.WriteLine("in Settings > Environments > release. Don't commit it or paste it anywhere else. Then clear this window (cls).");
    Console.WriteLine($"  {Convert.ToBase64String(key.ExportPkcs8PrivateKey())}");
    return 0;
}

static int Sign(string msi)
{
    var secret = Environment.GetEnvironmentVariable("LEO_UPDATE_KEY");
    if (string.IsNullOrWhiteSpace(secret)) return Fail("LEO_UPDATE_KEY isn't set (GitHub environment \"release\" secret).");
    if (UpdateKeys.Public.Count == 0) return Fail("src/LEO.Core/Updates/UpdateKeys.cs has no public key yet (LEO.md §26).");
    using var key = ECDsa.Create();
    try { key.ImportPkcs8PrivateKey(Convert.FromBase64String(secret.Trim()), out _); }
    catch (Exception ex) when (ex is FormatException or CryptographicException) { return Fail("LEO_UPDATE_KEY isn't a valid private key."); }
    if (!UpdateKeys.Public.Contains(UpdateSignature.PublicKeyText(key)))
        return Fail("LEO_UPDATE_KEY doesn't belong to any key in UpdateKeys.cs, so installed LEOs would refuse this update.");

    string text;
    using (var s = File.OpenRead(msi)) text = UpdateSignature.Sign(s, key);
    File.WriteAllText(msi + ".sig", text + "\n");
    if (!UpdateSignature.VerifyFile(msi, text, UpdateKeys.Public)) return Fail("The new signature doesn't verify.");
    Console.WriteLine($"Signed {Path.GetFileName(msi)} -> {Path.GetFileName(msi)}.sig");
    return 0;
}

static int Verify(string msi, string sig)
{
    var ok = File.Exists(sig) && UpdateSignature.VerifyFile(msi, File.ReadAllText(sig), UpdateKeys.Public);
    Console.WriteLine(ok ? $"OK: {Path.GetFileName(msi)} carries LEO's update signature." : $"NOT VALID: {Path.GetFileName(msi)}");
    return ok ? 0 : 1;
}

static int SelfTest()
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var data = "LEO"u8.ToArray();
    var text = UpdateSignature.Sign(new MemoryStream(data), key);
    var good = UpdateSignature.Verify(new MemoryStream(data), text, [UpdateSignature.PublicKeyText(key)]);
    var bad = UpdateSignature.Verify(new MemoryStream("LEX"u8.ToArray()), text, [UpdateSignature.PublicKeyText(key)]);
    Console.WriteLine(good && !bad ? "selftest OK" : "selftest FAILED");
    return good && !bad ? 0 : 1;
}

static int Usage()
{
    Console.Error.WriteLine("usage: dotnet run tools/update-key.cs -- new | sign <msi> | verify <msi> [<sig>] | selftest");
    return 2;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
