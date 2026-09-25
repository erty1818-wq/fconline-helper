namespace FcHelper.Market;

/// <summary>A card top rankers use that also trades below what similar cards cost: proven and underpriced.</summary>
public sealed record HiddenPick(MarketCard Card, string Position, int Grade, int Ovr, long Price, long Expected, int Users, double Share)
{
    public double Discount => Price / (double)Expected - 1;
}

/// <summary>One grade of a card, with the cheapest other card (and its grade) that reaches the same OVR at the position.</summary>
public sealed record GradeStep(int Grade, int Ovr, long Price, long? CostPerOvrFromPrevious, MarketCard? Alternative, int? AlternativeGrade, long? AlternativePrice)
{
    /// <summary>How much more this card costs than the cheapest equal-OVR alternative: 0.5 = 50% dearer.</summary>
    public double? PremiumOverAlternative => AlternativePrice is > 0 ? Price / (double)AlternativePrice - 1 : null;
}

/// <summary>
/// Buying a card at a higher grade versus other cards that reach the same OVR. <see cref="CompetitiveUpTo"/> is the
/// highest grade at which this card costs at most 20% more than the cheapest equal-OVR alternative (null: never —
/// a premium card whose extra value is not OVR, e.g. traits or team colour).
/// </summary>
public sealed record GradeAdvice(MarketCard Card, string Position, IReadOnlyList<GradeStep> Steps, int? CompetitiveUpTo,
    int? FromGrade, int? ToGrade, long? UpgradeCost, MarketCard? Alternative, int? AlternativeGrade, long? AlternativePrice);

public sealed record SalaryValue(MarketCard Card, string Position, int Grade, int Ovr, double EffectiveOvr, int Pay, long Price)
{
    /// <summary>Effective OVR per salary point, the currency that runs out first in ranked squads.</summary>
    public double OvrPerPay => EffectiveOvr / Math.Max(Pay, 1);
}

public sealed record PriceMove(MarketCard Card, int Grade, long Before, long Now, DateOnly Since, double Discount)
{
    public double Change => Before > 0 ? Now / (double)Before - 1 : 0;
}

/// <summary>A card the user owns, from their latest official match.</summary>
public sealed record OwnedCard(long SpId, int Grade, string Position);

public sealed record Upgrade(SquadSlot Out, MarketCard In, int Grade, int Ovr, double EffectiveGain, long BuyPrice, long SaleValue)
{
    public long NetCost => BuyPrice - SaleValue;
}

public sealed record UpgradePlan(IReadOnlyList<Upgrade> Moves, double TotalGain, long NetCost);

/// <summary>What the next opponent is weak to or strong at, turned into what to look for in a card.</summary>
public enum TacticalNeed
{
    /// <summary>Opponent concedes headers: a strong, jumping forward, cross-minded traits.</summary>
    AerialForward,
    /// <summary>Opponent concedes from distance: shot power and long shots in midfield.</summary>
    LongShots,
    /// <summary>Opponent concedes to through balls and runs in behind: pace up front.</summary>
    PaceInBehind,
    /// <summary>Opponent concedes finesse shots from the half-spaces: agile, dribbling wide forwards.</summary>
    FinesseWide,
    /// <summary>Opponent scores headers: aerial centre-backs.</summary>
    AerialDefence,
    /// <summary>Opponent scores through pace in behind: quick centre-backs and full-backs.</summary>
    PaceDefence,
    /// <summary>Opponent scores from close range in the box: markers and tacklers at centre-back.</summary>
    BoxDefence,
    /// <summary>Opponent scores low or long shots: a keeper with reflexes and diving.</summary>
    ShotStopper,
}

public sealed record TailoredPick(TacticalNeed Need, string Position, MarketCard Card, int Grade, int Ovr, double Fit, long Price, string Reason);

public sealed record FormationAdvice(string Opponent, IReadOnlyList<FormationMatchup> Best, IReadOnlyList<FormationMatchup> Worst);

public static class Advisors
{
    // ── hidden ranker picks ────────────────────────────────────────────────

