using FcHelper.Core;
using FcHelper.Core.Models;

namespace FcHelper.Services;

/// <summary>One player's line on the 선수 tab: totals from player[].status and two per-match indices [계산].</summary>
public sealed record PlayerIndexLine(int SpId, string Position, int Apps, int Goals, int Assists, double Rating, double Attack, double Defence);

/// <summary>
/// Attack and defence indices per match played (OS-17) [계산]. Plain weighted sums of the official player status, so a
/// reader can redo them: attack = 3·goal + 2·assist + 1·shot on target + 0.5·dribble won; defence = tackle won +
/// interception + block + 0.5·aerial duel won. status.defending is left out: its meaning is not documented.
/// A player counts as having played when the match gave them a rating.
/// </summary>
public static class PlayerIndex
{
    public const double Goal = 3, Assist = 2, OnTarget = 1, Dribble = 0.5;
    public const double Tackle = 1, Intercept = 1, Block = 1, Aerial = 0.5;

    public static double AttackOf(PlayerStatus s) => Goal * s.Goal + Assist * s.Assist + OnTarget * s.EffectiveShoot + Dribble * s.DribbleSuccess;
    public static double DefenceOf(PlayerStatus s) => Tackle * s.Tackle + Intercept * s.Intercept + Block * s.Block + Aerial * s.AerialSuccess;

    public static IReadOnlyList<PlayerIndexLine> Of(IEnumerable<MatchDetail> matches, string ouid) =>
        matches
            .Select(m => m.SideOf(ouid))
            .Where(s => s is { HasStats: true })
            .SelectMany(s => s!.Player)
            .Where(p => p.Status.SpRating > 0)
            .GroupBy(p => p.SpId)
            .Select(g => new PlayerIndexLine(
                g.Key,
                // Where they usually played; subs who came on show as SUB.
                g.GroupBy(p => p.SpPosition).OrderByDescending(x => x.Count()).ThenBy(x => x.Key == Positions.Substitute).First().Key is var pos
                    ? Positions.Label(pos) : "",
                g.Count(),
                g.Sum(p => p.Status.Goal),
                g.Sum(p => p.Status.Assist),
                g.Average(p => p.Status.SpRating),
                g.Average(p => AttackOf(p.Status)),
                g.Average(p => DefenceOf(p.Status))))
            .OrderByDescending(l => l.Apps).ThenByDescending(l => l.Rating)
            .ToList();
}
