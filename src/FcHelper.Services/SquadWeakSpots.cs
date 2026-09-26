using FcHelper.Market;

namespace FcHelper.Services;

/// <summary>One slot of the opponent's eleven that is weaker than the rest [계산].</summary>
public sealed record WeakSpot(int Index, string Position, string Name, double Ovr, double Gap, int Pay, double? PayRank, string Reason);

/// <summary>
/// Weak spots in the opponent's latest eleven (스쿼드 tab): managers who spend on the attack often leave the keeper or the
/// full-backs clearly below the rest. A slot is weak when its OVR is <see cref="WeakGap"/> or more under the eleven's
/// average, or when it is the keeper on a low salary (bottom quarter of market keepers). [계산: plain comparisons.]
/// </summary>
public static class SquadWeakSpots
{
    public const double WeakGap = 3;
    /// <summary>Salary rank among market cards of the same group: below this is 저급여, above 1 − this is 고급여.</summary>
    public const double PayQuarter = 0.25;

    public static string PayLabel(double? rank) => rank switch
    {
        null => "",
        <= PayQuarter => "저급여",
        >= 1 - PayQuarter => "고급여",
        _ => "보통 급여",
    };

    /// <param name="payRank">Salary rank of a card within its group (<see cref="SquadService.PayRank"/>).</param>
    /// <returns>The eleven's average OVR and the weak slots, weakest first.</returns>
    public static (double Average, IReadOnlyList<WeakSpot> Spots) Of(IReadOnlyList<SquadSlot> slots, Func<MarketCard, double?> payRank)
    {
        if (slots.Count == 0) return (0, []);
        // The OVR the pitch shows (with team colours), so the numbers match the cards.
        var average = slots.Average(s => s.ShownOvr);
        var spots = new List<WeakSpot>();
        foreach (var s in slots)
        {
            var gap = s.ShownOvr - average;
            var rank = payRank(s.Card);
            var keeper = s.Position == "GK";
            var cheapKeeper = keeper && rank is <= PayQuarter;
            if (gap > -WeakGap && !cheapKeeper) continue;
            var reasons = new List<string>();
            if (gap <= -WeakGap) reasons.Add($"팀 평균보다 OVR {-gap:0.0} 낮음");
            if (cheapKeeper) reasons.Add($"저급여 GK (급여 {s.Pay}, 시장 GK 중 하위 {rank!.Value * 100:0}%)");
            spots.Add(new WeakSpot(s.Index, s.Position, s.Card.Name, s.ShownOvr, gap, s.Pay, rank, string.Join(" · ", reasons)));
        }
        return (average, spots.OrderBy(w => w.Gap).ToList());
    }
}