    public static IReadOnlyList<HiddenPick> HiddenRankerPicks(IEnumerable<RankerPick> picks, IReadOnlyDictionary<long, MarketCard> cards,
        Func<MarketCard, PriceModel?> modelOf, CardFilter? filter = null, int minUsers = 10, string? position = null)
    {
        filter ??= new CardFilter { MinRatings = 0 };
        var result = new List<HiddenPick>();
        foreach (var p in picks.Where(p => p.Users >= minUsers && (position is null || Formations.Normalize(p.Position) == Formations.Normalize(position))))
        {
            if (!cards.TryGetValue(p.SpId, out var card) || modelOf(card) is not { } model) continue;
            if (!filter.Matches(card, p.Grade, Formations.Normalize(p.Position))) continue;
            var price = card.PriceAt(p.Grade);
            // Compare at the grade rankers use: the model was fitted at one grade, so scale by the card's own grade curve.
            var expected = (long)(model.Predict(card) * price / Math.Max(card.PriceAt(model.Grade), 1));
            result.Add(new HiddenPick(card, p.Position, p.Grade, card.OvrAt(Formations.Normalize(p.Position), p.Grade) ?? card.OvrAt(p.Grade),
                price, expected, p.Users, p.Share));
        }
        return result.Where(h => h.Discount < 0).OrderBy(h => h.Discount * Math.Log(1 + h.Users)).ToList();
    }

    // ── enhancement ────────────────────────────────────────────────────────

    /// <summary>
    /// Price and OVR per grade, the grade where one extra OVR point is cheapest to get by buying higher, and — for a
    /// planned move from one grade to another — the cheapest other card already at that OVR, which is often cheaper.
    /// </summary>
    /// <summary>Alternatives only at grades that trade reliably: very high grades of cheap cards show list prices nobody pays.</summary>
    public const int MaxAlternativeGrade = 10;

    public static GradeAdvice Grade(MarketCard card, string position, IEnumerable<MarketCard> pool, int? from = null, int? to = null)
    {
        // Alternatives must have a real market: a +1 price at the floor means nobody wants the card, and its high-grade
        // prices are then not prices anyone would get a good card for.
        var others = pool.Where(c => c.IsTraded && c.PlayerId != card.PlayerId && c.RatingCount >= 10 && c.PriceAt(1) > Grades.FloorPrice * 10).ToList();
        var steps = new List<GradeStep>();
        GradeStep? prev = null;
        int? competitive = null;
        foreach (var g in Enumerable.Range(1, 13))
        {
            var price = card.PriceAt(g);
            if (price <= 0) continue;
            var ovr = card.OvrAt(position, g) ?? card.OvrAt(g);
            long? perOvr = prev is null || ovr <= prev.Ovr ? null : Math.Max(0, price - prev.Price) / (ovr - prev.Ovr);
            var alternative = g <= MaxAlternativeGrade ? CheapestAt(others, position, ovr) : null;
            steps.Add(prev = new GradeStep(g, ovr, price, perOvr, alternative?.Card, alternative?.Grade, alternative?.Price));
            if (alternative is { } a && price <= a.Price * 1.2) competitive = g;
        }

        MarketCard? alt = null;
        int? altGrade = null;
        long? altPrice = null, upgradeCost = null;
        if (from is { } f && to is { } t && t > f)
        {
            upgradeCost = card.PriceAt(t) - card.PriceAt(f);
            if (CheapestAt(others, position, card.OvrAt(position, t) ?? card.OvrAt(t)) is { } a) (alt, altGrade, altPrice) = (a.Card, a.Grade, a.Price);
        }
        return new GradeAdvice(card, position, steps, competitive, from, to, upgradeCost, alt, altGrade, altPrice);
    }

    /// <summary>The cheapest card (at its lowest sufficient grade) reaching an OVR at a position, among reliably traded grades.</summary>
    private static (MarketCard Card, int Grade, long Price)? CheapestAt(IEnumerable<MarketCard> pool, string position, int ovr)
    {
        (MarketCard, int, long)? best = null;
        foreach (var c in pool)
        {
            long previous = 0;
            for (var g = 1; g <= MaxAlternativeGrade; g++)
            {
                var p = c.PriceAt(g);
                if (p < previous) break; // prices falling with grade: not a real market at these grades
                previous = p;
                if (c.OvrAt(position, g) is not { } o || o < ovr || p <= Grades.FloorPrice) continue;
                if (best is null || p < best.Value.Item3) best = (c, g, p);
                break;
            }
        }
        return best;
    }

    // ── salary ─────────────────────────────────────────────────────────────

