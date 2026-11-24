using Leo.Core.Networking;

namespace Leo.Core.SplitTunnel;

/// <summary>How a bond spreads new connections over its members (LEO.md §22.2).</summary>
public enum BondMode
{
    /// <summary>Each site (registrable domain, or address) stays on one member; sites spread. Engine: consistent-hashing.</summary>
    BySite = 0,

    /// <summary>Like <see cref="BySite"/>, shares follow the members' weights (members repeated in the engine group).</summary>
    Weighted = 1,

    /// <summary>Every new connection goes to the next member, even to the same site. Engine: round-robin.</summary>
    EveryConnection = 2,

    /// <summary>All new connections use the member with the lowest health-check latency. Engine: url-test.</summary>
    Fastest = 3,
}

/// <summary>One network in a bond.</summary>
public sealed class BondMember
{
    /// <summary>Adapter id (interface GUID).</summary>
    public string AdapterId { get; set; } = "";

    /// <summary>MAC fallback for re-plugged USB tethers (a new GUID on another port).</summary>
    public string? Mac { get; set; }

    /// <summary>1–5, used by <see cref="BondMode.Weighted"/> and <see cref="BondMode.EveryConnection"/>.</summary>
    public int Weight { get; set; } = 1;
}

/// <summary>
/// A bond group (LEO.md §6.8, §22): several physical networks used together. Machine-wide (user's decision, §22.8 Q2):
/// not part of a profile. Rules target it as <c>bond:&lt;id&gt;</c> (<see cref="BondTargets"/>).
/// </summary>
public sealed class Bond
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<BondMember> Members { get; set; } = [];
    public BondMode Mode { get; set; } = BondMode.BySite;

    /// <summary>When no member is usable: use Primary (default) or block.</summary>
    public OnTargetDown OnAllDown { get; set; } = OnTargetDown.FallbackPrimary;

    public Bond Clone() => new()
    {
        Id = Id, Name = Name, Mode = Mode, OnAllDown = OnAllDown,
        Members = Members.Select(m => new BondMember { AdapterId = m.AdapterId, Mac = m.Mac, Weight = m.Weight }).ToList(),
    };
}

/// <summary>Rule targets that point at a bond: <c>bond:&lt;id&gt;</c> in the rule's <c>TargetAdapterId</c> (adapter ids are GUIDs, no clash).</summary>
public static class BondTargets
{
    public const string Prefix = "bond:";

    public static bool IsBond(string? target) => target is not null && target.StartsWith(Prefix, StringComparison.Ordinal);

    public static string For(string bondId) => Prefix + bondId;

    public static string BondId(string target) => IsBond(target) ? target[Prefix.Length..] : "";
}

/// <summary>An adapter as bond logic sees it (physical networks only).</summary>
public sealed record BondAdapter(string Id, string? Mac, string Name, AdapterKind Kind, bool Connected, bool Usable,
    bool Metered = false, string? GatewayMac = null);

/// <summary>Validation and member resolution for bonds (LEO.md §22.1). Pure.</summary>
public static class BondRules
{
    public const int MinMembers = 2;
    public const int MaxMembers = 8;
    public const int MaxWeight = 5;

