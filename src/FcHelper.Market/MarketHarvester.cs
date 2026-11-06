using System.Globalization;

namespace FcHelper.Market;

public enum RefreshKind
{
    /// <summary>Everything: every stat pass and all tags. First run, and once when the collected stats change (at least 2,400 requests).</summary>
    Full,
    /// <summary>Prices and the first stat pass; the rest is carried over (about 260 requests).</summary>
    Prices,
}

public sealed record HarvestProgress(int Done, int Total, string Step);

/// <summary>
/// Turns a refresh into keyed steps and runs the ones not done yet, so an interrupted refresh resumes.
/// Slicing: the list returns at most 200 rows, so each query is one position group and one OVR value, split by
/// salary (or by OVR halves for tag and season queries) while a slice is full.
/// </summary>
public sealed class MarketHarvester(IMarketListSource source, MarketStore store)
{
    private sealed record Step(string Key, Func<CancellationToken, Task> Run);

    public async Task RunAsync(long snapshot, RefreshKind kind, long? previousSnapshot, IReadOnlyCollection<int> newSeasons,
        IProgress<HarvestProgress>? progress, CancellationToken ct)
    {
        var steps = Plan(snapshot, kind, previousSnapshot);
        var done = store.DoneKeys(snapshot);
        var count = await Run(snapshot, steps, done, 0, steps.Count, progress, ct);
        if (kind == RefreshKind.Prices)
        {
            // Second phase, once the lists are in: seasons with cards that never got their other stats and tags.
            var later = SeasonSteps(snapshot, SeasonsToComplete(snapshot, newSeasons));
            count = await Run(snapshot, later, done, count, steps.Count + later.Count, progress, ct);
            steps.AddRange(later);
        }
        progress?.Report(new HarvestProgress(count, steps.Count, "done"));
    }

    private async Task<int> Run(long snapshot, List<Step> steps, HashSet<string> done, int count, int total, IProgress<HarvestProgress>? progress, CancellationToken ct)
    {
        count += steps.Count(s => done.Contains(s.Key));
        foreach (var step in steps)
        {
            if (done.Contains(step.Key)) continue;
            progress?.Report(new HarvestProgress(count, total, step.Key));
            await step.Run(ct);
            store.MarkDone(snapshot, step.Key);
            count++;
        }
        return count;
    }

    /// <summary>
    /// The season filter of the second phase: new seasons in the official list plus any season with a card that has only
    /// the first stat pass (new cards the season list did not announce, or a refresh that missed it). Kept with the
    /// snapshot so a resumed refresh asks for the same seasons even after some cards were completed.
    /// </summary>
    private string SeasonsToComplete(long snapshot, IReadOnlyCollection<int> newSeasons)
    {
        var key = $"harvest.seasons.{snapshot}";
        if (store.GetValue(key) is { } saved) return saved.Value;
        var seasons = newSeasons.Concat(store.UncoveredSeasons(snapshot)).Distinct().Order().ToList();
        var filter = seasons.Count == 0 ? "" : "," + string.Join(",", seasons.Select(s => s.ToString(CultureInfo.InvariantCulture))) + ",";
        store.SetValue(key, filter, DateTime.UtcNow);
        return filter;
    }

    /// <summary>Other stat passes and the tags of the cards in <paramref name="filter"/>'s seasons.</summary>
    private List<Step> SeasonSteps(long snapshot, string filter)
    {
        var steps = new List<Step>();
        if (filter.Length == 0) return steps;
        foreach (var g in MarketGroups.All)
        {
            for (var pass = 1; pass < g.Stats.Length; pass++)
            {
                var stats = g.Stats[pass];
                steps.Add(new($"season|{filter}|{g.Key}|list{pass}", ct => ListRange(snapshot, g, new ListQuery(g.Positions, MarketGroups.OvrMinOf(g.Key), MarketGroups.OvrMax, stats) { Seasons = filter }, ct)));
            }
            steps.AddRange(TagSteps(snapshot, g, filter));
        }
        return steps;
    }

