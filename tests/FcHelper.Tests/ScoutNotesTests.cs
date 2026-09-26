using FcHelper.Analysis;
using FcHelper.Core;
using FcHelper.Core.Models;
using FcHelper.Services;
using FcHelper.Tests.Fixtures;

namespace FcHelper.Tests;

/// <summary>Plain-language 위험 / 약점 comments against the average (user request 2026-09-26).</summary>
public class ScoutNotesTests
{
    private static readonly DateTime Day = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

    private static OpponentReport Report(IReadOnlyList<MatchDetail> matches, Baseline baseline)
    {
        var analysis = UserAnalyzer.Analyze(matches, "opp", baseline);
        return new OpponentReport
        {
            Ouid = "opp", Nickname = "상대", Analysis = analysis, OneLine = "", Matches = matches, Baseline = baseline,
            Form = RecentForm.Of(matches, "opp"),
        };
    }

    /// <summary>40 ordinary matches: 50% possession, plain goals only, four a side.</summary>
    private static Baseline Average() => Baseline.Build(Enumerable.Range(0, 40).Select(i =>
        new MatchBuilder($"a{i}", $"b{i}").At(Day.AddHours(i))
            .A(s => { s.Possession(50); for (var g = 0; g < 4; g++) s.Goal(1); })
            .B(s => { s.Possession(50); for (var g = 0; g < 4; g++) s.Goal(2); })
            .Build()));

    [Fact]
    public void High_possession_and_conceding_finesse_goals_are_called_out()
    {
        var matches = Enumerable.Range(0, 10).Select(i =>
            new MatchBuilder("opp", $"x{i}").At(Day.AddDays(-i))
                .A(s => s.Possession(62).Goal(1).Goal(1))
                .B(s => s.Possession(38).Goal(9, ShotTypes.Finesse, x: 0.8))
                .Build()).ToList();

        var notes = ScoutNotes.Of(Report(matches, Average()));

        var all = string.Join(" | ", notes.Select(n => $"{n.Kind}: {n.Text}"));
        Assert.True(notes.Any(n => n.Kind == NoteKind.Danger && n.Text.StartsWith("점유율이 평균보다 높은 유저입니다 (62%")), all);
        Assert.True(notes.Any(n => n.Kind == NoteKind.Weakness && n.Text.StartsWith("감아차기에 자주 실점하는 유저입니다 (실점의 100%")), all);
        Assert.All(notes, n => Assert.Contains("평균", n.Text.Contains("연승") || n.Text.Contains("연패") || n.Text.Contains("기대") || n.Text.Contains("분에") || n.Text.Contains("선수") ? "평균" : n.Text));
    }

    [Fact]
    public void Too_few_matches_say_nothing()
    {
        var matches = Enumerable.Range(0, ScoutNotes.MinMatches - 1).Select(i => new MatchBuilder("opp", $"x{i}").A(s => s.Possession(70)).Build()).ToList();
        Assert.Empty(ScoutNotes.Of(Report(matches, Average())));
    }
}
