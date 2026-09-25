using FcHelper.Market;

namespace FcHelper.Tests;

public class OvrFormulaTests
{
    /// <summary>데이비드 시먼 ICON (101000001) at +1 as the data center showed it on 2026-09-25.</summary>
    private static readonly Dictionary<string, int> SeamanStats = new()
    {
        ["속력"] = 59, ["가속력"] = 76, ["골 결정력"] = 53, ["슛 파워"] = 50, ["중거리 슛"] = 44, ["위치 선정"] = 40, ["발리슛"] = 50, ["페널티 킥"] = 55,
        ["짧은 패스"] = 55, ["시야"] = 85, ["크로스"] = 50, ["긴 패스"] = 56, ["프리킥"] = 54, ["커브"] = 43, ["드리블"] = 54, ["볼 컨트롤"] = 53,
        ["민첩성"] = 78, ["밸런스"] = 87, ["반응 속도"] = 106, ["대인 수비"] = 46, ["태클"] = 37, ["가로채기"] = 60, ["헤더"] = 43, ["슬라이딩 태클"] = 40,
        ["몸싸움"] = 90, ["스태미너"] = 75, ["적극성"] = 56, ["점프"] = 90, ["침착성"] = 101,
        ["GK 다이빙"] = 107, ["GK 핸들링"] = 108, ["GK 킥"] = 109, ["GK 반응속도"] = 107, ["GK 위치 선정"] = 106,
    };

    private static readonly Dictionary<string, int> SeamanPositions = new()
    {
        ["ST"] = 57, ["LW"] = 59, ["CF"] = 59, ["RW"] = 59, ["CAM"] = 61, ["LM"] = 61, ["CM"] = 61, ["RM"] = 61, ["CDM"] = 59,
        ["LWB"] = 58, ["CB"] = 55, ["RWB"] = 58, ["LB"] = 56, ["SW"] = 55, ["RB"] = 56, ["GK"] = 106,
    };

    private static readonly CardAbility Seaman = new(101000001, SeamanStats, SeamanPositions);

    private static MarketCard Card => new()
    {
        Group = "GK", SpId = 101000001, Name = "데이비드 시먼", Season = "ICON", Ovr1 = 106, Positions = SeamanPositions,
    };

    [Fact]
    public void Formula_reproduces_every_position_the_data_center_lists()
    {
        foreach (var (position, ovr) in SeamanPositions) Assert.Equal(ovr, OvrFormula.Ovr(position, SeamanStats));
    }

    [Fact]
    public void Weights_add_up_to_100_and_sides_match()
    {
        foreach (var p in new[] { "ST", "CF", "LW", "CAM", "LM", "CM", "CDM", "LWB", "LB", "CB", "SW", "GK" })
            Assert.Equal(100, OvrFormula.Of(p).Values.Sum());
        Assert.Equal(OvrFormula.Of("LB"), OvrFormula.Of("RB"));
        Assert.Equal(OvrFormula.Of("LW"), OvrFormula.Of("RW"));
        Assert.Equal(18, OvrFormula.Weight("ST", "골 결정력"));
        Assert.Equal(OvrFormula.Of("CB"), OvrFormula.Of("LCB")); // side variants share the rating
    }

    [Fact]
    public void Team_colour_detail_stats_move_only_the_positions_that_weigh_them()
    {
        // 잉글랜드 3단계 on the data center: every stat +3, 골 결정력 and 긴 패스 more → CAM and CM +4, the rest +3.
        var england = new TeamColorLevel(3, 5, 3, ["전체 능력치 +3", "골 결정력 +1", "긴 패스 +2"]);
        Assert.Equal(65, FinalOvrMath.Compute(Card, Seaman, "CAM", 1, 1, [england]).Value);
        Assert.Equal(65, FinalOvrMath.Compute(Card, Seaman, "CM", 1, 1, [england]).Value);
        Assert.Equal(60, FinalOvrMath.Compute(Card, Seaman, "ST", 1, 1, [england]).Value);
        var gk = FinalOvrMath.Compute(Card, Seaman, "GK", 1, 1, [england]);
        Assert.Equal(109, gk.Value);
        Assert.True(gk.Exact);
    }

    [Fact]
    public void Grade_and_adaptability_add_to_every_stat()
    {
        // Data center: +8 gives 121 and 적응도 5 adds 4 more.
        var f = FinalOvrMath.Compute(Card, Seaman, "GK", 8, 5, []);
        Assert.Equal(106 + 15 + 4, f.Value);
        Assert.Equal(15, f.Grade);
        Assert.Equal(4, f.Adaptability);
    }

    [Fact]
    public void Without_stats_the_detail_bonus_is_rounded_down_and_marked()
    {
        var england = new TeamColorLevel(3, 5, 3, ["전체 능력치 +3", "골 결정력 +1", "긴 패스 +2"]);
        var f = FinalOvrMath.Compute(Card, null, "CAM", 1, 1, [england]);
        Assert.False(f.Exact);
        Assert.Equal(64, f.Value); // the exact figure is 65: at most one short
    }

    [Fact]
    public void Training_raises_the_heaviest_stats()
    {
        var before = FinalOvrMath.Compute(Card, Seaman, "ST", 8, 5, []);
        var plan = FinalOvrMath.Training("ST", 8, before);
        Assert.Equal(5, plan.Stats.Count);
        Assert.Equal("골 결정력", plan.Stats[0].Stat);
        Assert.Equal(2 * (18 + 13 + 10 + 10 + 10), plan.Points);
        Assert.Equal(6, FinalOvrMath.Training("ST", 11).Stats.Count);
        var after = FinalOvrMath.Compute(Card, Seaman, "ST", 8, 5, [], plan.AsBonus);
        Assert.Equal(before.Value + plan.Gain, after.Value);
        Assert.Equal(plan.Gain, after.Training);
        // The cheapest steps to the next OVR are worth at least the points missing.
        Assert.True(plan.Cheapest.Sum(c => c.Plus * OvrFormula.Weight("ST", c.Stat)) >= before.PointsToNext);
    }

    [Fact]
    public void Ability_fragment_is_parsed()
    {
        const string html = """
            <div class="ovr_set">
                <div class="position st value">57</div>
                <div class="position gk value">106</div>
            </div>
            <li class="ab" data-positon=",12,13,">
                <div class="txt">골 결정력</div>
                <div class="value over50">
                    53 <span class="diff"></span>
                </div>
            </li>
            <script>var x = '<div class="txt">가짜</div>';</script>
            """;
        var a = AbilityParser.Parse(1, html)!;
        Assert.Equal(57, a.Positions["ST"]);
        Assert.Equal(106, a.Positions["GK"]);
        Assert.Equal(53, a.Stats["골 결정력"]);
        Assert.Single(a.Stats);
    }

    [Fact]
    public void Face_list_maps_to_cdn_urls()
    {
        var faces = FaceClient.Parse("""[{"spid":101000001,"n1Custom":1,"season":"ICON"},{"spid":207000001,"n1Custom":4,"season":"TT"}]""");
        Assert.Equal(2, faces.Count);
        Assert.EndsWith("/playersAction/p101000001.png", faces[0].Url);
        Assert.EndsWith("/players/p1.png", faces[1].Url);
        Assert.Empty(FaceClient.Parse("not json"));
    }
}
