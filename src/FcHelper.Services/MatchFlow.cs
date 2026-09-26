using FcHelper.Core;
using FcHelper.Core.Models;

namespace FcHelper.Services;

/// <summary>
/// Per-match averages for the 흐름 tab's top line (OS-08), from matchDetail, shoot, pass and player status.
/// Forfeits carry no stats and are left out.
/// </summary>
public sealed record StatLine(int Matches, double Rating, double Goals, double Assists, double Possession, double ShotAccuracy, double PassAccuracy)
{
    public static StatLine Of(IEnumerable<MatchDetail> matches, string ouid)
    {
        var sides = matches.Select(m => m.SideOf(ouid)).Where(s => s is { HasStats: true }).Cast<MatchInfo>().ToList();
        if (sides.Count == 0) return new StatLine(0, 0, 0, 0, 0, 0, 0);
        var shots = sides.Sum(s => s.Shoot.ShootTotal);
        var passes = sides.Sum(s => s.Pass.PassTry);
        return new StatLine(
            sides.Count,
            // matchDetail.averageRating is not the players' average (real values sit near 4); the players' own ratings are.
            sides.Average(s => s.Player.Where(p => p.Status.SpRating > 0).Select(p => p.Status.SpRating).DefaultIfEmpty(0).Average()),
            sides.Average(s => s.Shoot.GoalTotal),
            sides.Average(s => s.Player.Sum(p => p.Status.Assist)),
            sides.Average(s => s.MatchDetail.Possession),
            shots == 0 ? 0 : (double)sides.Sum(s => s.Shoot.EffectiveShootTotal) / shots,
            passes == 0 ? 0 : (double)sides.Sum(s => s.Pass.PassSuccess) / passes);
    }
}

/// <summary>Where a shot's time goes on a 0–90 axis with the stoppage times folded in.</summary>
public static class MatchClock
{
    /// <summary>Axis position of the "90+" end: second-half stoppage and extra time.</summary>
    public const double Late = 96;

    /// <summary>
    /// First-half stoppage stays at 45 (it happened before the second half), second-half stoppage and extra time go to
    /// <see cref="Late"/>. Penalty shootouts are not open play and give null.
    /// </summary>
    public static double? Place(long goalTime)
    {
        var period = GoalTime.PeriodOf(goalTime);
        var minute = GoalTime.ToSeconds(goalTime) / 60.0;
        return period switch
        {
            MatchPeriod.FirstHalf => Math.Min(minute, 45),
            MatchPeriod.SecondHalf => minute >= 90 ? Late : minute,
            MatchPeriod.ExtraFirstHalf or MatchPeriod.ExtraSecondHalf => Late,
            _ => null,
        };
    }
}
