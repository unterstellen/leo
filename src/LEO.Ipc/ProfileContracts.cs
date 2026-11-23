using Leo.Core.Profiles;

namespace Leo.Ipc;

/// <summary>One profile as the UI shows it (M5, LEO.md §19).</summary>
public sealed record ProfileInfo(
    string Id,
    string Name,
    bool IsDefault,
    bool IsActive,
    IReadOnlyList<ProfileTrigger> Triggers,
    IReadOnlyList<string> TriggerTexts,   // "Wi-Fi “Zixel” is connected"
    bool TriggerMatches,                  // one of its triggers matches right now
    string Summary,                       // "2 app rules · 1 destination rule · 1 Wi-Fi pin"
    IReadOnlyList<string> PriorityNames); // its priority list as adapter names (connected or remembered)

/// <summary>All profiles, the active one and why it's active.</summary>
public sealed record ProfilesInfo(
    IReadOnlyList<ProfileInfo> Profiles,
    string ActiveProfileId,
    string ActiveProfileName,
    bool Manual,          // the user picked it; automatic switching waits for the networks to change
    bool AutoSwitch,      // automatic switching is on
    bool Paused,          // automatic switching paused because profiles kept switching back and forth
    string Reason,        // "Automatic: Wi-Fi “Zixel” is connected"
    string? PendingText)  // "Switching to Phone in 12 s (Ethernet is connected)"
{
    public static ProfilesInfo Empty { get; } = new([], Profile.DefaultId, "Default", false, true, false, "", null);
}

/// <summary>Create (Id null) or edit a profile's name and triggers. Its rules/priority/DNS/pins are edited while it's active.</summary>
public sealed record ProfileEdit(
    string? Id,
    string Name,
    IReadOnlyList<ProfileTrigger> Triggers,
    bool StartEmpty = false); // new profiles: start with no rules/pins instead of a copy of the active profile
