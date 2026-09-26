using FcHelper.Core.Models;

namespace FcHelper.Services;

/// <summary>One player's line over the analysed matches, from player[].status (직접; the average is 계산).</summary>
public sealed record PlayerLine(int SpId, int Apps, int Goals, int Assists, double AvgRating);

/// <summary>
/// Who carried the team recently: best average rating, most goals, most assists (슈팅 tab, OS-06).
/// A player counts as having played when the match gave them a rating; benchwarmers get none.
/// </summary>
public sealed record PlayerLeaders(PlayerLine? TopRated, PlayerLine? TopScorer, PlayerLine? TopAssister, IReadOnlyList<PlayerLine> All)
{
    /// <summary>Fewest appearances for the rating award, so one great cameo does not win it.</summary>
    public const int MinAppsForRating = 3;

    public static PlayerLeaders Of(IEnumerable<MatchDetail> matches, string ouid)
    {
        var lines = matches
            .Select(m => m.SideOf(ouid))
            .Where(s => s is { HasStats: true })
            .SelectMany(s => s!.Player)
            .Where(p => p.Status.SpRating > 0)
            .GroupBy(p => p.SpId)
            .Select(g => new PlayerLine(g.Key, g.Count(), g.Sum(p => p.Status.Goal), g.Sum(p => p.Status.Assist), g.Average(p => p.Status.SpRating)))
            .ToList();
        if (lines.Count == 0) return new PlayerLeaders(null, null, null, lines);

        var minApps = Math.Min(MinAppsForRating, lines.Max(l => l.Apps));
        var rated = lines.Where(l => l.Apps >= minApps).MaxBy(l => (l.AvgRating, l.Apps));
        var scorer = lines.Where(l => l.Goals > 0).MaxBy(l => (l.Goals, l.Assists, -l.Apps));
        var assister = lines.Where(l => l.Assists > 0).MaxBy(l => (l.Assists, l.Goals, -l.Apps));
        return new PlayerLeaders(rated, scorer, assister, lines);
    }
}
