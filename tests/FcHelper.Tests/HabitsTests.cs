using FcHelper.Services;
using FcHelper.Tests.Fixtures;

namespace FcHelper.Tests;

/// <summary>흐름 tab: forfeits, pauses and play hours (OS-10).</summary>
public class HabitsTests
{
    [Fact]
    public void Forfeits_pauses_and_korean_hours_are_counted()
    {
        // 2026-09-25 is a Friday. 13:00 UTC = 22:00 KST Friday; 16:30 UTC = 01:30 KST Saturday.
        var matches = new[]
        {
            new MatchBuilder("opp", "a").At(new DateTime(2026, 9, 25, 13, 0, 0, DateTimeKind.Utc)).A(s => s.Pauses(2)).Build(),
            new MatchBuilder("opp", "b").At(new DateTime(2026, 9, 25, 13, 40, 0, DateTimeKind.Utc)).A(s => s.Pauses(0)).Build(),
            new MatchBuilder("opp", "c").At(new DateTime(2026, 9, 25, 16, 30, 0, DateTimeKind.Utc)).A(s => s.NoStats()).Build(),
            new MatchBuilder("opp", "d").At(new DateTime(2026, 9, 24, 13, 0, 0, DateTimeKind.Utc)).B(s => s.Forfeit()).Build(),
        };

        var h = Habits.Of(matches, "opp");

        Assert.Equal((4, 1, 1), (h.Matches, h.ForfeitLosses, h.ForfeitWins));
        Assert.Equal(0.25, h.ForfeitLossRate);
        Assert.Equal(2.0 / 3, h.AvgPauses, 6); // the forfeit without stats has no pause count
        Assert.Equal(3, h.ByHour[22]);
        Assert.Equal(1, h.ByHour[1]);
        Assert.Equal(2, h.ByDay[4]); // Friday
        Assert.Equal(1, h.ByDay[5]); // Saturday
        Assert.Equal(1, h.ByDay[3]); // Thursday
        Assert.Equal((20, 3), h.PeakHours());
    }

    [Fact]
    public void Peak_block_wraps_past_midnight()
    {
        var h = new Habits(3, 0, 0, 0, Enumerable.Range(0, 24).Select(i => i is 23 or 0 or 1 ? 1 : 0).ToArray(), new int[7]);
        Assert.Equal((23, 3), h.PeakHours());
    }
}
