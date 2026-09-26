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
        var games = matches.Where(m => m.SideOf(ouid) is { HasStats: true }).ToList();
        var sides = games.Select(m => m.SideOf(ouid)!).ToList();
        if (sides.Count == 0) return new StatLine(0, 0, 0, 0, 0, 0, 0);
        var shots = sides.Sum(s => s.Shoot.ShootTotal);
        var passes = sides.Sum(s => s.Pass.PassTry);
        return new StatLine(
            sides.Count,
            // matchDetail.averageRating is not the players' average (real values sit near 4); the players' own ratings are.
            sides.Average(s => s.Player.Where(p => p.Status.SpRating > 0).Select(p => p.Status.SpRating).DefaultIfEmpty(0).Average()),
            // Same rule as the summary card: own goals count for the other side.
            games.Average(m => m.SideOf(ouid)!.Shoot.GoalTotal + (m.OpponentOf(ouid)?.Shoot.OwnGoal ?? 0)),
            sides.Average(s => s.Player.Sum(p => p.Status.Assist)),
            sides.Average(s => s.MatchDetail.Possession),
            shots == 0 ? 0 : (double)sides.Sum(s => s.Shoot.EffectiveShootTotal) / shots,
            passes == 0 ? 0 : (double)sides.Sum(s => s.Pass.PassSuccess) / passes);
    }
}

/// <summary>Goals scored and conceded in one quarter-hour (흐름 tab, OS-09) [직접].</summary>
public sealed record TimeBucket(string Label, int For, int Against);

public static class TimeBuckets
{
    /// <summary>Goals in each of <see cref="MatchClock.BucketLabels"/>; shootout goals are not open play and are left out.</summary>
    public static IReadOnlyList<TimeBucket> Of(ShotMap taken, ShotMap allowed)
    {
        int[] Count(ShotMap map)
        {
            var counts = new int[MatchClock.BucketLabels.Length];
            foreach (var d in map.Dots.Where(d => d.Result == ShotResult.Goal))
                if (MatchClock.BucketOf(d.GoalTime) is { } b) counts[b]++;
            return counts;
        }
        var f = Count(taken);
        var a = Count(allowed);
        return MatchClock.BucketLabels.Select((l, i) => new TimeBucket(l, f[i], a[i])).ToList();
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
    public static readonly string[] BucketLabels = ["0-15", "15-30", "30-45", "45-60", "60-75", "75-90", "90+"];

    /// <summary>
    /// Quarter-hour bucket (index into <see cref="BucketLabels"/>): first-half stoppage belongs to 30-45,
    /// second-half stoppage and extra time to 90+. Penalty shootouts give null.
    /// </summary>
    public static int? BucketOf(long goalTime)
    {
        var period = GoalTime.PeriodOf(goalTime);
        var minute = GoalTime.ToSeconds(goalTime) / 60.0;
        return period switch
        {
            MatchPeriod.FirstHalf => Math.Min((int)(minute / 15), 2),
            MatchPeriod.SecondHalf => minute >= 90 ? 6 : Math.Clamp((int)(minute / 15), 3, 5),
            MatchPeriod.ExtraFirstHalf or MatchPeriod.ExtraSecondHalf => 6,
            _ => null,
        };
    }

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
