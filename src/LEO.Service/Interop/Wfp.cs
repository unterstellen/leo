using System.Runtime.InteropServices;

namespace Leo.Service.Interop;

/// <summary>
/// Read-only Windows Filtering Platform access (LEO.md §20.4): the display names of registered WFP providers, used only
/// to name the product whose firewall blocks traffic. LEO never adds, edits or removes WFP filters or providers.
/// </summary>
public static class Wfp
{
    /// <summary>Provider display names, or an empty list when WFP can't be opened (best effort).</summary>
    public static List<string> ProviderNames()
    {
        var names = new List<string>();
        IntPtr engine = IntPtr.Zero, enumHandle = IntPtr.Zero, entries = IntPtr.Zero;
        try
        {
            if (FwpmEngineOpen0(null, RPC_C_AUTHN_WINNT, IntPtr.Zero, IntPtr.Zero, out engine) != 0) return names;
            if (FwpmProviderCreateEnumHandle0(engine, IntPtr.Zero, out enumHandle) != 0) return names;
            if (FwpmProviderEnum0(engine, enumHandle, 1000, out entries, out var count) != 0) return names;
            for (var i = 0; i < count; i++)
            {
                var provider = Marshal.ReadIntPtr(entries, i * IntPtr.Size);
                // FWPM_PROVIDER0: GUID providerKey (16), FWPM_DISPLAY_DATA0 { wchar_t* name; wchar_t* description } …
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(provider, 16));
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or AccessViolationException) { }
        finally
        {
            if (entries != IntPtr.Zero) FwpmFreeMemory0(ref entries);
            if (enumHandle != IntPtr.Zero) FwpmProviderDestroyEnumHandle0(engine, enumHandle);
            if (engine != IntPtr.Zero) FwpmEngineClose0(engine);
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The provider that most likely owns a VPN's firewall: one whose name shares a word with the VPN adapter's name or
    /// description (e.g. "Mullvad VPN" for "Mullvad Tunnel"). Pure, for tests.
    /// </summary>
    public static string? OwnerFor(IEnumerable<string> providers, string vpnName, string vpnDescription)
    {
        var words = $"{vpnName} {vpnDescription}".Split([' ', '-', '_', '(', ')', ':', '#'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4 && !Generic.Contains(w, StringComparer.OrdinalIgnoreCase)).ToList();
        return providers.FirstOrDefault(p => words.Any(w => p.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    private static readonly string[] Generic = ["Tunnel", "Adapter", "Network", "Virtual", "Ethernet", "Windows", "Driver", "Interface", "Connection", "Local", "Area"];

    private const uint RPC_C_AUTHN_WINNT = 10;

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    private static extern uint FwpmEngineOpen0(string? serverName, uint authnService, IntPtr authIdentity, IntPtr session, out IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmProviderCreateEnumHandle0(IntPtr engineHandle, IntPtr enumTemplate, out IntPtr enumHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmProviderEnum0(IntPtr engineHandle, IntPtr enumHandle, uint numEntriesRequested, out IntPtr entries, out uint numEntriesReturned);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmProviderDestroyEnumHandle0(IntPtr engineHandle, IntPtr enumHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern void FwpmFreeMemory0(ref IntPtr p);
}
