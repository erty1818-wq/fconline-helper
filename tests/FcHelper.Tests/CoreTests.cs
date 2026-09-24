using FcHelper.Core;

namespace FcHelper.Tests;

public class GoalTimeTests
{
    [Theory]
    [InlineData(0L, 0, MatchPeriod.FirstHalf)]
    [InlineData(1800L, 1800, MatchPeriod.FirstHalf)]
    [InlineData((1L << 24) + 30, 45 * 60 + 30, MatchPeriod.SecondHalf)]
    [InlineData((2L << 24) + 5, 90 * 60 + 5, MatchPeriod.ExtraFirstHalf)]
    [InlineData((3L << 24), 105 * 60, MatchPeriod.ExtraSecondHalf)]
    [InlineData((4L << 24) + 1, 120 * 60 + 1, MatchPeriod.PenaltyShootout)]
    public void Decodes_period_and_seconds(long encoded, int seconds, MatchPeriod period)
    {
        Assert.Equal(seconds, GoalTime.ToSeconds(encoded));
        Assert.Equal(period, GoalTime.PeriodOf(encoded));
    }

    [Fact]
    public void Negative_values_are_treated_as_kickoff() => Assert.Equal(0, GoalTime.ToSeconds(-5));
}

public class PitchTests
{
    [Theory]
    [InlineData(0.97, 0.5, GoalZone.SixYardBox)]
    [InlineData(0.90, 0.5, GoalZone.BoxCenter)]
    [InlineData(0.90, 0.25, GoalZone.BoxLeft)]
    [InlineData(0.90, 0.75, GoalZone.BoxRight)]
    [InlineData(0.75, 0.5, GoalZone.LongCenter)]
    [InlineData(0.75, 0.1, GoalZone.LongLeft)]
    [InlineData(0.75, 0.9, GoalZone.LongRight)]
    [InlineData(0.40, 0.5, GoalZone.Far)]
    public void Classifies_zones(double x, double y, GoalZone expected) => Assert.Equal(expected, Pitch.ZoneOf(x, y));

    [Fact]
    public void Api_in_box_flag_overrides_coordinates()
    {
        Assert.Equal(GoalZone.BoxCenter, Pitch.ZoneOf(0.80, 0.5, inPenalty: true));
        Assert.Equal(GoalZone.LongCenter, Pitch.ZoneOf(0.90, 0.5, inPenalty: false));
    }

    [Fact]
    public void Out_of_range_coordinates_are_clamped()
    {
        Assert.Equal(GoalZone.SixYardBox, Pitch.ZoneOf(1.3, 0.5));
        Assert.Equal(Lane.RightWing, Pitch.LaneOf(1.2));
    }

    [Theory]
    [InlineData(0.1, Lane.LeftWing)]
    [InlineData(0.3, Lane.LeftHalfSpace)]
    [InlineData(0.5, Lane.Center)]
    [InlineData(0.7, Lane.RightHalfSpace)]
    [InlineData(0.9, Lane.RightWing)]
    public void Classifies_lanes(double y, Lane lane) => Assert.Equal(lane, Pitch.LaneOf(y));

    [Fact]
    public void SpId_splits_into_season_and_player()
    {
        Assert.Equal(101, SpId.SeasonOf(101000001));
        Assert.Equal(1, SpId.PlayerOf(101000001));
    }
}
