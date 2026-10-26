namespace FcHelper.Market;

public sealed record AffiliationChoice(TeamColorTarget Target, int NearbyUsers, int NearbySquads, int RankerUsers, bool BudgetEvidence)
{
    public string Note => BudgetEvidence
        ? $"비슷한 가격대(현재 시세 환산) 랭커 {NearbySquads}팀 중 {NearbyUsers}팀이 {Target.Color.Name} 소속 11명 사용 · 소속 11/11"
        : $"{Target.Color.Name} 소속 11/11 · 이 예산대 표본 부족, 전체 랭커 인기 소속에서 비교 [추정]";
}

/// <summary>Rank actual affiliation elevens near the budget; never call an overall chart budget-specific evidence.</summary>
public static class SquadAffiliations
{
    public const int MinimumNearbySquads = 3, CompareColors = 4;

    public static IReadOnlyList<AffiliationChoice> Rank(SquadRequest request, IReadOnlyList<TeamColorTarget> targets,
        IReadOnlyList<RankerSquad> squads, IReadOnlyDictionary<long, MarketCard> cards, IReadOnlyList<TeamColorUsage> chart)
    {
        var budget = request.Budget == long.MaxValue ? double.PositiveInfinity : request.Budget
            + request.Locked.Values.Where(c => c.Owned).Sum(c => (double)(cards.GetValueOrDefault(c.SpId)?.PriceAt(c.Grade) ?? 0));
        var nearby = squads.Where(s => s.Starters.Count == 11 && s.Starters.Select(p => p.SpId % 1_000_000).Distinct().Count() == 11)
            .Select(s => (Squad: s, Prices: s.Starters.Select(p => cards.GetValueOrDefault(p.SpId)?.PriceAt(p.Grade) ?? 0).ToList()))
            .Where(s => s.Prices.All(p => p > Grades.FloorPrice))
            .Where(s => double.IsFinite(budget) && s.Prices.Sum(p => (double)p) >= budget * .5 && s.Prices.Sum(p => (double)p) <= budget * 2)
            .Select(s => s.Squad).ToList();
        var eligible = targets.Where(t => t.Color.Category == TeamColorCategory.Affiliation)
            .Where(t => request.Locked.Values.All(c => t.Members.Contains(c.SpId)))
            .Where(t => cards.Values.Where(c => t.Members.Contains(c.SpId) && !request.ExcludedPlayers.Contains(c.PlayerId)
                && (request.Locked.Values.Any(l => l.SpId == c.SpId) || c.IsTraded && request.Grades.Any(g => c.PriceAt(g) > Grades.FloorPrice)))
                .Select(c => c.PlayerId).Distinct().Count() >= 11)
            .Select(t => new AffiliationChoice(t, nearby.Count(s => s.Starters.All(p => t.Members.Contains(p.SpId))), nearby.Count,
                chart.FirstOrDefault(u => u.Id > 0 ? u.Id == t.Color.Id : u.Name == t.Color.Name)?.Users ?? 0, false)).ToList();
        var budgetEvidence = nearby.Count >= MinimumNearbySquads && eligible.Any(c => c.NearbyUsers > 0);
        return eligible.Select(c => c with { BudgetEvidence = budgetEvidence })
            .OrderByDescending(c => budgetEvidence ? c.NearbyUsers : c.RankerUsers).ThenByDescending(c => c.RankerUsers).ThenBy(c => c.Target.Color.Id).ToList();
    }

    /// <summary>Compare complete teams at the same budget. Value is near the best OVR for less money, not OVR divided by BP.</summary>
    public static IReadOnlyList<SquadPlan> Select(IReadOnlyList<SquadPlan> candidates, SquadMode mode, int count = 1)
    {
        if (candidates.Count == 0) return [];
        var best = candidates.Max(p => p.AveragePlayingOvr);
        var tolerance = mode switch { SquadMode.Value => 1.0, SquadMode.Balanced => .5, SquadMode.RankerPicks => 1.5, _ => 0 };
        var eligible = candidates.Where(p => p.AveragePlayingOvr >= best - tolerance);
        var ordered = mode == SquadMode.Strongest ? eligible.OrderByDescending(p => p.AveragePlayingOvr).ThenBy(p => p.TotalPrice)
            : mode == SquadMode.RankerPicks ? eligible.OrderByDescending(p => p.Slots.Sum(s => Math.Log2(1 + s.RankerUsers))).ThenByDescending(p => p.AveragePlayingOvr).ThenBy(p => p.TotalPrice)
            : eligible.OrderBy(p => p.TotalPrice).ThenByDescending(p => p.AveragePlayingOvr);
        var label = mode switch { SquadMode.Strongest => "최강", SquadMode.Balanced => "균형", SquadMode.Value => "가성비", _ => "랭커픽" };
        return ordered.Take(count).Select((p, i) => p with { Mode = mode, Label = i == 0 ? label : $"{label} 대안 {i}",
            SelectionNote = mode == SquadMode.Value ? $"{p.SelectionNote} · 최강 대비 평균 OVR −{best - p.AveragePlayingOvr:0.0}".Trim(' ', '·') : p.SelectionNote }).ToList();
    }
}
