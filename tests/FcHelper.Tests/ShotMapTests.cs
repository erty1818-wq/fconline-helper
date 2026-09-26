using FcHelper.Core;
using FcHelper.Services;
using FcHelper.Tests.Fixtures;

namespace FcHelper.Tests;

/// <summary>슈팅 tab: shots taken and allowed, on a half pitch (OS-05).</summary>
public class ShotMapTests
{
    [Fact]
    public void Taken_and_allowed_shots_come_from_the_right_side()
    {
        var matches = new[]
        {
            new MatchBuilder("opp", "a").A(s => s.Goal(1).Miss(1)).B(s => s.Goal(9, ShotTypes.Header)).Build(),
            new MatchBuilder("b", "opp").A(s => s.Miss(8)).B(s => s.Goal(2, x: 0.95)).Build(),
        };

        var taken = ShotMap.Of(matches, "opp", conceded: false);
        var allowed = ShotMap.Of(matches, "opp", conceded: true);

        Assert.Equal((3, 2, 2), (taken.Shots, taken.Goals, taken.OnTarget));
        Assert.Equal([1, 1, 2], taken.Dots.Select(d => d.SpId).Order());
        Assert.Equal((2, 1), (allowed.Shots, allowed.Goals));
        Assert.Equal(2, taken.Matches);
        Assert.Equal(1.5, taken.PerMatch);
    }

    [Fact]
    public void Forfeits_count_neither_shots_nor_matches()
    {
        var matches = new[]
        {
            new MatchBuilder("opp", "a").A(s => s.Goal(1)).Build(),
            new MatchBuilder("opp", "b").A(s => s.NoStats()).Build(),
        };
        var map = ShotMap.Of(matches, "opp", conceded: false);
        Assert.Equal((1, 1), (map.Shots, map.Matches));
    }

    [Theory]
    [InlineData(1.0, 0.5, 0.5, 0.0)]  // on the goal line, centre
    [InlineData(0.75, 0.1, 0.1, 0.5)] // halfway into the half, on the shooter's left
    [InlineData(0.5, 0.9, 0.9, 1.0)]  // halfway line
    [InlineData(0.2, 0.5, 0.5, 1.0)]  // own half: kept on the halfway line
    public void Coordinates_put_the_attacked_goal_on_top(double x, double y, double across, double down)
    {
        var (a, d) = ShotMap.ToHalfPitch(x, y);
        Assert.Equal(across, a, 6);
        Assert.Equal(down, d, 6);
    }

    [Fact]
    public void Box_flag_follows_the_pitch_zones()
    {
        var match = new MatchBuilder("opp", "a").A(s => s.Goal(1, x: 0.95, y: 0.5).Miss(1, x: 0.7, y: 0.5)).Build();
        var dots = ShotMap.Of([match], "opp", conceded: false).Dots;
        Assert.Equal([true, false], dots.Select(d => d.InBox));
    }
}
