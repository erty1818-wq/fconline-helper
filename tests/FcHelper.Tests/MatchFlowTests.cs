using FcHelper.Core.Models;
using FcHelper.Services;
using FcHelper.Tests.Fixtures;

namespace FcHelper.Tests;

/// <summary>흐름 tab: stat line and shot timeline (OS-08).</summary>
public class MatchFlowTests
{
    private const long Period = 1L << 24;

    [Theory]
    [InlineData(0, 10 * 60, 10.0)]          // first half, 10:00
    [InlineData(0, 47 * 60, 45.0)]          // first-half stoppage stays before half time
    [InlineData(1, 5 * 60, 50.0)]           // second half starts at 45:00
    [InlineData(1, 46 * 60, MatchClock.Late)] // 91:00, second-half stoppage
    [InlineData(2, 3 * 60, MatchClock.Late)]  // extra time
    public void Shot_times_fold_the_stoppages(int period, int seconds, double expected) =>
        Assert.Equal(expected, MatchClock.Place(period * Period + seconds)!.Value, 6);

    [Fact]
    public void Shootout_is_not_on_the_timeline() => Assert.Null(MatchClock.Place(4 * Period + 30));

    [Fact]
    public void Stat_line_averages_over_matches_with_stats()
    {
        var a = new MatchBuilder("opp", "x").A(s => s.Goal(1).Goal(1).Miss(1).Possession(60).Passes(100, 5)).Build();
        var b = new MatchBuilder("opp", "y").A(s => s.Miss(1).Possession(40).Passes(50, 2)).Build();
        var forfeit = new MatchBuilder("opp", "z").A(s => s.NoStats()).Build();
        var matches = new[] { a, b, forfeit }.Select(m => Scored(m, "opp")).ToList();

        var line = StatLine.Of(matches, "opp");

        Assert.Equal(2, line.Matches);
        Assert.Equal(1.0, line.Goals);
        Assert.Equal(50.0, line.Possession);
        Assert.Equal(2.0 / 4, line.ShotAccuracy, 6);
    }

    [Fact]
    public void Rating_is_the_players_average_leaving_out_the_unused_bench()
    {
        var m = new MatchBuilder("opp", "x").Build();
        MatchPlayer P(double rating, int position) => new() { SpId = 1, SpPosition = position, Status = new PlayerStatus { SpRating = rating } };
        m = m with { MatchInfo = [m.SideOf("opp")! with { Player = [P(8, 25), P(6, 0), P(0, 28)] }, m.OpponentOf("opp")!] };

        Assert.Equal(7.0, StatLine.Of([m], "opp").Rating, 6);
    }

    // The builder leaves the shoot and pass summaries at zero; fill them from the shot list the way the API does.
    private static MatchDetail Scored(MatchDetail m, string ouid)
    {
        var s = m.SideOf(ouid)!;
        if (!s.HasStats) return m;
        var shoot = s.Shoot with
        {
            ShootTotal = s.ShootDetail.Count, GoalTotal = s.ShootDetail.Count(d => d.Result == 3),
            EffectiveShootTotal = s.ShootDetail.Count(d => d.Result is 1 or 3),
        };
        return m with { MatchInfo = [s with { Shoot = shoot }, m.OpponentOf(ouid)!] };
    }
}

/// <summary>흐름 tab: goals per quarter-hour (OS-09).</summary>
public class TimeBucketTests
{
    private const long Period = 1L << 24;

    [Theory]
    [InlineData(0, 14 * 60 + 59, 0)]
    [InlineData(0, 44 * 60, 2)]
    [InlineData(0, 47 * 60, 2)] // first-half stoppage
    [InlineData(1, 0, 3)]       // 45:00, second-half kickoff
    [InlineData(1, 44 * 60, 5)] // 89:00
    [InlineData(1, 46 * 60, 6)] // 91:00
    [InlineData(3, 60, 6)]      // extra time
    public void Buckets_follow_the_period(int period, int seconds, int bucket) =>
        Assert.Equal(bucket, MatchClock.BucketOf(period * Period + seconds));

    [Fact]
    public void Bucket_totals_are_the_goals_scored_and_conceded()
    {
        var matches = new[]
        {
            new MatchBuilder("opp", "a").A(s => s.Goal(1, minute: 10).Goal(1, minute: 80).Miss(1, minute: 5)).B(s => s.Goal(2, minute: 50)).Build(),
            new MatchBuilder("b", "opp").A(s => s.Goal(3, minute: 20)).B(s => s.Goal(4, minute: 70)).Build(),
        };
        var taken = ShotMap.Of(matches, "opp", conceded: false);
        var allowed = ShotMap.Of(matches, "opp", conceded: true);

        var buckets = TimeBuckets.Of(taken, allowed);

        Assert.Equal(7, buckets.Count);
        Assert.Equal(taken.Goals, buckets.Sum(b => b.For));
        Assert.Equal(allowed.Goals, buckets.Sum(b => b.Against));
        Assert.Equal(1, buckets[0].For);
        Assert.Equal(1, buckets[1].Against);
    }
}
