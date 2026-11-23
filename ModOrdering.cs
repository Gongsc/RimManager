namespace RimManager;

internal sealed class ModOrdering(IReadOnlyList<Mod> mods, string gameVersion)
{
    private static readonly StringComparer Ids = ModLibrary.Ids;
    private readonly Dictionary<string, Mod> byId = mods.ToDictionary(m => m.Id, Ids);
    internal Mod? Find(string id) => byId.GetValueOrDefault(id)
        ?? (id.EndsWith("_steam", StringComparison.OrdinalIgnoreCase)
            && byId.GetValueOrDefault(id[..^6]) is { Manifest: not null } workshop ? workshop : null);

    internal bool Incompatible(Mod mod) => gameVersion.Length > 0 && mod.Supported.Length > 0
        && !mod.Supported.Contains(gameVersion, Ids);

    internal Dictionary<string, List<string>> AllIssues(IReadOnlyList<string> order)
    {
        var issues = new Dictionary<string, List<string>>(Ids);
        var rows = order.Select((id, index) => (id, index, mod: Find(id))).Where(r => r.mod is not null).ToList();
        var byMod = rows.GroupBy(r => r.mod!).ToDictionary(g => g.Key, g => g.First());
        void Add(string id, string issue)
        {
            if (!issues.TryGetValue(id, out var list)) issues[id] = list = [];
            if (!list.Contains(issue)) list.Add(issue);
        }
        foreach (var (id, index, mod) in rows)
        foreach (var options in mod!.Dependencies)
        {
            var matches = options.Select(Find).OfType<Mod>().Where(byMod.ContainsKey).ToList();
            if (matches.Any(m => byMod[m].index < index)) continue;
            var names = string.Join(" / ", options.Select(id => Find(id)?.Name ?? id));
            Add(id, matches.Count > 0 ? $"前置须先加载 · {names}" : $"缺少已启用的前置 · {names}");
        }
        foreach (var (from, to, forced) in Rules(byMod.Keys.ToList()))
        {
            if (forced && byMod[from].index >= byMod[to].index)
                Add(byMod[to].id, $"强制顺序不符 · {from.Name} → {to.Name}");
        }
        return issues;
    }

    internal IEnumerable<(Mod From, Mod To)> Connections(IReadOnlyList<string> order)
    {
        var active = order.Select(Find).OfType<Mod>().ToList();
        foreach (var mod in active)
        foreach (var options in mod.Dependencies)
        {
            var before = active.FirstOrDefault(item => options.Contains(item.Id, Ids));
            if (before is not null) yield return (before, mod);
        }
    }

    private static IEnumerable<(Mod From, Mod To, bool Forced)> Rules(IReadOnlyList<Mod> active)
    {
        var lookup = active.ToDictionary(m => m.Id, Ids);
        foreach (var m in active)
        {
            foreach (var id in m.ForceBefore) if (lookup.TryGetValue(id, out var other)) yield return (m, other, true);
            foreach (var id in m.ForceAfter) if (lookup.TryGetValue(id, out var other)) yield return (other, m, true);
            foreach (var id in m.Before) if (lookup.TryGetValue(id, out var other)) yield return (m, other, false);
            foreach (var id in m.After) if (lookup.TryGetValue(id, out var other)) yield return (other, m, false);
        }
    }

    internal (List<string> Order, List<string> Skipped) Sort(IReadOnlyList<string> order)
    {
        var active = order.Select(Find).OfType<Mod>().Distinct().ToList();
        var rules = Rules(active).ToList();
        var edges = Connections(order).Concat(rules.Where(r => r.Forced).Select(r => (r.From, r.To))).ToHashSet();
        var pending = new List<string>();
        List<string>? TrySort()
        {
            var remaining = order.ToHashSet(Ids);
            var idsByMod = order.Where(id => Find(id) is not null).GroupBy(id => Find(id)!)
                .ToDictionary(g => g.Key, g => g.ToArray());
            var prerequisites = edges.ToLookup(e => e.To, e => e.From);
            var result = new List<string>();
            while (remaining.Count > 0)
            {
                var next = order.FirstOrDefault(id => remaining.Contains(id) &&
                    (Find(id) is not Mod mod || !prerequisites[mod].Any(dep => idsByMod[dep].Any(remaining.Contains))));
                if (next is null)
                {
                    pending = remaining.ToList();
                    return null;
                }
                result.Add(next);
                remaining.Remove(next);
            }
            return result;
        }
        var sorted = TrySort() ?? throw new InvalidOperationException("必需依赖或强制顺序成环，涉及 " +
            string.Join("、", pending.Select(id => Find(id)?.Name ?? id)));
        var skipped = new List<string>();
        foreach (var (from, to, _) in rules.Where(r => !r.Forced))
        {
            if (!edges.Add((from, to))) continue;
            var candidate = TrySort();
            if (candidate is not null) sorted = candidate;
            else { edges.Remove((from, to)); skipped.Add($"{from.Name} → {to.Name}"); }
        }
        return (sorted, skipped);
    }
}
