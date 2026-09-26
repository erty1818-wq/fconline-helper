using FcHelper.Core.Models;

namespace FcHelper.Services;

/// <summary>One comparable number: its name, value, how to print it, and whether more is better.</summary>
public sealed record ProfileMetric(string Label, double Value, string Format, bool HigherIsBetter = true)
{
    public string Display => Format switch
    {
        "%" => $"{Value * 100:0}%",
        "p" => $"{Value:0}%",
        _ => Value.ToString(Format),
    };
}

/// <summary>
/// A manager's per-match averages side by side with someone else's (비교 tab, OS-11; OS-16 reuses it for two managers).
/// From shoot, pass, defence and player status of matches with stats [직접, averages 계산].
/// </summary>
public sealed record Profile(int Matches, IReadOnlyList<ProfileMetric> Metrics)
{
    public static Profile Of(IEnumerable<MatchDetail> matches, string ouid)
    {
        var games = matches
            .Select(m => (Me: m.SideOf(ouid), Them: m.OpponentOf(ouid)))
            .Where(x => x.Me is { HasStats: true })
            .Select(x => (Me: x.Me!, x.Them))
            .ToList();
        if (games.Count == 0) return new Profile(0, []);

        double Avg(Func<MatchInfo, double> f) => games.Average(g => f(g.Me));
        double Rate(Func<MatchInfo, int> made, Func<MatchInfo, int> tried)
        {
            var t = games.Sum(g => tried(g.Me));
            return t == 0 ? 0 : (double)games.Sum(g => made(g.Me)) / t;
        }

        return new Profile(games.Count,
        [
            new("점유율", Avg(s => s.MatchDetail.Possession), "p"),
            new("경기당 슈팅", Avg(s => s.Shoot.ShootTotal), "0.0"),
            new("경기당 유효 슈팅", Avg(s => s.Shoot.EffectiveShootTotal), "0.0"),
            new("슈팅 정확도", Rate(s => s.Shoot.EffectiveShootTotal, s => s.Shoot.ShootTotal), "%"),
            new("패스 성공률", Rate(s => s.Pass.PassSuccess, s => s.Pass.PassTry), "%"),
            new("태클 성공률", Rate(s => s.Defence.TackleSuccess, s => s.Defence.TackleTry), "%"),
            new("경기당 가로채기", Avg(s => s.Player.Sum(p => p.Status.Intercept)), "0.0"),
            // Same rule as the summary card: own goals count for the other side.
            new("평균 득점", games.Average(g => g.Me.Shoot.GoalTotal + (g.Them?.Shoot.OwnGoal ?? 0)), "0.00"),
            new("평균 실점", games.Average(g => (g.Them?.Shoot.GoalTotal ?? 0) + g.Me.Shoot.OwnGoal), "0.00", HigherIsBetter: false),
        ]);
    }
}
