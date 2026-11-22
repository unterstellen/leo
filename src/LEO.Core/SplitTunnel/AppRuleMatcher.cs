using System.Text;
using System.Text.RegularExpressions;

namespace Leo.Core.SplitTunnel;

/// <summary>
/// Matches executable paths against app rules, and turns rules into the engine's regex form. Pure, so the
/// service (child-process tracking) and the engine config share exactly the same semantics.
/// Precedence when several rules match: exact path > folder (deeper folder first) > name pattern.
/// </summary>
public static class AppRuleMatcher
{
    public static bool Matches(AppRule rule, string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(rule.Pattern)) return false;
        return Regex.IsMatch(exePath, ToRegex(rule), RegexOptions.CultureInvariant);
    }

    /// <summary>The most specific enabled rule for a path, or null.</summary>
    public static AppRule? FindBest(IEnumerable<AppRule> rules, string exePath) =>
        rules.Where(r => r.Enabled && Matches(r, exePath))
            .OrderBy(r => r.Kind) // ExactPath(0) < Folder(1) < NamePattern(2)
            .ThenByDescending(r => r.Kind == AppMatchKind.Folder ? NormalizeFolder(r.Pattern).Length : 0)
            .FirstOrDefault();

    /// <summary>
    /// Case-insensitive, anchored .NET/Go-compatible regex for the rule. Commas are written as \x2C because the
    /// engine's rule syntax splits on commas.
    /// </summary>
    public static string ToRegex(AppRule rule) => rule.Kind switch
    {
        AppMatchKind.ExactPath => "(?i)^" + Escape(rule.Pattern.Trim()) + "$",
        AppMatchKind.Folder => "(?i)^" + Escape(NormalizeFolder(rule.Pattern)) + @"\\.+$",
        AppMatchKind.NamePattern => @"(?i)(^|\\)" + WildcardToRegex(Path.GetFileName(rule.Pattern.Trim())) + "$",
        _ => "(?!)",
    };

    /// <summary>Folder without trailing slashes, forward slashes normalised.</summary>
    public static string NormalizeFolder(string folder) => folder.Trim().Replace('/', '\\').TrimEnd('\\');

    /// <summary>Regex for a path given as-is (used for child-process rules).</summary>
    public static string ExactPathRegex(string exePath) => "(?i)^" + Escape(exePath) + "$";

    private static string WildcardToRegex(string pattern)
    {
        var sb = new StringBuilder();
        foreach (var ch in pattern)
        {
            sb.Append(ch switch
            {
                '*' => @"[^\\]*",
                '?' => @"[^\\]",
                _ => Escape(ch.ToString()),
            });
        }
        return sb.ToString();
    }

    /// <summary>Regex-escapes a literal; commas become \x2C (engine rule syntax), Go RE2 compatible.</summary>
    private static string Escape(string literal)
    {
        var sb = new StringBuilder(literal.Length + 8);
        foreach (var ch in literal)
        {
            if (ch == ',') { sb.Append(@"\x2C"); continue; }
            if (@"\.+*?()|[]{}^$#".Contains(ch) || char.IsWhiteSpace(ch) && ch != ' ') sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