    private List<Step> Plan(long snapshot, RefreshKind kind, long? previous)
    {
        var steps = new List<Step>();
        foreach (var g in MarketGroups.All)
        {
            var passes = kind == RefreshKind.Full ? g.Stats.Length : 1;
            for (var pass = 0; pass < passes; pass++)
            {
                var stats = g.Stats[pass];
                for (var ovr = MarketGroups.OvrMinOf(g.Key); ovr <= MarketGroups.OvrMax; ovr++)
                {
                    var o = ovr;
                    steps.Add(new($"list|{g.Key}|{pass}|{o}", ct => ListByOvr(snapshot, g, stats, o, ct)));
                }
            }
        }
        if (kind == RefreshKind.Full)
        {
            foreach (var g in MarketGroups.All) steps.AddRange(TagSteps(snapshot, g, seasons: ""));
        }
        else if (previous is { } prev)
        {
            steps.Add(new("carry", _ => { store.CarryOver(prev, snapshot); return Task.CompletedTask; }));
        }
        return steps;
    }

    private IEnumerable<Step> TagSteps(long snapshot, MarketGroup g, string seasons)
    {
        var prefix = seasons.Length == 0 ? "tag" : $"season|{seasons}|tag";
        var baseQuery = new ListQuery(g.Positions, MarketGroups.OvrMinOf(g.Key), MarketGroups.OvrMax, g.Stats[0]) { Seasons = seasons };
        foreach (var t in g.Traits)
            yield return new($"{prefix}|{g.Key}|trait:{t}", ct => Tag(snapshot, g, $"trait:{t}", baseQuery with { Trait = t }, ct));
        foreach (var s in MarketGroups.SkillTags)
            yield return new($"{prefix}|{g.Key}|skill:{s}", ct => Tag(snapshot, g, $"skill:{s}", baseQuery with { SkillMove = s }, ct));
        if (MarketGroups.BodyGroups.Contains(g.Key))
        {
            foreach (var (name, code) in MarketGroups.Bodies)
                yield return new($"{prefix}|{g.Key}|body:{name}", ct => Tag(snapshot, g, $"body:{name}", baseQuery with { Body = code }, ct));
        }
    }

    private async Task ListByOvr(long snapshot, MarketGroup g, string[] stats, int ovr, CancellationToken ct)
    {
        var query = new ListQuery(g.Positions, ovr, ovr, stats);
        var rows = await source.QueryAsync(query, ct);
        if (rows.Count >= ListQuery.MaxRows)
        {
            var all = new List<ListRow>();
            foreach (var (lo, hi) in MarketGroups.SalaryBands)
                all.AddRange(await source.QueryAsync(query with { PayMin = lo, PayMax = hi }, ct));
            rows = all;
        }
        store.SaveRows(snapshot, g.Key, rows);
    }

    private async Task ListRange(long snapshot, MarketGroup g, ListQuery query, CancellationToken ct) =>
        store.SaveRows(snapshot, g.Key, await SplitWhileFull(query, ct));

    private async Task Tag(long snapshot, MarketGroup g, string tag, ListQuery query, CancellationToken ct) =>
        store.SaveTag(snapshot, g.Key, (await SplitWhileFull(query, ct)).Select(r => r.SpId), tag);

    /// <summary>Halves the OVR range while a slice comes back full.</summary>
    private async Task<List<ListRow>> SplitWhileFull(ListQuery query, CancellationToken ct)
    {
        var rows = await source.QueryAsync(query, ct);
        if (rows.Count < ListQuery.MaxRows || query.OvrMin == query.OvrMax) return [.. rows];
        var mid = (query.OvrMin + query.OvrMax) / 2;
        var result = await SplitWhileFull(query with { OvrMax = mid }, ct);
        result.AddRange(await SplitWhileFull(query with { OvrMin = mid + 1 }, ct));
        return result;
    }
}
