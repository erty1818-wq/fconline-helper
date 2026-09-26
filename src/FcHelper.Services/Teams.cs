using FcHelper.Core.Models;

namespace FcHelper.Services;

/// <summary>One squad a manager played with: the matches whose starters mostly match its anchor match.</summary>
public sealed record Team(int Number, IReadOnlySet<int> Anchor, IReadOnlyList<MatchDetail> Matches);

/// <summary>
/// Splits a manager's matches into the squads they used (비교 tab, OS-15) [계산].
/// Rule: going from the newest match, a match joins the first team whose anchor (that team's newest match) shares at
/// least <see cref="MinShared"/> of its starters; otherwise it starts a new team with itself as the anchor.
/// Matches without records (forfeits) are left out.
/// </summary>
public static class Teams
{
    public const int MinShared = 7;

    public static IReadOnlyList<Team> Group(IEnumerable<MatchDetail> matches, string ouid)
    {
        var teams = new List<(HashSet<int> Anchor, List<MatchDetail> Matches)>();
        foreach (var m in matches.OrderByDescending(m => m.MatchDate))
        {
            if (m.SideOf(ouid) is not { HasStats: true } side) continue;
            var starters = side.Player.Where(p => !p.IsSubstitute).Select(p => p.SpId).ToHashSet();
            var team = teams.FirstOrDefault(t => t.Anchor.Count(starters.Contains) >= MinShared);
            if (team.Anchor is null) teams.Add((starters, [m]));
            else team.Matches.Add(m);
        }
        return teams.Select((t, i) => new Team(i + 1, t.Anchor, t.Matches)).ToList();
    }
}
