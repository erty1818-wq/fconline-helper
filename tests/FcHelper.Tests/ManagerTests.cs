using FcHelper.Core.Models;
using FcHelper.Market;
using FcHelper.Services;

namespace FcHelper.Tests;

public class ManagerModeTests
{
    private const string RankingRow = """
        <div class="tr">
            <span class="td rank_no">1</span>
            <span class="td rank_coach">
                <span class="coach_wrap"><span class="name profile_pointer" data-sn="1">Gucci보이</span></span>
                <span class="price" alt="10,465,340,000" title="10,465,340,000">104억 6,534만</span>
            </span>
            <span class="td team_color">
                <span class="name"><span class="inner">
        레알 마드리드 <small>(11명)</small>
                </span></span>
            </span>
            <span class="td formation">4-1-2-3</span>
        </div>
        <div class="tr">
            <span class="td rank_no">2</span>
            <span class="td rank_coach">
                <span class="coach_wrap"><span class="name profile_pointer" data-sn="2">루하</span></span>
                <span class="price" title="37,455,370,000">374억</span>
            </span>
            <span class="td team_color"><span class="name"><span class="inner">레알 마드리드 <small>(11명)</small></span></span></span>
            <span class="td formation">4-2-4</span>
        </div>
        """;

    [Fact]
    public void Reads_team_colour_and_formation_of_ranking_rows()
    {
        var rows = RankingParser.Rows(RankingRow);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new RankRow(1, "Gucci보이", 10_465_340_000, "레알 마드리드", 11, "4-1-2-3"), rows[0]);

        var rate = Assert.Single(RankingParser.PickRates(rows));
        Assert.Equal(("레알 마드리드", 2, 1.0), (rate.TeamColor, rate.Rankers, rate.Share));
        Assert.Equal("루하", rate.Richest.Nickname);
        Assert.Equal(23_960_355_000, rate.AverageValue);
    }

    [Fact]
    public void Team_players_count_the_rankers_of_that_colour()
    {
        RankerSquad Squad(string nick, long st) => new(0, nick, 0, [new RankerSquadPlayer(st, 11, "ST"), new RankerSquadPlayer(900, 8, "GK")]);
        var squads = new[] { Squad("a", 100), Squad("b", 100), Squad("c", 200), Squad("d", 100) };
        var colorOf = new Dictionary<string, string> { ["a"] = "맨유", ["b"] = "맨유", ["c"] = "맨유", ["d"] = "아스널" };

        var team = ManagerAnalysisMath.TeamPlayers("맨유", squads, colorOf, id => ($"p{id}", "S"));

        Assert.Equal(3, team.Squads);
        var st = team.ByRole["ST"];
        Assert.Equal((100L, 2, 2 / 3.0), (st[0].SpId, st[0].Users, st[0].Share));
        Assert.Equal(11, st[0].Grade);
    }

    private static MatchPlayer P(int pos, int goals = 0, int tackles = 0) => new()
    {
        SpId = 100 + pos, SpPosition = pos, SpGrade = 11, Status = new PlayerStatus { Goal = goals, Tackle = tackles, PassTry = 10, PassSuccess = 8, SpRating = 7 },
    };

    [Fact]
    public void Formation_lines_follow_defence_holding_midfield_attacking_forwards()
    {
        // GK, LB, LCB, RCB, RB, CDM, LM, LCM, RCM, RM, CAM: 4-1-4-1-0
        Assert.Equal("4-1-4-1-0", ManagerAnalysis.Lines([P(0), P(7), P(6), P(4), P(3), P(10), P(16), P(15), P(13), P(12), P(18)]));
        Assert.Equal("4-2-0-2-2", ManagerAnalysis.Lines([P(0), P(7), P(6), P(4), P(3), P(11), P(9), P(19), P(17), P(26), P(24)]));
    }

    [Fact]
    public void Coach_report_counts_record_goals_and_per_card_numbers()
    {
        MatchDetail Match(string result, int goalsFor, int goalsAgainst) => new()
        {
            MatchId = Guid.NewGuid().ToString(), MatchType = 52,
            MatchInfo =
            [
                new MatchInfo { Ouid = "me", MatchDetail = new MatchSideDetail { MatchResult = result }, Shoot = new ShootSummary { GoalTotalDisplay = goalsFor },
                    Player = [P(25, goals: goalsFor), P(5, tackles: 3)] },
                new MatchInfo { Ouid = "them", Shoot = new ShootSummary { GoalTotalDisplay = goalsAgainst } },
            ],
        };
        var report = ManagerAnalysis.Build("나", "me", [Match("승", 2, 1), Match("무", 1, 1), Match("패", 0, 2)], new Dictionary<int, string> { [125] = "공격수" });

        Assert.Equal((3, 1, 1, 1), (report.Games, report.Wins, report.Draws, report.Losses));
        Assert.Equal(1.0, report.GoalsFor, 3);
        Assert.Equal(4 / 3.0, report.GoalsAgainst, 3);
        var striker = report.Players.Single(p => p.Name == "공격수");
        Assert.Equal(("ST", 3, 1.0), (striker.Position, striker.Games, striker.Goals));
        Assert.Equal(0.8, striker.PassRate, 3);
        Assert.Equal(6.0, report.Players.Single(p => p.Position == "CB").Defence, 3); // tackles 3 × 2
    }
}
