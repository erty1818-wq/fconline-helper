using FcHelper.Core.Models;
using FcHelper.NexonApi;

namespace FcHelper.Market;

/// <summary>
/// Everything the squad tools need behind one object, for the app and the CLI: the merged card pool of the current
/// market snapshot, price models, the daily ranker chart (cached, refreshed at most every 12 hours), team colours and
/// rankers' match stats. UI code calls these methods and binds the returned records.
/// </summary>
public sealed class SquadService(
    MarketService market, MarketStore store, IRankerChartSource charts, TeamColorCache teamColors,
    IRankerStatsSource? rankerStats = null, TimeProvider? time = null, SalaryCapCache? salaryCap = null)
{
    public const int OfficialMatch = 50;
    public static readonly TimeSpan ChartTtl = TimeSpan.FromHours(12);
    /// <summary>The model grade used for premiums and expected prices when a request spans several grades.</summary>
    public const int ModelGrade = 8;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _chartGate = new(1, 1);
    private readonly Dictionary<string, RankerStat> _statsCache = [];
    private (long Snapshot, IReadOnlyList<MarketCard> Cards, IReadOnlyDictionary<long, MarketCard> BySpId)? _pool;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public MarketService Market => market;

    // ── data ───────────────────────────────────────────────────────────────

    /// <summary>
    /// All cards of the current snapshot, one per spid: a card collected under several position groups keeps the union
    /// of its positions, stats and tags.
    /// </summary>
    public IReadOnlyList<MarketCard> Pool() => EnsurePool().Cards;

    public MarketCard? Card(long spId) => EnsurePool().BySpId.GetValueOrDefault(spId);

    private (long Snapshot, IReadOnlyList<MarketCard> Cards, IReadOnlyDictionary<long, MarketCard> BySpId) EnsurePool()
    {
        var snap = store.LatestFinished() ?? throw new InvalidOperationException("시세 데이터가 아직 없습니다.");
        if (_pool is { } p && p.Snapshot == snap.Id) return p;
        var merged = new Dictionary<long, MarketCard>();
        foreach (var c in store.LoadCards(snap.Id))
        {
            if (!merged.TryGetValue(c.SpId, out var m)) { merged[c.SpId] = c; continue; }
            merged[c.SpId] = m with
            {
                Positions = m.Positions.Concat(c.Positions).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Max(kv => kv.Value)),
                Stats = m.Stats.Concat(c.Stats).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value),
                Tags = m.Tags.Union(c.Tags).ToHashSet(),
            };
        }
        var cards = merged.Values.ToList();
        _pool = (snap.Id, cards, merged);
        return _pool.Value;
    }

    /// <summary>The price model of the card's own position group.</summary>
    public PriceModel? ModelOf(MarketCard card, int grade = ModelGrade) => market.Model(card.Group, grade);

    /// <summary>The newest daily chart for a ranker range; fetched when missing or older than <see cref="ChartTtl"/>.</summary>
    public async Task<RankerChartData?> ChartAsync(int rankFrom = 1, int rankTo = 10000, bool allowFetch = true, CancellationToken ct = default)
    {
        await _chartGate.WaitAsync(ct);
        try
        {
            var cached = store.LatestChart(rankFrom, rankTo);
            // Charts saved before team colour ids were read list only the top 10: fetch again once.
            var complete = cached is { } k && k.Chart.TeamColors.Any(t => t.Id > 0);
            if (cached is { } c && (complete && Now - c.FetchedAt < ChartTtl || !allowFetch)) return c.Chart;
            if (!allowFetch) return null;
            try
            {
                var chart = await charts.FetchAsync(rankFrom, rankTo, ct);
                if (chart.Picks.Count > 0) store.SaveChart(chart, Now);
                return chart.Picks.Count > 0 ? chart : cached?.Chart;
            }
            catch (HttpRequestException)
            {
                return cached?.Chart;
            }
        }
        finally
        {
            _chartGate.Release();
        }
    }

    // ── squads ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SquadPlan>> BuildAsync(SquadRequest request, int rankFrom = 1, int rankTo = 10000, CancellationToken ct = default)
    {
        var chart = await ChartAsync(rankFrom, rankTo, ct: ct);
        var rankers = chart is null ? null : new RankerUsage(chart.Picks);
        var pool = Pool();
        return await Task.Run(() => new SquadBuilder(pool, c => ModelOf(c), rankers).Build(request), ct);
    }

    /// <summary>The best plan of every mode for the same request, for side-by-side comparison.</summary>
    public async Task<IReadOnlyList<SquadPlan>> CompareModesAsync(SquadRequest request, int rankFrom = 1, int rankTo = 10000, CancellationToken ct = default)
    {
        var plans = new List<SquadPlan>();
        foreach (var mode in Enum.GetValues<SquadMode>())
        {
            try
            {
                plans.AddRange((await BuildAsync(request with { Mode = mode, Plans = 1 }, rankFrom, rankTo, ct)).Take(1));
            }
            catch (InvalidOperationException) when (mode == SquadMode.RankerPicks)
            {
                // No chart yet: the other modes still stand.
            }
        }
        return plans;
    }

    // ── analyses ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<HiddenPick>> HiddenRankerPicksAsync(string? position = null, CardFilter? filter = null,
        int minUsers = 10, int rankFrom = 1, int rankTo = 10000, CancellationToken ct = default)
    {
        var chart = await ChartAsync(rankFrom, rankTo, ct: ct);
        return chart is null ? [] : Advisors.HiddenRankerPicks(chart.Picks, EnsurePool().BySpId, c => ModelOf(c), filter, minUsers, position);
    }

    public GradeAdvice? Grade(long spId, string position, int? from = null, int? to = null) =>
        Card(spId) is { } card ? Advisors.Grade(card, Formations.Normalize(position), Pool(), from, to) : null;

    public IReadOnlyList<SalaryValue> SalaryEfficiency(string position, int grade, long minPrice = 0, long maxPrice = long.MaxValue, int minOvr = 0) =>
        Advisors.SalaryEfficiency(Pool(), Formations.Normalize(position), grade, c => ModelOf(c), minPrice, maxPrice, minOvr);

    /// <summary>Price change at a grade against the price history <paramref name="days"/> ago (or the oldest day kept).</summary>
    public IReadOnlyList<PriceMove> PriceMoves(int grade = 8, int days = 7, long minPrice = 0)
    {
        var history = store.PriceHistoryDays();
        var today = DateOnly.FromDateTime(Now);
        var since = history.Where(d => d <= today.AddDays(-days)).DefaultIfEmpty(history.FirstOrDefault()).Max();
        if (history.Count == 0 || since >= today) return [];
        return Advisors.PriceMoves(EnsurePool().BySpId, store.PriceHistory(since), since, grade, c => ModelOf(c), minPrice);
    }

    public IReadOnlyList<PriceMove> Alerts() => Advisors.Alerts(PriceMoves(ModelGrade, 1, 10_000_000));

    /// <summary>The salary cap known now (310 until the squad maker has been read).</summary>
    public int SalaryCap => salaryCap?.Current ?? SalaryCapSource.Fallback;

    /// <summary>The salary cap, re-read from the official squad maker weekly.</summary>
    public async Task<int> SalaryCapAsync(CancellationToken ct = default) => salaryCap is null ? SalaryCapSource.Fallback : await salaryCap.GetAsync(ct);

    /// <summary>The user's current eleven with the bonuses of the given team colours.</summary>
    public IReadOnlyList<SquadSlot> CurrentSquad(IEnumerable<OwnedCard> owned, IReadOnlyList<TeamColorTarget> teamColors) =>
        Advisors.WithTeamColors(CurrentSquad(owned), teamColors);

    /// <summary>The user's current eleven as squad slots, from the cards (and grades) of their latest official match.</summary>
    public IReadOnlyList<SquadSlot> CurrentSquad(IEnumerable<OwnedCard> owned)
    {
        var slots = new List<SquadSlot>();
        foreach (var (o, i) in owned.Select((o, i) => (o, i)))
        {
            if (Card(o.SpId) is not { } card) continue;
            var model = ModelOf(card);
            var pos = Formations.Normalize(o.Position);
            slots.Add(new SquadSlot(i, pos, card, o.Grade, card.OvrAt(pos, o.Grade) ?? card.OvrAt(o.Grade), model?.PremiumInOvr(card) ?? 0, 0,
                card.PriceAt(o.Grade), SquadBuilder.ExpectedAt(model, card, o.Grade), card.Pay, 0, 0, Locked: true, Owned: true));
        }
        return slots;
    }

    /// <param name="teamColorIds">Team colours to keep at their level (see <see cref="Advisors.Upgrades"/>).</param>
    public async Task<IReadOnlyList<UpgradePlan>> UpgradesAsync(IEnumerable<OwnedCard> owned, long budget, IReadOnlyList<int> grades, SaleFee? fee = null,
        int maxMoves = 2, IReadOnlyList<int>? teamColorIds = null, CancellationToken ct = default)
    {
        var targets = await TargetsAsync(teamColorIds ?? [], ct);
        return Advisors.Upgrades(CurrentSquad(owned, targets), Pool(), c => ModelOf(c), budget, grades, fee, maxMoves, teamColors: targets);
    }

    /// <summary>
    /// Team colours with their members, for a squad request or the upgrades. The game runs one colour per category
    /// (소속 / 특성 / 강화) at a time, so a second one of the same category is dropped (the first given wins).
    /// </summary>
    public async Task<IReadOnlyList<TeamColorTarget>> TargetsAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        var targets = new List<TeamColorTarget>();
        foreach (var id in ids.Where(i => i > 0).Distinct())
            if (await TeamColorAsync(id, ct) is { } tc && targets.All(t => t.Color.Category != tc.Color.Category))
                targets.Add(new TeamColorTarget(tc.Color, tc.Members));
        return targets;
    }

    /// <summary>The detected colours the game would run: the strongest of each category.</summary>
    public static IReadOnlyList<int> ActiveTeamColors(IEnumerable<(TeamColor Color, int Owned, TeamColorLevel Level)> detected) =>
        detected.GroupBy(d => d.Color.Category).Select(g => g.First().Color.Id).ToList();

    /// <summary>
    /// Team colours the squad is built on: the colours shared by at least half of a few sampled cards (read from their
    /// detail pages), then counted exactly over the whole squad with the members list. Only those that reach a level.
    /// Costs a handful of requests the first time; members are cached for a week.
    /// </summary>
    public async Task<IReadOnlyList<(TeamColor Color, int Owned, TeamColorLevel Level)>> DetectTeamColorsAsync(IEnumerable<OwnedCard> owned, int samples = 4,
        CancellationToken ct = default)
    {
        var ownedList = owned.ToList();
        var counts = new Dictionary<int, int>();
        foreach (var o in ownedList.Take(samples))
            foreach (var id in await teamColors.CardTeamColorsAsync(o.SpId, ct))
                counts[id] = counts.GetValueOrDefault(id) + 1;
        var ids = ownedList.Select(o => o.SpId).ToHashSet();
        var result = new List<(TeamColor, int, TeamColorLevel)>();
        foreach (var id in counts.Where(kv => kv.Value * 2 >= Math.Min(samples, ownedList.Count)).Select(kv => kv.Key))
        {
            if (await TeamColorAsync(id, ct) is not { } tc) continue;
            var count = ids.Count(tc.Members.Contains);
            if (tc.Color.LevelFor(count) is { } level) result.Add((tc.Color, count, level));
        }
        // Strongest first within a category: all-stats bonus, then the extra single-stat bonuses.
        return result.OrderBy(r => r.Item1.Category).ThenByDescending(r => r.Item3.AllStats)
            .ThenByDescending(r => TeamColorParser.StatBonuses(r.Item3.Effects).Values.Sum()).ThenByDescending(r => r.Item2).ToList();
    }

    public IReadOnlyList<TailoredPick> Tailored(IEnumerable<TacticalNeed> needs, int grade, long maxPrice = long.MaxValue, int perNeed = 5) =>
        Advisors.Tailored(needs, Pool(), grade, maxPrice, perNeed);

    public async Task<FormationAdvice?> FormationAdviceAsync(string opponentFormation, int rankFrom = 1, int rankTo = 10000, CancellationToken ct = default) =>
        await ChartAsync(rankFrom, rankTo, ct: ct) is { } chart ? Advisors.Formation(opponentFormation, chart.Matchups) : null;

    // ── team colours ───────────────────────────────────────────────────────

    public Task<IReadOnlyList<TeamColor>> TeamColorsAsync(CancellationToken ct = default) => teamColors.CatalogAsync(ct);

    /// <summary>The team colour with its level ladder and members (fetched once, then weekly).</summary>
    public Task<(TeamColor Color, IReadOnlySet<long> Members)?> TeamColorAsync(int id, CancellationToken ct = default) => teamColors.GetAsync(id, ct);

    /// <summary>Team colours rankers use most (daily chart), matched to catalogue entries by name.</summary>
    public async Task<IReadOnlyList<(TeamColor Color, TeamColorUsage Usage)>> PopularTeamColorsAsync(int top = RankerTeamColors, CancellationToken ct = default)
    {
        var chart = await ChartAsync(ct: ct);
        var catalog = await TeamColorsAsync(ct);
        if (chart is null) return [];
        var affiliation = catalog.Where(t => t.Category == TeamColorCategory.Affiliation).ToList();
        return chart.TeamColors
            .Select(u => (Color: affiliation.FirstOrDefault(t => u.Id > 0 ? t.Id == u.Id : t.Name == u.Name), Usage: u))
            .Where(t => t.Color is not null).Select(t => (t.Color!, t.Usage)).Take(top).ToList();
    }

    /// <summary>How many of the team colours rankers use most count as "주요 랭커 팀컬러".</summary>
    public const int RankerTeamColors = 20;

    /// <summary>
    /// Cards that count for at least one of the 20 team colours rankers use most: a card outside all of them rarely
    /// fits a top squad. The members of each colour are fetched once and kept for a week (about 60 requests the first time).
    /// </summary>
    public async Task<IReadOnlySet<long>> RankerTeamColorMembersAsync(int top = RankerTeamColors, CancellationToken ct = default)
    {
        var members = new HashSet<long>();
        foreach (var (color, _) in await PopularTeamColorsAsync(top, ct))
            if (await TeamColorAsync(color.Id, ct) is { } tc) members.UnionWith(tc.Members);
        if (top == RankerTeamColors) market.SetRankerTeamColorMembers(members);
        return members;
    }

    // ── rankers' match stats ───────────────────────────────────────────────

    /// <summary>
    /// How top rankers did with each card at a position over 20 matches (official ranker-stats; uses the API quota,
    /// so only for the cards a screen shows; cached for the day). Missing = rankers did not use it.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, RankerStat>> RankerStatsAsync(IEnumerable<(long SpId, string Position)> cards, CancellationToken ct = default)
    {
        if (rankerStats is null) return new Dictionary<long, RankerStat>();
        var day = DateOnly.FromDateTime(Now).ToString("yyyyMMdd");
        var wanted = cards.Select(c => (c.SpId, Code: PositionCode(c.Position))).Distinct().ToList();
        var missing = wanted.Where(w => !_statsCache.ContainsKey($"{day}|{w.SpId}|{w.Code}")).ToList();
        if (missing.Count > 0)
        {
            foreach (var s in await rankerStats.GetRankerStatsAsync(OfficialMatch, missing.Select(m => (m.SpId, m.Code)).ToList(), ct))
                _statsCache[$"{day}|{s.SpId}|{s.SpPosition}"] = s;
            foreach (var m in missing) _statsCache.TryAdd($"{day}|{m.SpId}|{m.Code}", new RankerStat { SpId = m.SpId, SpPosition = m.Code });
        }
        return wanted.Select(w => _statsCache[$"{day}|{w.SpId}|{w.Code}"]).Where(s => s.Status.MatchCount > 0)
            .GroupBy(s => s.SpId).ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>spposition code of a rated position, as ranker-stats and the chart use it.</summary>
    public static int PositionCode(string position) => Formations.Normalize(position) switch
    {
        "GK" => 0, "RWB" => 2, "RB" => 3, "CB" => 5, "LB" => 7, "LWB" => 8, "CDM" => 10, "RM" => 12, "CM" => 14, "LM" => 16,
        "CAM" => 18, "CF" => 21, "RW" => 23, "LW" => 27, _ => 25,
    };
}
