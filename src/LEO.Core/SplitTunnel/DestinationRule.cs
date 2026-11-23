namespace Leo.Core.SplitTunnel;

/// <summary>How a destination rule recognises where traffic is going (LEO.md §6.5).</summary>
public enum DestinationKind
{
    /// <summary>A domain and all its subdomains ("youtube.com" also matches "www.youtube.com").</summary>
    DomainSuffix = 0,

    /// <summary>Exactly this host name, no subdomains.</summary>
    DomainExact = 1,

    /// <summary>Any host name containing this text (e.g. "googlevideo").</summary>
    DomainKeyword = 2,

    /// <summary>An IPv4/IPv6 address or CIDR range ("203.0.113.0/24", "2001:db8::/32").</summary>
    IpRange = 3,

    /// <summary>Any destination; only the port/protocol filter decides (e.g. all UDP 3478–3481).</summary>
    AnyHost = 4,
}

public enum DestinationProtocol
{
    Any = 0,
    Tcp = 1,
    Udp = 2,
}

/// <summary>"Traffic to this destination uses that network." Persisted in config.json.</summary>
public sealed class DestinationRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Optional label; the UI falls back to the value.</summary>
    public string DisplayName { get; set; } = "";

    public DestinationKind Kind { get; set; } = DestinationKind.DomainSuffix;

    /// <summary>Domain, keyword, or IP/CIDR depending on <see cref="Kind"/>; empty for <see cref="DestinationKind.AnyHost"/>.</summary>
    public string Value { get; set; } = "";

    /// <summary>Optional ports: "443", "80, 443", "27000-27100". Empty = every port. Required for AnyHost.</summary>
    public string Ports { get; set; } = "";

    public DestinationProtocol Protocol { get; set; } = DestinationProtocol.Any;

    /// <summary>
    /// Optional app + destination combo: only traffic from the programs of this app rule (its own match, not its
    /// child programs) to this destination. null = any app.
    /// </summary>
    public string? AppRuleId { get; set; }

    /// <summary>Adapter id (interface GUID) the traffic should use.</summary>
    public string TargetAdapterId { get; set; } = "";

    /// <summary>VPN targets only (M6): the VPN's key, so the rule finds the VPN again as a new adapter. null otherwise.</summary>
    public string? TargetVpnKey { get; set; }

    public OnTargetDown OnTargetDown { get; set; } = OnTargetDown.FallbackPrimary;

    public bool Enabled { get; set; } = true;
}

/// <summary>Per-network DNS (LEO.md §5.4): empty <see cref="Servers"/> = the network's own (DHCP) DNS.</summary>
public sealed class NetworkDnsSetting
{
    public string AdapterId { get; set; } = "";

    /// <summary>"1.1.1.1", "2606:4700:4700::1111", "https://dns.google/dns-query", "tls://1.1.1.1".</summary>
    public List<string> Servers { get; set; } = [];
}
