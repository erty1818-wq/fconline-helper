using FcHelper.Core;
using FcHelper.Services;
using FcHelper.Tests.Fixtures;

namespace FcHelper.Tests;

/// <summary>xG [추정] and shot types (OS-07). The coefficients are fixed here so a change to them is deliberate.</summary>
public class ExpectedGoalsTests
{
    [Fact]
    public void Coefficients_are_the_fitted_ones()
    {
        Assert.Equal((0.33, -0.110, 2.25, -1.47, 0.78),
            (ExpectedGoals.Intercept, ExpectedGoals.PerMetre, ExpectedGoals.PerRadian, ExpectedGoals.Header, ExpectedGoals.Penalty));
    }

    [Fact]
    public void Geometry_measures_from_the_goal_centre()
    {
        var (d, a) = ExpectedGoals.Geometry(1 - 11.0 / 105, 0.5); // the penalty spot
        Assert.Equal(11, d, 6);
        Assert.Equal(2 * Math.Atan(3.66 / 11), a, 6);
    }

    [Fact]
    public void Closer_central_and_footed_shots_are_likelier()
    {
        var near = ExpectedGoals.Of(0.95, 0.5, ShotTypes.Normal);
        var far = ExpectedGoals.Of(0.75, 0.5, ShotTypes.Normal);
        var wide = ExpectedGoals.Of(0.95, 0.15, ShotTypes.Normal);
        var header = ExpectedGoals.Of(0.95, 0.5, ShotTypes.Header);
        Assert.True(near > far && near > wide && near > header);
        Assert.InRange(far, 0.05, 0.35);
        Assert.Equal(0.78, ExpectedGoals.Of(0.3, 0.1, ShotTypes.Penalty));
    }

    [Fact]
    public void Shot_types_add_up_to_the_map()
    {
        var match = new MatchBuilder("opp", "x")
            .A(s => s.Goal(1, ShotTypes.Finesse).Goal(1, ShotTypes.Finesse).Miss(1, ShotTypes.Finesse).Goal(2, ShotTypes.Header)).Build();
        var map = ShotMap.Of([match], "opp", conceded: false);
        var lines = ShotTypeLine.Of(map);

        Assert.Equal(map.Shots, lines.Sum(l => l.Shots));
        Assert.Equal(map.Goals, lines.Sum(l => l.Goals));
        Assert.Equal(map.Xg, lines.Sum(l => l.Xg), 9);
        var finesse = lines[0];
        Assert.Equal((ShotTypes.Finesse, 3, 2), (finesse.Type, finesse.Shots, finesse.Goals));
        Assert.Equal(2.0 / 3, finesse.Conversion, 9);
    }
}