    public static IReadOnlyList<SalaryValue> SalaryEfficiency(IEnumerable<MarketCard> pool, string position, int grade,
        Func<MarketCard, PriceModel?> modelOf, long minPrice = 0, long maxPrice = long.MaxValue, int minOvr = 0) =>
        pool.Where(c => c.IsTraded && c.OvrAt(position, grade) is { } o && o >= minOvr)
            .Where(c => c.PriceAt(grade) is var p && p > Grades.FloorPrice && p >= minPrice && p <= maxPrice)
            .Select(c =>
            {
                var ovr = c.OvrAt(position, grade)!.Value;
                return new SalaryValue(c, position, grade, ovr, ovr + (modelOf(c)?.PremiumInOvr(c) ?? 0), c.Pay, c.PriceAt(grade));
            })
            .OrderByDescending(s => s.OvrPerPay).ThenBy(s => s.Price).ToList();

    // ── price moves ────────────────────────────────────────────────────────

    public static IReadOnlyList<PriceMove> PriceMoves(IReadOnlyDictionary<long, MarketCard> now, Dictionary<(string Grp, long SpId), Dictionary<int, long>> before,
        DateOnly since, int grade, Func<MarketCard, PriceModel?> modelOf, long minPrice = 0) =>
        before.Where(kv => now.ContainsKey(kv.Key.SpId) && kv.Value.ContainsKey(grade))
            .Select(kv =>
            {
                var card = now[kv.Key.SpId];
                var model = modelOf(card);
                var discount = model is null ? 0 : card.PriceAt(model.Grade) / model.Predict(card) - 1;
                return new PriceMove(card, grade, kv.Value[grade], card.PriceAt(grade), since, discount);
            })
            .Where(m => m.Now > Grades.FloorPrice && m.Now >= minPrice && m.Before > Grades.FloorPrice)
            .OrderBy(m => m.Change).ToList();

    /// <summary>Worth a tray notification after a refresh: a clear drop on a card that is already cheap for its spec.</summary>
    public static IReadOnlyList<PriceMove> Alerts(IEnumerable<PriceMove> moves, double drop = -0.15, double discount = -0.25) =>
        moves.Where(m => m.Change <= drop && m.Discount <= discount).Take(5).ToList();

    // ── my squad ───────────────────────────────────────────────────────────

    /// <summary>
    /// The one or two swaps that raise effective OVR the most for a net budget: buy price minus what the replaced card
    /// sells for (after the market fee). Pairs must use different slots and footballers.
    /// </summary>
    /// <param name="current">The squad with its team colour bonuses (<see cref="WithTeamColors"/>).</param>
    /// <param name="teamColors">Colours to keep: a swap may not drop one to a lower level. A 소속 bonus stays with the
    /// slot whoever comes in; a 특성 bonus only when the new card is a member too.</param>
    public static IReadOnlyList<UpgradePlan> Upgrades(IReadOnlyList<SquadSlot> current, IEnumerable<MarketCard> pool, Func<MarketCard, PriceModel?> modelOf,
        long budget, IReadOnlyList<int> grades, SaleFee? fee = null, int maxMoves = 2, int top = 5, IReadOnlyList<TeamColorTarget>? teamColors = null,
        IReadOnlySet<(long SpId, int Grade)>? excluded = null)
    {
        fee ??= SaleFee.Standard;
        teamColors ??= [];
        var counts = teamColors.Select(t => current.Count(s => t.Members.Contains(s.Card.SpId))).ToArray();
        var owned = current.Select(s => s.Card.PlayerId).ToHashSet();
        var poolList = pool.Where(c => c.IsTraded && !owned.Contains(c.PlayerId)).ToList();
        var options = new List<Upgrade>();
        foreach (var slot in current)
        {
            var sale = fee.NetOf(slot.Price);
            var best = new List<Upgrade>();
            foreach (var c in poolList)
            {
                if (!KeepsLevels(teamColors, counts, [(slot.Card.SpId, c.SpId)])) continue;
                var colorBonus = TeamColorBonusAt(slot.Position, c.SpId, teamColors, counts);
                foreach (var g in grades)
                {
                    var ovr = c.OvrAt(slot.Position, g);
                    var price = c.PriceAt(g);
                    if (ovr is null || price <= Grades.FloorPrice || price - sale > budget || excluded?.Contains((c.SpId, g)) == true) continue;
                    var gain = ovr.Value + colorBonus + (modelOf(c)?.PremiumInOvr(c) ?? 0) - slot.EffectiveOvr;
                    if (gain > 0.5) best.Add(new Upgrade(slot, c, g, ovr.Value, gain, price, sale));
                }
            }
            options.AddRange(best.OrderByDescending(u => u.EffectiveGain / Math.Max(u.NetCost, 1) * 1e8 + u.EffectiveGain).Take(15));
        }
        var plans = options.Where(o => o.NetCost <= budget).Select(o => new UpgradePlan([o], o.EffectiveGain, o.NetCost)).ToList();
        if (maxMoves >= 2)
        {
            for (var i = 0; i < options.Count; i++)
            for (var j = i + 1; j < options.Count; j++)
            {
                var (a, b) = (options[i], options[j]);
                if (a.Out.Index == b.Out.Index || a.In.PlayerId == b.In.PlayerId || a.NetCost + b.NetCost > budget) continue;
                if (!KeepsLevels(teamColors, counts, [(a.Out.Card.SpId, a.In.SpId), (b.Out.Card.SpId, b.In.SpId)])) continue;
                plans.Add(new UpgradePlan([a, b], a.EffectiveGain + b.EffectiveGain, a.NetCost + b.NetCost));
            }
        }
        return plans.OrderByDescending(p => p.TotalGain).ThenBy(p => p.NetCost).Take(top).ToList();
    }

