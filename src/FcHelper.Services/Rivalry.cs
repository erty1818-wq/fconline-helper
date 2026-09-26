using FcHelper.Core.Models;

namespace FcHelper.Services;

/// <summary>
/// Two managers' games against each other, from <paramref name="a"/>'s side (비교 tab, OS-16) [직접].
/// Only matches already cached count: the API cannot search one manager's matches for another.
/// </summary>
public sealed record Rivalry(int Matches, int Wins, int Draws, int Losses, int GoalsFor, int GoalsAgainst, DateTime? Last)
{
    public int GoalDifference => GoalsFor - GoalsAgainst;

    public static Rivalry Of(IEnumerable<MatchDetail> matches, string a, string b)
    {
        var games = matches.Where(m => m.SideOf(a) is not null && m.SideOf(b) is not null).ToList();
        int gf = 0, ga = 0, w = 0, d = 0, l = 0;
        foreach (var m in games)
        {
            var (me, them) = (m.SideOf(a)!, m.SideOf(b)!);
            // Same goal rule as the summary card: own goals count for the other side.
            gf += me.Shoot.GoalTotal + them.Shoot.OwnGoal;
            ga += them.Shoot.GoalTotal + me.Shoot.OwnGoal;
            switch (me.MatchDetail.Outcome)
            {
                case MatchOutcome.Win: w++; break;
                case MatchOutcome.Draw: d++; break;
                case MatchOutcome.Loss: l++; break;
            }
        }
        return new Rivalry(games.Count, w, d, l, gf, ga, games.Count == 0 ? null : games.Max(m => m.MatchDate));
    }
}
