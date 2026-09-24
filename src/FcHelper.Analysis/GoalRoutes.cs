using FcHelper.Core;
using FcHelper.Core.Models;

namespace FcHelper.Analysis;

/// <summary>
/// Guesses how an assisted goal was built from the assist and shot coordinates. The API records no such
/// thing, so anything derived from here is <see cref="Evidence.Inferred"/>.
/// </summary>
public static class GoalRoutes
{
    /// <summary>Pulled back from near the byline, finished centrally with the foot.</summary>
    public static bool IsCutback(ShootDetail s) =>
        Pitch.IsBylineWide(s.AssistX, s.AssistY)
        && Pitch.ZoneOf(s.X, s.Y, s.InPenalty) is GoalZone.SixYardBox or GoalZone.BoxCenter
        && s.Type != ShotTypes.Header;

    /// <summary>Delivered from a wide lane, finished with a header.</summary>
    public static bool IsCrossHeader(ShootDetail s) =>
        Pitch.IsWide(s.AssistY) && s.AssistX >= 0.6 && s.Type == ShotTypes.Header;

    /// <summary>Played from the central lanes between midfield and the box, finished inside the box with the foot.</summary>
    public static bool IsThroughRun(ShootDetail s) =>
        !Pitch.IsWide(s.AssistY)
        && s.AssistX is >= 0.5 and < 1 - Pitch.BoxDepth
        && s.InPenalty
        && s.Type != ShotTypes.Header;
}
