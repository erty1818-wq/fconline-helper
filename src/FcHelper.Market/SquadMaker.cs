namespace FcHelper.Market;

public enum CandidateSort { Ovr, PriceLow, Value, Rankers }

/// <summary>A suggested card for a slot and why ("더 강하게", "더 싸게", "랭커 픽", "추천").</summary>
public sealed record SlotSuggestion(string Reason, SquadSlot Slot);

/// <summary>
/// The manual side of the squad maker: any card at any grade in any slot, searched by name and season, with
/// suggestions per slot, bulk grade changes and the team colours the hand-made eleven reaches. Prices and effective
/// OVR are worked out the same way as for the AI squads, so both can be mixed freely.
/// </summary>
public sealed class SquadMaker(SquadService squads, RankerUsage? rankers = null)
{
    /// <summary>A card at a slot and grade, priced and rated as the builder does it.</summary>
    public SquadSlot Slot(int index, string position, MarketCard card, int grade, bool owned = false, bool locked = false)
    {
        var pos = Formations.Normalize(position);
        var model = squads.ModelOf(card);
        return new SquadSlot(index, pos, card, grade, card.OvrAt(pos, grade) ?? card.OvrAt(grade), model?.PremiumInOvr(card) ?? 0, 0,
            card.PriceAt(grade), SquadBuilder.ExpectedAt(model, card, grade), card.Pay, rankers?.Users(pos, card.SpId) ?? 0,
            rankers?.Share(pos, card.SpId) ?? 0, locked, owned);
    }

    /// <summary>The same slot at another grade (e.g. "전체 +11").</summary>
    public SquadSlot WithGrade(SquadSlot s, int grade) => Slot(s.Index, s.Position, s.Card, grade, s.Owned, s.Locked);

    /// <summary>Seasons in the market, newest ids first, for the season filter.</summary>
    public IReadOnlyList<string> Seasons() =>
        squads.Pool().GroupBy(c => c.Season).OrderByDescending(g => g.Max(c => c.SeasonId)).Select(g => g.Key).ToList();

