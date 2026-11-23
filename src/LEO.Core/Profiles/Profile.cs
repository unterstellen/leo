using Leo.Core.SplitTunnel;

namespace Leo.Core.Profiles;

/// <summary>What turns a profile on automatically (LEO.md §6.6, §19).</summary>
public enum ProfileTriggerKind
{
    /// <summary>Any Wi-Fi adapter is connected to <see cref="ProfileTrigger.Value"/> (an SSID).</summary>
    WifiConnected = 0,

    /// <summary><see cref="ProfileTrigger.Value"/> (an SSID) shows up in a Wi-Fi scan, connected or not.</summary>
    WifiVisible = 1,

    /// <summary>The adapter <see cref="ProfileTrigger.Value"/> (interface GUID, MAC fallback) is plugged in and connected.</summary>
    AdapterConnected = 2,
}

public sealed class ProfileTrigger
{
    public ProfileTriggerKind Kind { get; set; }

    /// <summary>SSID for Wi-Fi triggers; adapter id (interface GUID) for <see cref="ProfileTriggerKind.AdapterConnected"/>.</summary>
    public string Value { get; set; } = "";

    /// <summary>Adapter triggers: MAC, so a USB adapter that comes back with a new GUID still matches.</summary>
    public string? Mac { get; set; }

    /// <summary>Adapter triggers: the adapter's name when the trigger was made (shown while it's unplugged).</summary>
    public string? Label { get; set; }
}

/// <summary>Wi-Fi pins of one adapter inside a profile.</summary>
public sealed class ProfilePins
{
    public string AdapterId { get; set; } = "";
    public string? Mac { get; set; }
    public List<string> Ssids { get; set; } = [];
}

/// <summary>
/// Everything a profile switches (LEO.md §3): priority list, app + destination rules, per-network DNS, Wi-Fi pins.
/// Nicknames, health/failover settings, dual link and the split-tunneling master switch are machine-wide.
/// </summary>
public sealed class ProfileSettings
{
    public List<string> PriorityOrder { get; set; } = [];
    public List<AppRule> AppRules { get; set; } = [];
    public List<DestinationRule> DestinationRules { get; set; } = [];
    public List<NetworkDnsSetting> NetworkDns { get; set; } = [];
    public List<ProfilePins> WifiPins { get; set; } = [];
}

/// <summary>
/// A named setup (LEO.md §3, §6.6). The <b>active</b> profile's settings live in the top-level config fields
/// (PriorityOrder, SplitTunnel.*, Adapters[].PinnedSsids), so every manager keeps working on one copy;
/// <see cref="Settings"/> is the stored copy for inactive profiles and is refreshed when LEO switches away.
/// </summary>
public sealed class Profile
{
    public const string DefaultId = "default";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>Any one matching trigger turns the profile on (OR). Empty = manual only. Ignored for Default.</summary>
    public List<ProfileTrigger> Triggers { get; set; } = [];

    public ProfileSettings Settings { get; set; } = new();

    public bool IsDefault => Id == DefaultId;
}

/// <summary>Which profile is on and why (persisted, so a restart keeps it).</summary>
public sealed class ProfileState
{
    public string ActiveProfileId { get; set; } = Profile.DefaultId;

    /// <summary>The user picked <see cref="ActiveProfileId"/> by hand; automatic switching waits for the situation to change.</summary>
    public bool Manual { get; set; }

    /// <summary>Automatic switching on (default). Off = profiles only change by hand.</summary>
    public bool AutoSwitch { get; set; } = true;
}
