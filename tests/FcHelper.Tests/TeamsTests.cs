using FcHelper.Services;
using FcHelper.Tests.Fixtures;

namespace FcHelper.Tests;

/// <summary>비교 tab: squads the manager switched between (OS-15). The grouping rule is fixed here.</summary>
public class TeamsTests
{
    private static readonly int[] Shape = [0, 3, 4, 6, 7, 10, 10, 18, 12, 16, 25];

    /// <param name="first">spId of the first starter; the eleven are first … first + 10.</param>
    private static FcHelper.Core.Models.MatchDetail Match(int first, int daysAgo) =>
        new MatchBuilder("opp", "x").At(new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc).AddDays(-daysAgo))
            .A(s => { for (var i = 0; i < 11; i++) s.Player(first + i, Shape[i]); }).Build();

    [Fact]
    public void Seven_shared_starters_make_one_team()
    {
        var matches = new[]
        {
            Match(100, 0),  // team 1 anchor: 100-110
            Match(104, 1),  // shares 104-110 = 7 with the anchor → team 1
            Match(105, 2),  // shares 105-110 = 6 → a new team
            Match(500, 3),  // nothing in common → another team
            Match(105, 4),  // same as the third match → joins team 2
        };

        var teams = Teams.Group(matches, "opp");

        Assert.Equal([2, 2, 1], teams.Select(t => t.Matches.Count));
        Assert.Equal([1, 2, 3], teams.Select(t => t.Number));
        Assert.Contains(100, teams[0].Anchor);
    }

    [Fact]
    public void Forfeits_are_left_out()
    {
        var forfeit = new MatchBuilder("opp", "x").A(s => s.NoStats()).Build();
        Assert.Single(Teams.Group([Match(100, 0), forfeit], "opp"));
    }
}
