using FcHelper.Core.Models;
using FcHelper.Services;
using FcHelper.Tests.Fixtures;

namespace FcHelper.Tests;

/// <summary>최근 경기 요약: best rating, most goals, most assists (OS-06).</summary>
public class PlayerLeadersTests
{
    private static MatchPlayer P(int spId, double rating, int goals = 0, int assists = 0, int position = 25) => new()
    {
        SpId = spId, SpPosition = position, SpGrade = 5,
        Status = new PlayerStatus { SpRating = rating, Goal = goals, Assist = assists },
    };

    private static MatchDetail Match(params MatchPlayer[] players)
    {
        var m = new MatchBuilder("opp", "x").Build();
        var side = m.SideOf("opp")!;
        return m with { MatchInfo = [side with { Player = [.. players] }, m.OpponentOf("opp")!] };
    }

    [Fact]
    public void Leaders_are_picked_from_the_player_status_totals()
    {
        var matches = new[]
        {
            Match(P(1, 8.0, goals: 2), P(2, 7.0, assists: 2), P(3, 9.9)),
            Match(P(1, 7.0, goals: 1), P(2, 7.5, assists: 1)),
            Match(P(1, 7.5), P(2, 8.5, goals: 1, assists: 1), P(4, 0, position: 28)),
        };

        var l = PlayerLeaders.Of(matches, "opp");

        Assert.Equal((1, 3, 3), (l.TopScorer!.SpId, l.TopScorer.Goals, l.TopScorer.Apps));
        Assert.Equal((2, 4), (l.TopAssister!.SpId, l.TopAssister.Assists));
        // Player 3's one 9.9 does not beat three good matches; player 4 never came on.
        Assert.Equal(2, l.TopRated!.SpId);
        Assert.Equal(7.667, l.TopRated.AvgRating, 3);
        Assert.DoesNotContain(l.All, x => x.SpId == 4);
    }

    [Fact]
    public void No_rated_players_gives_no_leaders()
    {
        var l = PlayerLeaders.Of([new MatchBuilder("opp", "x").A(s => s.NoStats()).Build()], "opp");
        Assert.Null(l.TopRated);
        Assert.Empty(l.All);
    }
}
