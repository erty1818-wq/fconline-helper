using FcHelper.Core;
using FcHelper.Core.Models;

namespace FcHelper.Services;

/// <summary>shootDetail.result, checked on 734 cached match sides (docs/opponent-search/PROGRESS.md).</summary>
public enum ShotResult { OnTarget = 1, OffTarget = 2, Goal = 3 }

/// <summary>
/// One shot on the half-pitch picture: <see cref="Across"/> 0 = the shooter's left touchline … 1 = right,
/// <see cref="Down"/> 0 = the goal line being attacked … 1 = halfway line.
/// </summary>
public sealed record ShotDot(double Across, double Down, ShotResult Result, int Type, int SpId, int Minute, string MatchId, bool InBox, double Xg, double? Clock);

/// <summary>
/// The shots a manager took (or allowed) over their matches, for the 슈팅 tab. Straight from shootDetail (직접);
/// coordinates are per shooter, x = 1 at the goal they attack and small y on their left (Core/Pitch.cs).
/// </summary>
public sealed record ShotMap(IReadOnlyList<ShotDot> Dots, int Matches)
{
    public int Shots => Dots.Count;
    public int Goals => Dots.Count(d => d.Result == ShotResult.Goal);
    public int OnTarget => Dots.Count(d => d.Result is ShotResult.Goal or ShotResult.OnTarget);
    public double PerMatch => Matches == 0 ? 0 : (double)Shots / Matches;
    /// <summary>Sum of the shots' expected goals [추정].</summary>
    public double Xg => Dots.Sum(d => d.Xg);

    /// <param name="conceded">False: shots <paramref name="ouid"/> took. True: shots their opponents took against them.</param>
    public static ShotMap Of(IEnumerable<MatchDetail> matches, string ouid, bool conceded)
    {
        var dots = new List<ShotDot>();
        var played = 0;
        foreach (var m in matches)
        {
            var side = conceded ? m.OpponentOf(ouid) : m.SideOf(ouid);
            // Forfeits carry no shot data; they would only pull the per-match average down.
            if (side is null || m.SideOf(ouid) is not { HasStats: true }) continue;
            played++;
            foreach (var s in side.ShootDetail)
            {
                if (s.Result is < 1 or > 3) continue;
                var (across, down) = ToHalfPitch(s.X, s.Y);
                dots.Add(new ShotDot(across, down, (ShotResult)s.Result, s.Type, s.SpId, GoalTime.ToMinute(s.GoalTime), m.MatchId, Pitch.IsInBox(s.X, s.Y), ExpectedGoals.Of(s.X, s.Y, s.Type), MatchClock.Place(s.GoalTime)));
            }
        }
        return new ShotMap(dots, played);
    }

    /// <summary>API coordinates to the picture: the attacked goal on top, the shooter's left on the left.
    /// Shots from the own half (rare) sit on the halfway line.</summary>
    public static (double Across, double Down) ToHalfPitch(double x, double y) =>
        (Math.Clamp(y, 0, 1), Math.Clamp((1 - x) * 2, 0, 1));
}
