using FcHelper.Core;
using FcHelper.Core.Models;

namespace FcHelper.Services;

/// <summary>One card of a user's 감독모드 matches: how it did per game while on the pitch.</summary>
/// <param name="Attack">
/// 공격 기여 [계산] per game: goals ×10, assists ×7, shots on target ×2, other shots ×0.5, dribbles won ×0.5, passes
/// completed ×0.05. A simple, visible formula, not fc-info's index.
/// </param>
/// <param name="Defence">수비 기여 [계산] per game: tackles ×2, blocks ×3, interceptions ×2, aerial duels won ×1, defending actions ×0.5.</param>
public sealed record ManagerPlayer(int SpId, string Name, string Position, int Grade, int Games, double WinRate,
    double Attack, double Defence, double Goals, double Assists, double ShotsOnTarget, double PassRate, double Tackles, double Blocks,
    double Interceptions, double Rating);

/// <summary>A formation as rows of defenders–holding–midfield–attacking midfield–forwards (fc-info's 4-1-4-1-0 style) and its record.</summary>
public sealed record ManagerFormation(string Lines, int Games, int Wins, int Draws, int Losses)
{
    public double WinRate => Games == 0 ? 0 : Wins / (double)Games;
}

public sealed record ManagerReport(string Nickname, int Games, int Wins, int Draws, int Losses, double GoalsFor, double GoalsAgainst,
    IReadOnlyList<ManagerPlayer> Players, IReadOnlyList<ManagerFormation> Formations)
{
    public double WinRate => Games == 0 ? 0 : Wins / (double)Games;
}

public static class ManagerAnalysis
{
    public const int ManagerMatch = 52;

    /// <summary>
    /// Rows of a lineup: defenders, holding mids, midfield (incl. wide mids), attacking mids, forwards (incl. wingers).
    /// </summary>
    public static string Lines(IEnumerable<MatchPlayer> starters)
    {
        int df = 0, dm = 0, mf = 0, am = 0, fw = 0;
        foreach (var p in starters.Where(p => !p.IsSubstitute && p.SpPosition != 0))
        {
            switch (Positions.Label(p.SpPosition))
            {
                case "SW" or "RWB" or "RB" or "RCB" or "CB" or "LCB" or "LB" or "LWB": df++; break;
                case "RDM" or "CDM" or "LDM": dm++; break;
                case "RM" or "RCM" or "CM" or "LCM" or "LM": mf++; break;
                case "RAM" or "CAM" or "LAM": am++; break;
                default: fw++; break;
            }
        }
        return $"{df}-{dm}-{mf}-{am}-{fw}";
    }

    public static ManagerReport Build(string nickname, string ouid, IEnumerable<MatchDetail> matches, IReadOnlyDictionary<int, string> names)
    {
        var sides = matches.Select(m => (Mine: m.SideOf(ouid), Theirs: m.OpponentOf(ouid))).Where(x => x.Mine is not null).ToList();
        var games = sides.Count;
        static char Result(MatchInfo side) => side.MatchDetail.MatchResult switch { "승" => 'W', "무" => 'D', _ => 'L' };
        var wins = sides.Count(x => Result(x.Mine!) == 'W');
        var draws = sides.Count(x => Result(x.Mine!) == 'D');

        var players = sides.Where(x => x.Mine!.HasStats)
            .SelectMany(x => x.Mine!.Player.Where(p => !p.IsSubstitute).Select(p => (p, Win: Result(x.Mine!) == 'W')))
            .GroupBy(x => x.p.SpId)
            .Select(g =>
            {
                var n = g.Count();
                double Avg(Func<PlayerStatus, double> f) => g.Average(x => f(x.p.Status));
                var passTry = g.Sum(x => x.p.Status.PassTry);
                var position = g.GroupBy(x => Positions.Label(x.p.SpPosition)).MaxBy(x => x.Count())!.Key;
                var grade = g.GroupBy(x => x.p.SpGrade).MaxBy(x => x.Count())!.Key;
                return new ManagerPlayer(g.Key, names.GetValueOrDefault(g.Key, g.Key.ToString()), position, grade, n, g.Count(x => x.Win) / (double)n,
                    Avg(s => s.Goal * 10 + s.Assist * 7 + s.EffectiveShoot * 2 + (s.Shoot - s.EffectiveShoot) * 0.5 + s.DribbleSuccess * 0.5 + s.PassSuccess * 0.05),
                    Avg(s => s.Tackle * 2 + s.Block * 3 + s.Intercept * 2 + s.AerialSuccess + s.Defending * 0.5),
                    Avg(s => s.Goal), Avg(s => s.Assist), Avg(s => s.EffectiveShoot),
                    passTry == 0 ? 0 : g.Sum(x => x.p.Status.PassSuccess) / (double)passTry,
                    Avg(s => s.Tackle), Avg(s => s.Block), Avg(s => s.Intercept), Avg(s => s.SpRating));
            })
            .OrderByDescending(p => p.Games).ThenByDescending(p => p.Attack).ToList();

        var formations = sides.Where(x => x.Mine!.HasStats)
            .GroupBy(x => Lines(x.Mine!.Player))
            .Select(g => new ManagerFormation(g.Key, g.Count(), g.Count(x => Result(x.Mine!) == 'W'), g.Count(x => Result(x.Mine!) == 'D'),
                g.Count(x => Result(x.Mine!) == 'L')))
            .OrderByDescending(f => f.Games).ToList();

        return new ManagerReport(nickname, games, wins, draws, games - wins - draws,
            games == 0 ? 0 : sides.Average(x => x.Mine!.Shoot.GoalTotalDisplay),
            games == 0 ? 0 : sides.Average(x => x.Theirs?.Shoot.GoalTotalDisplay ?? 0), players, formations);
    }
}
