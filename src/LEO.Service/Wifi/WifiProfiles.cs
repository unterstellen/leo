using System.Security;
using System.Text;
using System.Xml.Linq;
using Leo.Service.Interop;

namespace Leo.Service.Wifi;

/// <summary>Builds and inspects WLAN profile XML (personal networks only; enterprise needs Windows' own UI).</summary>
internal static class WifiProfiles
{
    private static readonly XNamespace Ns = "http://www.microsoft.com/networking/WLAN/profile/v1";

    public static string SecurityLabel(int auth) => auth switch
    {
        WlanApi.AuthOpen => "Open",
        WlanApi.AuthOwe => "Open (enhanced)",
        WlanApi.AuthWpaPsk => "WPA-Personal",
        WlanApi.AuthRsnaPsk => "WPA2-Personal",
        WlanApi.AuthWpa3Sae => "WPA3-Personal",
        3 or 6 or 8 or 11 => "Enterprise",
        _ => "Other",
    };

    /// <summary>True when LEO can create a profile for this network (open or a personal passphrase).</summary>
    public static bool CanCreate(int auth) => auth is WlanApi.AuthOpen or WlanApi.AuthWpaPsk or WlanApi.AuthRsnaPsk or WlanApi.AuthWpa3Sae;

    public static bool NeedsPassword(int auth) => auth is WlanApi.AuthWpaPsk or WlanApi.AuthRsnaPsk or WlanApi.AuthWpa3Sae;

    /// <summary>Profile XML for a personal network. Windows encrypts the key when it stores the profile.</summary>
    public static string Build(string ssid, int auth, string? passphrase)
    {
        var (authentication, encryption) = auth switch
        {
            WlanApi.AuthOpen => ("open", "none"),
            WlanApi.AuthWpaPsk => ("WPAPSK", "TKIP"),
            WlanApi.AuthRsnaPsk => ("WPA2PSK", "AES"),
            WlanApi.AuthWpa3Sae => ("WPA3SAE", "AES"),
            _ => throw new InvalidOperationException("This network uses enterprise or unsupported security; connect to it once in Windows Wi-Fi settings."),
        };

        var hex = Convert.ToHexString(Encoding.UTF8.GetBytes(ssid));
        var name = SecurityElement.Escape(ssid);
        var sharedKey = NeedsPassword(auth)
            ? $"<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{SecurityElement.Escape(passphrase ?? "")}</keyMaterial></sharedKey>"
            : "";
        var sae = auth == WlanApi.AuthWpa3Sae ? "<transitionMode xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v4\">true</transitionMode>" : "";

        return $"""
            <?xml version="1.0"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>{name}</name>
              <SSIDConfig><SSID><hex>{hex}</hex><name>{name}</name></SSID></SSIDConfig>
              <connectionType>ESS</connectionType>
              <connectionMode>auto</connectionMode>
              <MSM><security>
                <authEncryption><authentication>{authentication}</authentication><encryption>{encryption}</encryption><useOneX>false</useOneX>{sae}</authEncryption>
                {sharedKey}
              </security></MSM>
            </WLANProfile>
            """;
    }

    /// <summary>The SSID a profile XML targets (hex form preferred, it's exact for non-ASCII names).</summary>
    public static string? SsidOf(string profileXml)
    {
        try
        {
            var ssid = XDocument.Parse(profileXml).Descendants(Ns + "SSID").FirstOrDefault();
            if (ssid is null) return null;
            if (ssid.Element(Ns + "hex")?.Value is { Length: > 0 } hex) return Encoding.UTF8.GetString(Convert.FromHexString(hex));
            return ssid.Element(Ns + "name")?.Value;
        }
        catch
        {
            return null;
        }
    }
}
