namespace Leo.Core.Networking;

/// <summary>An adapter as the planner sees it.</summary>
public sealed record PlannerAdapter(string Id, AdapterKind Kind, bool IsUp, bool HasGateway, int CurrentMetric);

/// <summary>Result of planning: the effective order, target metrics, and who is Primary right now.</summary>
/// <param name="Order">Effective order (user ranking with demoted adapters moved behind healthy ones).</param>
/// <param name="Ranking">The user's ranking only (what the UI shows as #1, #2…).</param>
public sealed record PriorityPlan(
    IReadOnlyList<string> Order,
    IReadOnlyList<string> Ranking,
    IReadOnlyDictionary<string, int> Metrics,
    string? PrimaryId);

/// <summary>
/// Turns the user's priority list into interface metrics (LEO.md §5.1). Pure and deterministic.
/// Lower metric wins, so position 0 gets the lowest. Adapters the user hasn't ranked yet are appended
/// in a sensible default order (wired → Wi-Fi → tether → cellular → Bluetooth).
/// Failover (LEO.md §5.7): <paramref name="demoted"/> adapters keep their rank but get metrics behind
/// every healthy one, so Windows moves traffic off them without the user's list changing.
/// </summary>
public static class PriorityPlanner
{
    public const int BaseMetric = 10;
    public const int Step = 10;

    public static PriorityPlan Plan(IReadOnlyList<PlannerAdapter> adapters, IReadOnlyList<string> userOrder,
        IReadOnlySet<string>? demoted = null)
    {
        var candidates = adapters.Where(a => a.Kind.IsPrimaryCandidate()).ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

        var ranking = new List<string>();
        foreach (var id in userOrder)
        {
            if (candidates.ContainsKey(id) && !ranking.Contains(id, StringComparer.OrdinalIgnoreCase)) ranking.Add(id);
        }

        // Unranked: prefer kind (wired first), then adapters that work right now, then Windows' own metric.
        var unranked = candidates.Values
            .Where(a => !ranking.Contains(a.Id, StringComparer.OrdinalIgnoreCase))
            .OrderBy(a => a.Kind.DefaultRank())
            .ThenByDescending(a => a.IsUp && a.HasGateway)
            .ThenBy(a => a.CurrentMetric)
            .ThenBy(a => a.Id, StringComparer.OrdinalIgnoreCase);
        ranking.AddRange(unranked.Select(a => a.Id));

        bool IsDemoted(string id) => demoted?.Contains(id) == true;
        var order = ranking.Where(id => !IsDemoted(id)).Concat(ranking.Where(IsDemoted)).ToList();

        var metrics = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < order.Count; i++) metrics[order[i]] = BaseMetric + i * Step;

        bool Routable(string id) => candidates[id] is { IsUp: true, HasGateway: true };
        var primary = order.FirstOrDefault(id => Routable(id) && !IsDemoted(id)) ?? order.FirstOrDefault(Routable);
        return new PriorityPlan(order, ranking, metrics, primary);
    }

    /// <summary>Returns <paramref name="order"/> with <paramref name="id"/> moved to the front.</summary>
    public static List<string> MoveToFront(IReadOnlyList<string> order, string id)
    {
        var list = order.Where(x => !x.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Insert(0, id);
        return list;
    }
}
