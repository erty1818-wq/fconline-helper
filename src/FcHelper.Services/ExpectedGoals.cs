using FcHelper.Core;

namespace FcHelper.Services;

/// <summary>
/// A small expected-goals model [추정] for the 슈팅 tab (OS-07): the chance a shot goes in from where it was taken.
/// Logistic regression on distance to the goal centre, the angle the goal mouth opens and whether it was a header,
/// fitted on 4,285 cached official-match shots (penalties left out), 2026-09-26:
/// held-out log-loss 0.597 against 0.681 for a flat rate. FC Online scores far more than real football
/// (43% of non-penalty shots go in), so real-football xG tables would not fit.
/// Penalties get their observed rate instead (43 of 55).
/// </summary>
public static class ExpectedGoals
{
    public const double Intercept = 0.33, PerMetre = -0.110, PerRadian = 2.25, Header = -1.47;
    public const double Penalty = 0.78;

    /// <param name="x">API x (1 = the goal being attacked).</param>
    /// <param name="y">API y (0.5 = centre).</param>
    public static double Of(double x, double y, int type)
    {
        if (type == ShotTypes.Penalty) return Penalty;
        var (distance, angle) = Geometry(x, y);
        var z = Intercept + PerMetre * distance + PerRadian * angle + (type == ShotTypes.Header ? Header : 0);
        return 1 / (1 + Math.Exp(-z));
    }

    /// <summary>Metres to the goal centre and the angle (radians) between the posts, on a 105 × 68 m pitch.</summary>
    public static (double Distance, double Angle) Geometry(double x, double y)
    {
        var dx = (1 - Math.Clamp(x, 0, 1)) * 105;
        var dy = (y - 0.5) * 68;
        const double half = 7.32 / 2;
        var angle = Math.Atan2(7.32 * dx, dx * dx + dy * dy - half * half);
        return (Math.Sqrt(dx * dx + dy * dy), angle < 0 ? angle + Math.PI : angle);
    }
}

/// <summary>Attempts, goals and xG for one shot type [직접, xG 추정].</summary>
public sealed record ShotTypeLine(int Type, int Shots, int OnTarget, int Goals, double Xg)
{
    public double Conversion => Shots == 0 ? 0 : (double)Goals / Shots;

    public static IReadOnlyList<ShotTypeLine> Of(ShotMap map) => map.Dots
        .GroupBy(d => d.Type)
        .Select(g => new ShotTypeLine(g.Key, g.Count(), g.Count(d => d.Result is ShotResult.Goal or ShotResult.OnTarget),
            g.Count(d => d.Result == ShotResult.Goal), g.Sum(d => d.Xg)))
        .OrderByDescending(l => l.Shots).ThenBy(l => l.Type)
        .ToList();
}