    /// <summary>Cards that can play <paramref name="position"/>, filtered by name and season, best first by <paramref name="sort"/>.</summary>
    /// <param name="members">Only these cards (the chosen 소속 colour's members), or null for any.</param>
    public IReadOnlyList<SquadSlot> Search(int index, string position, int grade, string? name = null, string? season = null,
        CandidateSort sort = CandidateSort.Ovr, IReadOnlySet<int>? usedPlayers = null, int top = 60, IReadOnlySet<long>? members = null)
    {
        var pos = Formations.Normalize(position);
        grade = Math.Min(grade, Grades.MaxTradable); // +12/+13 cannot be bought
        var slots = squads.Pool()
            .Where(c => c.OvrAt(pos, grade) is not null && c.PriceAt(grade) > 0)
            .Where(c => string.IsNullOrWhiteSpace(name) || c.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(c => season is null || c.Season == season)
            .Where(c => usedPlayers is null || !usedPlayers.Contains(c.PlayerId))
            .Where(c => members is null || members.Contains(c.SpId))
            .Select(c => Slot(index, pos, c, grade));
        slots = sort switch
        {
            CandidateSort.PriceLow => slots.Where(s => s.Price > Grades.FloorPrice).OrderBy(s => s.Price),
            CandidateSort.Value => slots.Where(s => s.Price > Grades.FloorPrice).OrderBy(s => s.Discount),
            CandidateSort.Rankers => slots.Where(s => s.RankerUsers > 0).OrderByDescending(s => s.RankerUsers),
            _ => slots.OrderByDescending(s => s.EffectiveOvr).ThenBy(s => s.Price),
        };
        return slots.Take(top).ToList();
    }

    /// <summary>
    /// Alternatives for a slot. With a card: stronger for about the same money, nearly as strong for clearly less, and
    /// what rankers field there. Empty: the best cards around the rankers' usual price for the role (their share of
    /// the budget), and the rankers' picks.
    /// </summary>
    /// <param name="members">The chosen 소속 colour's members: suggestions stay inside it.</param>
    /// <param name="maxPrice">What the slot may cost at most (the budget left plus the current card), or no limit.</param>
    public IReadOnlyList<SlotSuggestion> Suggest(int index, string position, int grade, SquadSlot? current, IReadOnlySet<int> usedPlayers,
        long budget = long.MaxValue, RankerAllocation? allocation = null, int perKind = 3, IReadOnlySet<long>? members = null, long maxPrice = long.MaxValue)
    {
        var pool = Search(index, position, grade, usedPlayers: usedPlayers, top: 400, members: members)
            .Where(s => s.Price > Grades.FloorPrice && s.Price <= maxPrice && s.Card.IsTraded && s.Card.PlayerId != current?.Card.PlayerId).ToList();
        var result = new List<SlotSuggestion>();
        if (current is not null)
        {
            result.AddRange(pool.Where(s => s.Price <= current.Price * 1.3 && s.EffectiveOvr > current.EffectiveOvr + 0.5)
                .OrderByDescending(s => s.EffectiveOvr).Take(perKind).Select(s => new SlotSuggestion("더 강하게 (비슷한 가격)", s)));
            result.AddRange(pool.Where(s => s.Price < current.Price * 0.75 && s.EffectiveOvr >= current.EffectiveOvr - 1.5)
                .OrderBy(s => s.Price).Take(perKind).Select(s => new SlotSuggestion("더 싸게 (비슷한 OVR)", s)));
        }
        else
        {
            var target = allocation?.For(position) is { } share && budget < long.MaxValue ? share.PriceShare * budget : (double?)null;
            var near = target is { } t ? pool.Where(s => s.Price >= t * 0.5 && s.Price <= t * 1.5) : pool;
            result.AddRange(near.OrderByDescending(s => s.EffectiveOvr).Take(perKind + 2)
                .Select(s => new SlotSuggestion(target is null ? "추천" : "랭커 가격대 추천", s)));
        }
        result.AddRange(pool.Where(s => s.RankerUsers > 0 && result.All(r => r.Slot.Card.SpId != s.Card.SpId))
            .OrderByDescending(s => s.RankerUsers).Take(perKind).Select(s => new SlotSuggestion($"랭커 픽 ({s.RankerUsers}명)", s)));
        return result;
    }

    /// <summary>
    /// Moves a hand-made eleven to another formation: each player keeps a slot of the same position if there is one,
    /// else of the same role (LB → RB, CAM → CM …); the rest are left empty.
    /// </summary>
    public IReadOnlyList<SquadSlot?> Remap(IReadOnlyList<SquadSlot?> from, Formation to)
    {
        var result = new SquadSlot?[to.Slots.Length];
        var left = from.Where(s => s is not null).Cast<SquadSlot>().ToList();
        foreach (var pass in new Func<SquadSlot, string, bool>[]
                 {
                     (s, p) => s.Position == p,
                     (s, p) => RankerAllocation.RoleOf(s.Position) == RankerAllocation.RoleOf(p),
                     (s, p) => s.Card.OvrAt(p, s.Grade) is not null,
                 })
        {
            for (var i = 0; i < result.Length; i++)
            {
                if (result[i] is not null) continue;
                var pick = left.FirstOrDefault(s => pass(s, to.Slots[i]));
                if (pick is null) continue;
                left.Remove(pick);
                result[i] = Slot(i, to.Slots[i], pick.Card, pick.Grade, pick.Owned, pick.Locked);
            }
        }
        return result;
    }

    /// <summary>
    /// The team colours a hand-made eleven reaches: the given ones (from the dropdowns) plus the best 소속 colour among
    /// those rankers use most, when it reaches a level; and the eleven with their bonuses applied.
    /// </summary>
    public async Task<(IReadOnlyList<SquadSlot> Slots, IReadOnlyList<AppliedTeamColor> Colors)> ApplyTeamColorsAsync(
        IReadOnlyList<SquadSlot> filled, IReadOnlyList<TeamColorTarget> selected, CancellationToken ct = default)
    {
        var targets = selected.ToList();
        if (targets.All(t => t.Color.Category != TeamColorCategory.Affiliation))
        {
            (TeamColorTarget Target, int Count)? best = null;
            foreach (var (color, _) in await squads.PopularTeamColorsAsync(ct: ct))
            {
                if (await squads.TeamColorAsync(color.Id, ct) is not { } tc) continue;
                var count = filled.Count(s => tc.Members.Contains(s.Card.SpId));
                if (tc.Color.LevelFor(count) is not null && (best is null || count > best.Value.Count)) best = (new TeamColorTarget(tc.Color, tc.Members), count);
            }
            if (best is { } b) targets.Insert(0, b.Target);
        }
        // The 강화 colour follows the grades in the eleven: the best one reached applies.
        if (targets.All(t => t.Color.Category != TeamColorCategory.Enhance)) targets.AddRange(await squads.EnhanceTargetsAsync(ct));
        var slots = Advisors.WithTeamColors(filled, targets);
        var colors = targets.Select(t =>
        {
            var n = filled.Count(s => t.Counts(s.Card.SpId, s.Grade));
            return new AppliedTeamColor(t.Color, n, t.Color.LevelFor(n));
        }).ToList();
        // Show one 강화 colour: the best one reached (or none).
        var enhance = colors.Where(c => c.Color.Category == TeamColorCategory.Enhance && c.Level is not null).MaxBy(c => c.Level!.AllStats);
        colors = colors.Where(c => c.Color.Category != TeamColorCategory.Enhance || ReferenceEquals(c, enhance)).ToList();
        return (slots, colors);
    }
}
