using FcHelper.Core.Models;
using FcHelper.Services;
using FcHelper.Tests.Fixtures;

namespace FcHelper.Tests;

/// <summary>선수 tab: attack and defence indices (OS-17). The weights are fixed here so a change to them is deliberate.</summary>
public class PlayerIndexTests
{
    [Fact]
    public void Weights_are_the_documented_ones()
    {
        Assert.Equal((3.0, 2.0, 1.0, 0.5), (PlayerIndex.Goal, PlayerIndex.Assist, PlayerIndex.OnTarget, PlayerIndex.Dribble));
        Assert.Equal((1.0, 1.0, 1.0, 0.5), (PlayerIndex.Tackle, PlayerIndex.Intercept, PlayerIndex.Block, PlayerIndex.Aerial));
    }

    [Fact]
    public void Indices_are_per_match_averages_of_the_weighted_status()
    {
        MatchPlayer P(int position, PlayerStatus s) => new() { SpId = 7, SpPosition = position, Status = s };
        MatchDetail Match(params MatchPlayer[] players)
        {
            var m = new MatchBuilder("opp", "x").Build();
            return m with { MatchInfo = [m.SideOf("opp")! with { Player = [.. players] }, m.OpponentOf("opp")!] };
        }
        var matches = new[]
        {
            Match(P(25, new PlayerStatus { SpRating = 8, Goal = 1, Assist = 1, EffectiveShoot = 2, DribbleSuccess = 2 })), // attack 3+2+2+1 = 8
            Match(P(25, new PlayerStatus { SpRating = 6, Tackle = 2, Intercept = 1, Block = 1, AerialSuccess = 2 })),       // defence 2+1+1+1 = 5
            Match(P(28, new PlayerStatus { SpRating = 7 })),                                                              // came on as a sub
            Match(P(28, new PlayerStatus())),                                                                             // stayed on the bench
        };

        var line = Assert.Single(PlayerIndex.Of(matches, "opp"));

        Assert.Equal(("ST", 3, 1, 1), (line.Position, line.Apps, line.Goals, line.Assists));
        Assert.Equal(7.0, line.Rating, 6);
        Assert.Equal(8.0 / 3, line.Attack, 6);
        Assert.Equal(5.0 / 3, line.Defence, 6);
    }
}
