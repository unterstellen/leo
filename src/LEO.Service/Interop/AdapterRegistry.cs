using Microsoft.Win32;

namespace Leo.Service.Interop;

/// <summary>Reads network-class driver "Characteristics" flags (NCF_HIDDEN, NCF_VIRTUAL, …) per interface GUID.</summary>
internal static class AdapterRegistry
{
    private const string NetClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    public static Dictionary<string, int> ReadCharacteristics()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(NetClassKey);
            if (cls is null) return result;
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (sub.Length != 4 || !char.IsDigit(sub[0])) continue; // "0000", "0001", …
                try
                {
                    using var key = cls.OpenSubKey(sub);
                    if (key?.GetValue("NetCfgInstanceId") is string id && key.GetValue("Characteristics") is int flags)
                    {
                        result[id] = flags;
                    }
                }
                catch { /* some subkeys are ACL-protected; skip */ }
            }
        }
        catch { /* best effort */ }
        return result;
    }
}
