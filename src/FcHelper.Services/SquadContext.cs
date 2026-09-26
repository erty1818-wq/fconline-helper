using FcHelper.Analysis;
using FcHelper.Core;
using FcHelper.Core.Models;
using FcHelper.Data;
using FcHelper.Market;

namespace FcHelper.Services;

/// <summary>Links the match data (official API cache) to the squad tools: my current eleven, the opponent's needs and formation.</summary>
public static class SquadContext
{
    /// <summary>
    /// The starters and grades of the user's latest cached official match, i.e. the squad they own and play now.
    /// </summary>
    public static IReadOnlyList<OwnedCard> MyCurrentCards(FcDatabase db, string myOuid, int matchType = 50)
    {
        var latest = db.GetMatches(db.GetCachedMatchIds(myOuid, matchType, 1)).FirstOrDefault()?.SideOf(myOuid);
        return latest is null ? [] : StartersOf(latest);
    }

    /// <summary>The eleven who started for one side of a match, with their grades and positions.</summary>
    public static IReadOnlyList<OwnedCard> StartersOf(MatchInfo side) => side.Player.Where(p => !p.IsSubstitute)
        .Select(p => new OwnedCard(p.SpId, Math.Max(1, p.SpGrade), Positions.Label(p.SpPosition))).ToList();

    /// <summary>Formation of one side, from its starters' positions [추정].</summary>
    public static string? FormationOf(MatchInfo side) =>
        Advisors.EstimateFormation(side.Player.Where(p => !p.IsSubstitute).Select(p => p.SpPosition));

    /// <summary>
    /// The opponent's most frequent formation over their cached matches, estimated from starters' positions [추정].
    /// </summary>
    public static string? OpponentFormation(FcDatabase db, string ouid, int matchType = 50, int matches = 15) =>
        db.GetMatches(db.GetCachedMatchIds(ouid, matchType, matches))
            .Select(m => m.SideOf(ouid))
            .Where(s => s is { HasStats: true })
            .Select(s => Advisors.EstimateFormation(s!.Player.Where(p => !p.IsSubstitute).Select(p => p.SpPosition)))
            .Where(f => f is not null)
            .GroupBy(f => f).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;

    /// <summary>What to look for against this opponent: their weaknesses to exploit and their threats to cover.</summary>
    public static IReadOnlyList<TacticalNeed> NeedsAgainst(UserAnalysis opponent)
    {
        var needs = new List<TacticalNeed>();
        foreach (var i in opponent.Insights)
        {
            var need = i.Key switch
            {
                SideMetrics.ConcededType + "3" => TacticalNeed.AerialForward,
                SideMetrics.ConcededLongRange => TacticalNeed.LongShots,
                SideMetrics.ConcededType + "2" => TacticalNeed.FinesseWide,
                SideMetrics.GoalType + "3" or SideMetrics.RouteCross => TacticalNeed.AerialDefence,
                SideMetrics.RouteThrough => TacticalNeed.PaceDefence,
                SideMetrics.GoalZone + nameof(GoalZone.SixYardBox) or SideMetrics.GoalZone + nameof(GoalZone.BoxCenter) or SideMetrics.RouteCutback => TacticalNeed.BoxDefence,
                SideMetrics.GoalType + "6" or SideMetrics.GoalLongRange => TacticalNeed.ShotStopper,
                _ => (TacticalNeed?)null,
            };
            if (need is { } n && !needs.Contains(n)) needs.Add(n);
        }
        // Many through balls against few goals conceded from them still says: pace behind their line is worth trying.
        if (opponent.Rates.TryGetValue(SideMetrics.PassThrough, out var through) && through.Value > 0.2 && !needs.Contains(TacticalNeed.PaceDefence))
            needs.Add(TacticalNeed.PaceDefence);
        return needs;
    }
}
