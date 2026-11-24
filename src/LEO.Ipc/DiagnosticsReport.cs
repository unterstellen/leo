using System.Text;
using System.Text.RegularExpressions;

namespace Leo.Ipc;

/// <summary>
/// Settings → About → "Copy diagnostics" (LEO.md §24.7): a text report the tester pastes into a GitHub issue
/// themselves; LEO sends nothing. SSIDs, network and adapter nicknames, IP and MAC addresses are left out unless the
/// user ticks "Include network names and addresses". Log lines are warnings and errors only, and never hold
/// per-connection data (§17–§19); addresses in them are masked too.
/// </summary>
public static partial class DiagnosticsReport
{
    public sealed record Input(
        string AppVersion,
        string? ServiceVersion,
        bool Signed,
        string? Signer,
        string WindowsVersion,
        NetworkSnapshot Networks,
        SplitTunnelInfo SplitTunnel,
        IReadOnlyList<string> ServiceLogLines,
        bool IncludePersonal);

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex IPv4();

    // Four or more groups, or a "::" form; never a time of day like 15:03:22.
    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{1,4}:){3,7}[0-9A-Fa-f]{1,4}\b|\b(?:[0-9A-Fa-f]{1,4}:)+:(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4})*)?|::(?:[0-9A-Fa-f]{1,4}:)*[0-9A-Fa-f]{1,4}\b")]
    private static partial Regex IPv6();

    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b")]
    private static partial Regex Mac();

    public static string Build(Input i)
    {
        var sb = new StringBuilder();
        sb.AppendLine("LEO diagnostics");
        sb.AppendLine($"LEO app {i.AppVersion}, service {i.ServiceVersion ?? "not connected"}, {(i.Signed ? "signed by " + i.Signer : Leo.Core.LeoInfo.IsOfficialBuild ? "official build, not code-signed" : "development build")}");
        sb.AppendLine($"Windows {i.WindowsVersion}");
        sb.AppendLine(i.IncludePersonal ? "Names and addresses: included" : "Names and addresses: left out");
        sb.AppendLine();

        sb.AppendLine("Adapters:");
        var n = 0;
        foreach (var a in i.Networks.Adapters)
        {
            n++;
            var label = i.IncludePersonal ? $"{a.Name}{(a.Nickname is null ? "" : $" \"{a.Nickname}\"")}" : $"Adapter {n}";
            sb.Append($"- {label}: {a.Kind}, {a.Description}, {a.Status}");
            if (a.LinkSpeedBps > 0) sb.Append($", {a.LinkSpeedBps / 1_000_000} Mbit/s");
            sb.Append($", health {a.Health}");
            if (a.LatencyMs is { } ms) sb.Append($" {ms} ms");
            if (a.IsPrimary) sb.Append(", Primary");
            if (a.IsDemoted) sb.Append(", demoted by failover");
            if (a.DualLinkSupported) sb.Append($", dual link {(a.DualLinkEnabled ? "on" : "off")}");
            if (a.FollowsAdapterId is not null) sb.Append(", secondary Wi-Fi station");
            if (a.Network is { } net)
            {
                sb.Append($", {net.Connectivity}{(net.IsMetered ? ", metered" : "")}");
                if (i.IncludePersonal) sb.Append($", network \"{net.Nickname ?? net.Name}\"");
            }
            if (a.Vpn is { } vpn) sb.Append($", VPN {vpn.Key}{(vpn.Connected ? " connected" : "")}{(vpn.TakesDefault4 ? ", takes the default route" : "")}");
            if (i.IncludePersonal)
            {
                if (a.Mac is not null) sb.Append($", MAC {a.Mac}");
                if (a.IPv4.Count > 0) sb.Append($", IPv4 {string.Join(" ", a.IPv4)}");
            }
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(a.HealthDetail)) sb.AppendLine($"  {Mask(a.HealthDetail, i.IncludePersonal)}");
        }
        if (n == 0) sb.AppendLine("- none (service not connected)");
        sb.AppendLine($"Failover: {(i.Networks.AutoFailover ? "on" : "off")}{(i.Networks.FailoverActive ? ", active now" : "")}; metrics managed: {i.Networks.ManageMetrics}");
        sb.AppendLine();

        var st = i.SplitTunnel;
        sb.AppendLine($"Split tunneling: {(st.Enabled ? "on" : "off")}, engine {st.State}, {st.Rules.Count} app rule(s), " +
                      $"{st.Destinations?.Count ?? 0} destination rule(s), {st.Bonds?.Count ?? 0} bond(s)");
        if (!string.IsNullOrWhiteSpace(st.Message)) sb.AppendLine($"  {Mask(st.Message, i.IncludePersonal)}");
        if (!string.IsNullOrWhiteSpace(st.VpnBanner)) sb.AppendLine($"  {Mask(st.VpnBanner, i.IncludePersonal)}");
        sb.AppendLine();

        sb.AppendLine("Recent service warnings and errors:");
        var lines = i.ServiceLogLines.Where(IsWarningOrError).TakeLast(40).ToList();
        foreach (var line in lines) sb.AppendLine(Mask(line, i.IncludePersonal));
        if (lines.Count == 0) sb.AppendLine("- none");
        return sb.ToString();
    }

    /// <summary>The service log's level token: "yyyy-MM-dd HH:mm:ss.fff zzz WARN …" (also ERRO, CRIT).</summary>
    public static bool IsWarningOrError(string line) =>
        line.Contains(" WARN ", StringComparison.Ordinal) || line.Contains(" ERRO ", StringComparison.Ordinal) ||
        line.Contains(" CRIT ", StringComparison.Ordinal);

    /// <summary>Masks IP and MAC addresses unless the user asked to include them.</summary>
    public static string Mask(string text, bool includePersonal)
    {
        if (includePersonal) return text;
        var masked = Mac().Replace(text, "<mac>");
        masked = IPv4().Replace(masked, "<ip>");
        return IPv6().Replace(masked, "<ip>");
    }
}