    /// <summary>Normalises and checks a bond; returns a user-facing problem or null.</summary>
    public static string? Validate(Bond bond, IReadOnlyList<BondAdapter> adapters, IEnumerable<Bond> others)
    {
        bond.Name = (bond.Name ?? "").Trim();
        if (bond.Name.Length == 0) return "Give the bond a name.";
        if (bond.Name.Length > 40) return "Use a shorter name (up to 40 characters).";
        if (others.Any(b => b.Id != bond.Id && b.Name.Equals(bond.Name, StringComparison.OrdinalIgnoreCase)))
            return $"There's already a bond called '{bond.Name}'.";
        bond.Members = bond.Members.Where(m => !string.IsNullOrWhiteSpace(m.AdapterId))
            .GroupBy(m => m.AdapterId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (bond.Members.Count < MinMembers) return "Choose at least two networks for the bond.";
        if (bond.Members.Count > MaxMembers) return $"A bond can have up to {MaxMembers} networks.";
        foreach (var m in bond.Members)
        {
            var a = adapters.FirstOrDefault(x => x.Id.Equals(m.AdapterId, StringComparison.OrdinalIgnoreCase));
            if (a is not null && a.Kind == AdapterKind.Vpn)
                return $"{a.Name} is a VPN. VPNs can't be part of a bond: they ride on the same physical networks.";
            if (a is not null && !a.Kind.IsPrimaryCandidate()) return $"{a.Name} can't be part of a bond.";
            if (a is not null && string.IsNullOrWhiteSpace(m.Mac)) m.Mac = a.Mac;
            m.Weight = Math.Clamp(m.Weight, 1, MaxWeight);
        }
        if (!Enum.IsDefined(bond.Mode)) bond.Mode = BondMode.BySite;
        if (!Enum.IsDefined(bond.OnAllDown)) bond.OnAllDown = OnTargetDown.FallbackPrimary;
        return null;
    }

    /// <summary>A member's adapter now: by id, else by MAC (re-plugged tether), else null.</summary>
    public static BondAdapter? Resolve(BondMember m, IReadOnlyList<BondAdapter> adapters) =>
        adapters.FirstOrDefault(a => a.Id.Equals(m.AdapterId, StringComparison.OrdinalIgnoreCase))
        ?? (string.IsNullOrWhiteSpace(m.Mac) ? null
            : adapters.FirstOrDefault(a => a.Mac is not null && a.Mac.Equals(m.Mac, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Members in the user's order with their adapter (null = not present).</summary>
    public static IReadOnlyList<(BondMember Member, BondAdapter? Adapter)> Members(Bond bond, IReadOnlyList<BondAdapter> adapters) =>
        bond.Members.Select(m => (m, Resolve(m, adapters))).ToList();

    /// <summary>Usable member adapters (connected, internet per LEO's health), in the user's order.</summary>
    public static IReadOnlyList<BondAdapter> Usable(Bond bond, IReadOnlyList<BondAdapter> adapters) =>
        Members(bond, adapters).Where(x => x.Adapter is { Usable: true }).Select(x => x.Adapter!).ToList();

    /// <summary>"WiFi and WiFi 2 use the same router: bonding them adds no internet speed." (same gateway MAC), or null.</summary>
    public static string? SameRouterHint(Bond bond, IReadOnlyList<BondAdapter> adapters)
    {
        var connected = Members(bond, adapters).Where(x => x.Adapter is { Connected: true } && !string.IsNullOrWhiteSpace(x.Adapter.GatewayMac))
            .Select(x => x.Adapter!).ToList();
        var same = connected.GroupBy(a => a.GatewayMac!, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        return same is null ? null
            : $"{string.Join(" and ", same.Select(a => a.Name))} use the same router: bonding them adds no internet speed.";
    }

    /// <summary>
    /// Which member answers DNS for bond-targeted domains (LEO.md §22.5): the Primary if it is a usable member, else the
    /// first usable member in the bond's order; null = none usable.
    /// </summary>
    public static string? DnsMember(Bond bond, IReadOnlyList<BondAdapter> adapters, string? primaryId)
    {
        var usable = Usable(bond, adapters);
        return usable.FirstOrDefault(a => a.Id.Equals(primaryId, StringComparison.OrdinalIgnoreCase))?.Id ?? usable.FirstOrDefault()?.Id;
    }

    /// <summary>Short UI name of a mode.</summary>
    public static string ModeName(BondMode mode) => mode switch
    {
        BondMode.BySite => "By site",
        BondMode.Weighted => "Weighted",
        BondMode.EveryConnection => "Every connection",
        BondMode.Fastest => "Fastest",
        _ => mode.ToString(),
    };
}
