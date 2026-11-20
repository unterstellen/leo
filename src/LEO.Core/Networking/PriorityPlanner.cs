namespace Leo.Core.Networking;

/// <summary>An adapter as the planner sees it.</summary>
public sealed record PlannerAdapter(string Id, AdapterKind Kind, bool IsUp, bool HasGateway, int CurrentMetric);

/// <summary>Result of planning: the effective order, target metrics, and who is Primary right now.</summary>
public sealed record PriorityPlan(IReadOnlyList<string> Order, IReadOnlyDictionary<string, int> Metrics, string? PrimaryId);

/// <summary>
/// Turns the user's priority list into interface metrics (LEO.md §5.1). Pure and deterministic.
/// Lower metric wins, so position 0 gets the lowest. Adapters the user hasn't ranked yet are appended
/// in a sensible default order (wired → Wi-Fi → tether → cellular → Bluetooth).
/// </summary>
public static class PriorityPlanner
{
    public const int BaseMetric = 10;
    public const int Step = 10;

    public static PriorityPlan Plan(IReadOnlyList<PlannerAdapter> adapters, IReadOnlyList<string> userOrder)
    {
        var candidates = adapters.Where(a => a.Kind.IsPrimaryCandidate()).ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

        var order = new List<string>();
        foreach (var id in userOrder)
        {
            if (candidates.ContainsKey(id) && !order.Contains(id, StringComparer.OrdinalIgnoreCase)) order.Add(id);
        }

        // Unranked: prefer kind (wired first), then adapters that work right now, then Windows' own metric.
        var unranked = candidates.Values
            .Where(a => !order.Contains(a.Id, StringComparer.OrdinalIgnoreCase))
            .OrderBy(a => a.Kind.DefaultRank())
            .ThenByDescending(a => a.IsUp && a.HasGateway)
            .ThenBy(a => a.CurrentMetric)
            .ThenBy(a => a.Id, StringComparer.OrdinalIgnoreCase);
        order.AddRange(unranked.Select(a => a.Id));

        var metrics = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < order.Count; i++) metrics[order[i]] = BaseMetric + i * Step;

        var primary = order.FirstOrDefault(id => candidates[id] is { IsUp: true, HasGateway: true });
        return new PriorityPlan(order, metrics, primary);
    }

    /// <summary>Returns <paramref name="order"/> with <paramref name="id"/> moved to the front.</summary>
    public static List<string> MoveToFront(IReadOnlyList<string> order, string id)
    {
        var list = order.Where(x => !x.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Insert(0, id);
        return list;
    }
}
