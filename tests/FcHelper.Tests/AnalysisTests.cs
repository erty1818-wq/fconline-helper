using FcHelper.Analysis;
using FcHelper.Core;
using FcHelper.Core.Models;
using FcHelper.Tests.Fixtures;

namespace FcHelper.Tests;

public class UserAnalyzerTests
{
    private const int Ronaldo = 101000001;
    private const int Gullit = 101000002;
    private const int Other = 101000003;

    /// <summary>An opponent who mostly scores finesse shots from the right of the box, Gullit → Ronaldo.</summary>
    private static List<MatchDetail> FinesseSpecialist(int matches = 10) =>
        Enumerable.Range(0, matches).Select(i => new MatchBuilder("opp", $"victim{i}")
            .At(new DateTime(2026, 9, 1).AddHours(i))
            .A(s => s.Controller("pad").Possession(58)
                .Goal(Ronaldo, ShotTypes.Finesse, x: 0.9, y: 0.75, minute: 20, assist: Gullit)
                .Goal(Ronaldo, ShotTypes.Finesse, x: 0.9, y: 0.75, minute: 80, assist: Gullit)
                .Goal(Other, ShotTypes.Normal, x: 0.9, y: 0.5, minute: 60))
            .B(s => s.Goal(Other, ShotTypes.Header, x: 0.95, y: 0.5, minute: 50))
            .Build()).ToList();

    /// <summary>A population where goals are mostly normal shots from the centre.</summary>
    private static Baseline OrdinaryBaseline()
    {
        var matches = Enumerable.Range(0, 60).Select(i => new MatchBuilder($"p{i}", $"q{i}")
            .A(s => s.Goal(Other, ShotTypes.Normal).Goal(Other, ShotTypes.Normal).Goal(Other, ShotTypes.Finesse, y: 0.5)
                .Goal(Other, ShotTypes.Header, y: 0.5))
            .B(s => s.Goal(Other, ShotTypes.Normal, y: 0.5).Goal(Other, ShotTypes.Normal, x: 0.75)
                .Goal(Other, ShotTypes.Header, x: 0.95).Goal(Other, ShotTypes.Normal))
            .Build());
        return Baseline.Build(matches);
    }

    [Fact]
    public void Counts_record_and_averages()
    {
        var a = UserAnalyzer.Analyze(FinesseSpecialist(), "opp");

        Assert.Equal(new RecordSummary(10, 10, 0, 0, 0), a.Record);
        Assert.Equal(3, a.AvgGoalsFor);
        Assert.Equal(1, a.AvgGoalsAgainst);
        Assert.Equal(58, a.AvgPossession);
        Assert.Equal(30, a.GoalCount);
        Assert.Equal(10, a.ConcededCount);
        Assert.Equal("pad", a.Controller!.Label);
        Assert.Equal(1.0, a.Conversion);
    }

    [Fact]
    public void Finds_players_combos_and_signature_goal()
    {
        var a = UserAnalyzer.Analyze(FinesseSpecialist(), "opp");

        var top = a.Players[0];
        Assert.Equal(Ronaldo, top.SpId);
        Assert.Equal(20, top.Goals);
        Assert.Equal(20.0 / 30, top.GoalShare, 3);
        Assert.Equal(new GoalCombo(Gullit, Ronaldo, 20), a.Combos[0]);
        Assert.Equal(new SignatureGoal(Ronaldo, Gullit, GoalZone.BoxRight, ShotTypes.Finesse, 20, 30), a.Signature);
        Assert.Equal("감아차기", a.GoalTypes[0].Label);
        Assert.Equal("헤더", a.ConcededTypes.Single().Label);
    }

    [Fact]
    public void Without_a_baseline_uses_absolute_thresholds_and_no_comparison_text()
    {
        var a = UserAnalyzer.Analyze(FinesseSpecialist(), "opp", baseline: null, playerName: id => id == Ronaldo ? "호날두" : "?");

        Assert.False(a.ComparedToBaseline);
        Assert.Contains(a.Threats, t => t.Key == "goal.type.2" && t.Text == "감아차기 득점 67%");
        Assert.Contains(a.Threats, t => t.Key == "player.dependency" && t.Text.StartsWith("호날두 득점 의존 67%"));
        Assert.Contains(a.Weaknesses, w => w.Key == "conceded.type.3");
        Assert.DoesNotContain(a.Insights, i => System.Text.RegularExpressions.Regex.IsMatch(i.Text, @"\(평균 \d"));
    }

    [Fact]
    public void With_a_baseline_reports_lift_over_the_population()
    {
        var baseline = OrdinaryBaseline();
        Assert.True(baseline.IsUsable);

        var a = UserAnalyzer.Analyze(FinesseSpecialist(), "opp", baseline);

        Assert.True(a.ComparedToBaseline);
        var finesse = Assert.Single(a.Threats, t => t.Key == "goal.type.2");
        Assert.Contains("(평균 13%)", finesse.Text);
        Assert.True(finesse.Rate > finesse.BaselineRate);
        // Normal-shot goals are below the population rate, so they must not be flagged.
        Assert.DoesNotContain(a.Threats, t => t.Key == "goal.type.1");
    }