    /// <summary>The squad's slots with the OVR their team colours add (members counted over the whole squad).</summary>
    public static IReadOnlyList<SquadSlot> WithTeamColors(IReadOnlyList<SquadSlot> squad, IReadOnlyList<TeamColorTarget> teamColors)
    {
        var counts = teamColors.Select(t => squad.Count(s => t.Members.Contains(s.Card.SpId))).ToArray();
        return squad.Select(s => s with { TeamColorBonus = TeamColorBonusAt(s.Position, s.Card.SpId, teamColors, counts) }).ToList();
    }

    /// <summary>OVR the colours add to a card at a position, at the levels the member counts reach.</summary>
    public static double TeamColorBonusAt(string position, long spId, IReadOnlyList<TeamColorTarget> teamColors, IReadOnlyList<int> counts)
    {
        var total = 0.0;
        for (var i = 0; i < teamColors.Count; i++)
            if (teamColors[i].Color.LevelFor(counts[i]) is { } level && (teamColors[i].Color.AppliesToSquad || teamColors[i].Members.Contains(spId)))
                total += level.OvrGain(position);
        return total;
    }

    /// <summary>Whether the swaps (card out, card in) leave every colour at its level.</summary>
    private static bool KeepsLevels(IReadOnlyList<TeamColorTarget> teamColors, int[] counts, (long Out, long In)[] swaps)
    {
        for (var i = 0; i < teamColors.Count; i++)
        {
            var members = teamColors[i].Members;
            var after = counts[i] + swaps.Sum(s => (members.Contains(s.In) ? 1 : 0) - (members.Contains(s.Out) ? 1 : 0));
            if (teamColors[i].Color.LevelIndexFor(after) != teamColors[i].Color.LevelIndexFor(counts[i]) && after < counts[i]) return false;
        }
        return true;
    }

    // ── opponent-tailored picks ────────────────────────────────────────────

    private sealed record NeedSpec(string[] Positions, (string Stat, double Weight)[] Stats, string[] Tags, string Reason);

    private static readonly IReadOnlyDictionary<TacticalNeed, NeedSpec> Needs = new Dictionary<TacticalNeed, NeedSpec>
    {
        [TacticalNeed.AerialForward] = new(["ST", "CF"], [("strength", 1)], ["trait:크로스 포쳐", "trait:타이탄", "body:heavy"], "헤더 실점이 많은 상대: 몸싸움 좋은 타깃형 공격수"),
        [TacticalNeed.LongShots] = new(["CAM", "CM"], [("longshots", 1), ("shotpower", 1)], ["trait:레이저 슈터"], "중거리 실점이 많은 상대: 중거리 슛 좋은 미드필더"),
        [TacticalNeed.PaceInBehind] = new(["ST", "CF", "LW", "RW"], [("sprintspeed", 1), ("acceleration", 1)], ["trait:라인 브레이커", "trait:스피드스터"], "뒷공간이 약한 상대: 빠른 침투형 공격수"),
        [TacticalNeed.FinesseWide] = new(["LW", "RW", "CAM"], [("dribbling", 1), ("agility", 1)], ["trait:트릭스터", "skill:5", "skill:6"], "측면 감아차기 실점이 많은 상대: 드리블 좋은 측면 공격수"),
        [TacticalNeed.AerialDefence] = new(["CB"], [("headingaccuracy", 1), ("jumping", 1), ("strength", 1)], ["trait:블로커", "trait:타이탄"], "헤더로 넣는 상대: 공중볼 강한 센터백"),
        [TacticalNeed.PaceDefence] = new(["CB", "LB", "RB"], [("sprintspeed", 1), ("acceleration", 1)], ["trait:체이서", "trait:스피드스터"], "침투로 넣는 상대: 빠른 수비수"),
        [TacticalNeed.BoxDefence] = new(["CB"], [("marking", 1), ("standingtackle", 1), ("strength", 1)], ["trait:블로커", "trait:와일드 태클러"], "박스 안 근거리 득점이 많은 상대: 대인 수비·태클 좋은 센터백"),
        [TacticalNeed.ShotStopper] = new(["GK"], [("gkreflexes", 1), ("gkdiving", 1)], ["trait:GK 빠른 반응"], "낮은 슛·중거리로 넣는 상대: 반응속도·다이빙 좋은 골키퍼"),
    };

