using FcHelper.Core.Models;
using FcHelper.Services;
using FcHelper.Tests.Fixtures;

namespace FcHelper.Tests;

/// <summary>The header of the opponent card: last results, current run, division emblem (OS-02).</summary>
public class RecentFormTests
{
    private static readonly DateTime Day = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    // "me" scores `mine`, "opp" scores `theirs`; `daysAgo` sets the order.
    private static MatchDetail Match(int daysAgo, int mine, int theirs) => new MatchBuilder("me", "opp").At(Day.AddDays(-daysAgo))
        .A(s => { for (var i = 0; i < mine; i++) s.Goal(1); })
        .B(s => { for (var i = 0; i < theirs; i++) s.Goal(2); })
        .Build();

    [Fact]
    public void Results_are_newest_first_whatever_the_input_order()
    {
        var form = RecentForm.Of([Match(3, 0, 1), Match(1, 1, 0), Match(2, 1, 1)], "me");
        Assert.Equal([MatchOutcome.Win, MatchOutcome.Draw, MatchOutcome.Loss], form.Results);
    }

    [Fact]
    public void Streak_counts_the_run_from_the_newest_match()
    {
        var form = RecentForm.Of([Match(1, 2, 0), Match(2, 1, 0), Match(3, 3, 1), Match(4, 0, 1), Match(5, 1, 0)], "me");
        Assert.Equal((MatchOutcome.Win, 3), (form.Streak, form.StreakLength));
        Assert.Equal("3연승 중", form.StreakText);

        var losing = RecentForm.Of([Match(1, 0, 1), Match(2, 0, 2), Match(3, 1, 0)], "me");
        Assert.Equal("2연패 중", losing.StreakText);
        Assert.Equal("", RecentForm.Of([Match(1, 1, 0), Match(2, 0, 1)], "me").StreakText);
    }

    [Fact]
    public void Only_the_last_twenty_count_and_no_matches_is_empty()
    {
        var many = Enumerable.Range(0, 25).Select(d => Match(d, 1, 0)).ToList();
        var form = RecentForm.Of(many, "me");
        Assert.Equal(RecentForm.DefaultCount, form.Results.Count);
        Assert.Equal(20, form.StreakLength);

        var none = RecentForm.Of([], "me");
        Assert.Empty(none.Results);
        Assert.Equal("", none.StreakText);
    }

    [Theory]
    [InlineData(800, "ico_rank0.png")]
    [InlineData(1300, "ico_rank5.png")]
    [InlineData(1700, "ico_rank6.png")]
    [InlineData(3100, "ico_rank20.png")]
    public void Division_emblem_is_the_place_in_the_division_list(int division, string file) =>
        Assert.EndsWith("/" + file, DivisionIcon.Url(division));

    [Fact]
    public void Unknown_division_has_no_emblem() => Assert.Null(DivisionIcon.Url(1500));
}
