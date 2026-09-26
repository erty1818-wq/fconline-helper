using FcHelper.Core.Models;

namespace FcHelper.Services;

/// <summary>Results of the matches that share one key (a controller, a formation …) [직접; averages 계산].</summary>
public sealed record SplitLine(string Key, int Matches, int Wins, int Draws, int Losses, double GoalsFor, double GoalsAgainst)
{
    public double WinRate => Matches == 0 ? 0 : (double)Wins / Matches;
}

/// <summary>Groups a manager's matches by a key for the 비교 tab: 패드/키보드 (OS-12), 포메이션 (OS-13).</summary>
public static class Splits
{
    /// <summary>Label for matches whose key is unknown (forfeits without records …), kept so the lines add up to all matches.</summary>
    public const string Unknown = "알 수 없음";

    /// <param name="key">The group of one side, or null when it cannot be told.</param>
    public static IReadOnlyList<SplitLine> By(IEnumerable<MatchDetail> matches, string ouid, Func<MatchInfo, string?> key) =>
        matches
            .Select(m => (Me: m.SideOf(ouid), Them: m.OpponentOf(ouid)))
            .Where(x => x.Me is not null)
            .GroupBy(x => key(x.Me!) is { Length: > 0 } k ? k : Unknown)
            .Select(g => new SplitLine(g.Key, g.Count(),
                g.Count(x => x.Me!.MatchDetail.Outcome == MatchOutcome.Win),
                g.Count(x => x.Me!.MatchDetail.Outcome == MatchOutcome.Draw),
                g.Count(x => x.Me!.MatchDetail.Outcome == MatchOutcome.Loss),
                // Same goal rule as the summary card: own goals count for the other side.
                g.Average(x => x.Me!.Shoot.GoalTotal + (x.Them?.Shoot.OwnGoal ?? 0)),
                g.Average(x => (x.Them?.Shoot.GoalTotal ?? 0) + x.Me!.Shoot.OwnGoal)))
            // Unknown last, the rest by how often they were used.
            .OrderBy(l => l.Key == Unknown).ThenByDescending(l => l.Matches).ThenBy(l => l.Key)
            .ToList();

    public static IReadOnlyList<SplitLine> ByController(IEnumerable<MatchDetail> matches, string ouid) =>
        By(matches, ouid, s => s.HasStats && s.MatchDetail.Controller.Length > 0 ? ReportText.ControllerLabel(s.MatchDetail.Controller) : null);

    /// <summary>
    /// The lineup that stands for a formation (OS-14): of the matches played in it, the one whose starters appear most
    /// often in the others, so it is a real eleven with real positions and grades. Null when the formation was not used.
    /// </summary>
    public static (MatchInfo Side, int Matches)? Representative(IEnumerable<MatchDetail> matches, string ouid, string formation)
    {
        var sides = matches.Select(m => m.SideOf(ouid))
            .Where(s => s is { HasStats: true } && SquadContext.FormationOf(s) == formation)
            .Cast<MatchInfo>().ToList();
        if (sides.Count == 0) return null;
        var used = sides.SelectMany(s => s.Player.Where(p => !p.IsSubstitute).Select(p => p.SpId)).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        // Ties go to the newer match (the list is newest first).
        var best = sides.Select((s, i) => (Side: s, Score: s.Player.Where(p => !p.IsSubstitute).Sum(p => used[p.SpId]), Order: i))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Order).First().Side;
        return (best, sides.Count);
    }

    /// <summary>Formation estimated from the starters' positions [추정].</summary>
    public static IReadOnlyList<SplitLine> ByFormation(IEnumerable<MatchDetail> matches, string ouid) =>
        By(matches, ouid, s => s.HasStats ? SquadContext.FormationOf(s) : null);
}