    /// <summary>Cards in budget ranked by OVR plus how well their stats (relative to OVR) and tags fit the need.</summary>
    public static IReadOnlyList<TailoredPick> Tailored(IEnumerable<TacticalNeed> needs, IEnumerable<MarketCard> pool, int grade,
        long maxPrice = long.MaxValue, int perNeed = 5)
    {
        var list = pool.Where(c => c.IsTraded && c.PriceAt(grade) is var p && p > Grades.FloorPrice && p <= maxPrice).ToList();
        var result = new List<TailoredPick>();
        foreach (var need in needs.Distinct())
        {
            var spec = Needs[need];
            var picks = new List<TailoredPick>();
            foreach (var c in list)
            {
                var pos = spec.Positions.FirstOrDefault(p => c.OvrAt(p, grade) is not null);
                if (pos is null) continue;
                var ovr = c.OvrAt(pos, grade)!.Value;
                var statFit = spec.Stats.Where(s => c.Stats.ContainsKey(s.Stat)).Sum(s => s.Weight * (c.Stats[s.Stat] - c.Ovr1)) / Math.Max(1, spec.Stats.Length);
                var tagFit = spec.Tags.Count(c.Tags.Contains) * 2.0;
                picks.Add(new TailoredPick(need, pos, c, grade, ovr, ovr + statFit * 0.5 + tagFit, c.PriceAt(grade), spec.Reason));
            }
            result.AddRange(picks.OrderByDescending(p => p.Fit).Take(perNeed));
        }
        return result;
    }

    // ── formations ─────────────────────────────────────────────────────────

    /// <summary>Formations that did best (and worst) against the opponent's formation among rankers, with enough games.</summary>
    public static FormationAdvice Formation(string opponent, IEnumerable<FormationMatchup> matchups, int minGames = 30, int top = 3)
    {
        var vs = matchups.Where(m => m.Opponent == opponent && m.Games >= minGames && m.Formation != opponent).ToList();
        return new FormationAdvice(opponent, vs.OrderByDescending(m => m.WinRate).Take(top).ToList(), vs.OrderBy(m => m.WinRate).Take(top).ToList());
    }

    /// <summary>
    /// The formation a squad most likely played from its starters' positions (spposition codes), by matching the count
    /// of each rated position against the known formations [추정: the game does not report the formation].
    /// </summary>
    public static string? EstimateFormation(IEnumerable<int> starterPositions)
    {
        var names = new Dictionary<int, string>
        {
            [0] = "GK", [1] = "CB", [2] = "RWB", [3] = "RB", [4] = "CB", [5] = "CB", [6] = "CB", [7] = "LB", [8] = "LWB", [9] = "CDM", [10] = "CDM",
            [11] = "CDM", [12] = "RM", [13] = "CM", [14] = "CM", [15] = "CM", [16] = "LM", [17] = "CAM", [18] = "CAM", [19] = "CAM", [20] = "CF",
            [21] = "CF", [22] = "CF", [23] = "RW", [24] = "ST", [25] = "ST", [26] = "ST", [27] = "LW",
        };
        var have = starterPositions.Where(names.ContainsKey).Select(p => names[p]).GroupBy(p => p).ToDictionary(g => g.Key, g => g.Count());
        if (have.Values.Sum() < 10) return null;
        return Formations.All
            .Select(f => (f.Name, Miss: f.Slots.GroupBy(s => s).Sum(g => Math.Abs(g.Count() - have.GetValueOrDefault(g.Key)))
                + have.Where(kv => !f.Slots.Contains(kv.Key)).Sum(kv => kv.Value)))
            .OrderBy(t => t.Miss).First().Name;
    }
}