    [Fact]
    public void Small_samples_are_shrunk_toward_the_baseline()
    {
        var baseline = OrdinaryBaseline();
        // One match, one finesse goal: 100% raw, but far too little evidence.
        var tiny = new[] { new MatchBuilder("opp", "x").A(s => s.Goal(Ronaldo, ShotTypes.Finesse)).Build() };

        var a = UserAnalyzer.Analyze(tiny, "opp", baseline);

        Assert.DoesNotContain(a.Threats, t => t.Key == "goal.type.2");
    }

    [Fact]
    public void First_goal_stats_skip_matches_with_unaccounted_goals()
    {
        var matches = new List<MatchDetail>
        {
            new MatchBuilder("me", "a").A(s => s.Goal(1, minute: 10)).B(s => s.Goal(2, minute: 50)).A(s => s.Goal(1, minute: 70)).Build(),
            new MatchBuilder("me", "b").B(s => s.Goal(2, minute: 5)).Build(),
            new MatchBuilder("me", "c").Build(),
        };
        // An own goal shows in the score but has no shot entry: that match must be skipped.
        var ownGoal = new MatchBuilder("me", "d").A(s => s.Goal(1, minute: 30)).Build();
        var me = ownGoal.SideOf("me")!;
        matches.Add(ownGoal with
        {
            MatchInfo = [me with { Shoot = me.Shoot with { GoalTotalDisplay = 2 } }, ownGoal.OpponentOf("me")!],
        });

        var a = UserAnalyzer.Analyze(matches, "me");

        Assert.Equal(new FirstGoalStats(1, 1, 1, 0, 1), a.FirstGoal);
    }

    [Fact]
    public void Forfeits_and_pauses_become_direct_traits()
    {
        var matches = Enumerable.Range(0, 6).Select(i => new MatchBuilder("opp", $"x{i}")
            .A(s => { s.Pauses(3); if (i < 2) s.Forfeit(); })
            .Build()).ToList();

        var a = UserAnalyzer.Analyze(matches, "opp");

        Assert.Equal(2, a.Record.Forfeits);
        Assert.Contains(a.Traits, t => t.Key == "match.forfeit" && t.Evidence == Evidence.Direct);
        Assert.Contains(a.Traits, t => t.Key == "pause" && t.Text == "일시정지 경기당 3회");
    }

    [Fact]
    public void Inferred_route_tags_are_marked_as_inferred()
    {
        // Cut-backs: assist from near the byline on the wing, finished centrally.
        var matches = Enumerable.Range(0, 5).Select(i => new MatchBuilder("opp", $"x{i}")
            .A(s => s.Goal(Ronaldo, ShotTypes.Normal, x: 0.92, y: 0.5, assist: Gullit, assistX: 0.97, assistY: 0.15))
            .Build()).ToList();

        var a = UserAnalyzer.Analyze(matches, "opp");

        var cutback = Assert.Single(a.Threats, t => t.Key == "route.cutback");
        Assert.Equal(Evidence.Inferred, cutback.Evidence);
    }

    [Fact]
    public void Empty_input_is_safe()
    {
        var a = UserAnalyzer.Analyze([], "opp");
        Assert.Equal(0, a.Record.Matches);
        Assert.Empty(a.Insights);
        Assert.Equal("분석할 공식경기 기록이 없습니다.", Summary.OneLine(a));
    }

    [Fact]
    public void One_line_summary_names_threats_and_weakness()
    {
        var a = UserAnalyzer.Analyze(FinesseSpecialist(), "opp", playerName: _ => "호날두");
        var line = Summary.OneLine(a);
        Assert.Contains("위주.", line);
        Assert.Contains("약점: 헤더 실점.", line);
    }

    [Fact]
    public void Matchup_pairs_opponent_threats_with_my_weaknesses()
    {
        var opp = UserAnalyzer.Analyze(FinesseSpecialist(), "opp");
        // I concede mostly finesse goals.
        var myMatches = Enumerable.Range(0, 8).Select(i => new MatchBuilder("me", $"y{i}")
            .B(s => s.Goal(Other, ShotTypes.Finesse, y: 0.75).Goal(Other, ShotTypes.Finesse, y: 0.3))
            .Build()).ToList();
        var me = UserAnalyzer.Analyze(myMatches, "me");

        var alert = Assert.Single(Matchup.Compare(opp, me), a => a.OpponentThreat.Key == "goal.type.2");
        Assert.Equal("상대 감아차기 득점 ↑ × 내 감아차기 실점 ↑", alert.Text);
    }
}
