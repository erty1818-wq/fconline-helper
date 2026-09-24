namespace FcHelper.Core;

public enum Lane { LeftWing, LeftHalfSpace, Center, RightHalfSpace, RightWing }

public enum GoalZone
{
    SixYardBox,
    BoxLeft,
    BoxCenter,
    BoxRight,
    LongLeft,
    LongCenter,
    LongRight,
    Far,
}

/// <summary>
/// Classifies API coordinates (0..1 on the full pitch) into zones. Verified on real samples (docs/PLANNING.md 3.4):
/// each side's coordinates are normalised so x = 1 is the goal it attacks, and small y is the attacker's left
/// (left-sided players shoot and assist from low y). The API's inPenalty flag flips exactly at x = 1 - 16.5/105.
/// </summary>
public static class Pitch
{
    // Real pitch proportions: 105m x 68m, penalty box 16.5m deep x 40.32m wide, six-yard box 5.5m x 18.32m.
    public const double BoxDepth = 16.5 / 105;
    public const double BoxHalfWidth = 20.16 / 68;
    public const double SixYardDepth = 5.5 / 105;
    public const double SixYardHalfWidth = 9.16 / 68;
    /// <summary>Shots from further out than this are counted as "far" rather than long range.</summary>
    public const double LongRangeDepth = 35.0 / 105;

    private static double Y(double y) => Clamp(y);
    private static double Clamp(double v) => Math.Clamp(v, 0, 1);

    public static Lane LaneOf(double y)
    {
        var v = Y(y);
        return v switch
        {
            < 0.2 => Lane.LeftWing,
            < 0.4 => Lane.LeftHalfSpace,
            <= 0.6 => Lane.Center,
            <= 0.8 => Lane.RightHalfSpace,
            _ => Lane.RightWing,
        };
    }

    public static bool IsInBox(double x, double y) =>
        Clamp(x) >= 1 - BoxDepth && Math.Abs(Y(y) - 0.5) <= BoxHalfWidth;

    public static bool IsInSixYardBox(double x, double y) =>
        Clamp(x) >= 1 - SixYardDepth && Math.Abs(Y(y) - 0.5) <= SixYardHalfWidth;

    /// <param name="inPenalty">The API's own in-box flag, which wins over the coordinate test when given.</param>
    public static GoalZone ZoneOf(double x, double y, bool? inPenalty = null)
    {
        var inBox = inPenalty ?? IsInBox(x, y);
        var v = Y(y);
        // Box and long-range zones split into thirds around the goal's width.
        var side = v < 0.5 - 0.1 ? -1 : v > 0.5 + 0.1 ? 1 : 0;

        if (inBox)
        {
            if (IsInSixYardBox(x, y)) return GoalZone.SixYardBox;
            return side switch { < 0 => GoalZone.BoxLeft, > 0 => GoalZone.BoxRight, _ => GoalZone.BoxCenter };
        }

        if (Clamp(x) < 1 - LongRangeDepth) return GoalZone.Far;
        return side switch { < 0 => GoalZone.LongLeft, > 0 => GoalZone.LongRight, _ => GoalZone.LongCenter };
    }

    public static bool IsLongRange(GoalZone zone) => zone is GoalZone.LongLeft or GoalZone.LongCenter or GoalZone.LongRight or GoalZone.Far;

    /// <summary>Close to the byline and wide of the six-yard box: where cut-backs usually start.</summary>
    public static bool IsBylineWide(double x, double y) =>
        Clamp(x) >= 1 - SixYardDepth * 1.5 && Math.Abs(Y(y) - 0.5) > SixYardHalfWidth;

    public static bool IsWide(double y) => LaneOf(y) is Lane.LeftWing or Lane.RightWing;

    public static string Label(GoalZone zone) => zone switch
    {
        GoalZone.SixYardBox => "골문 앞",
        GoalZone.BoxLeft => "박스 좌측",
        GoalZone.BoxCenter => "박스 중앙",
        GoalZone.BoxRight => "박스 우측",
        GoalZone.LongLeft => "좌측 중거리",
        GoalZone.LongCenter => "중앙 중거리",
        GoalZone.LongRight => "우측 중거리",
        _ => "먼 거리",
    };

    public static string Label(Lane lane) => lane switch
    {
        Lane.LeftWing => "좌측면",
        Lane.LeftHalfSpace => "좌측 하프스페이스",
        Lane.Center => "중앙",
        Lane.RightHalfSpace => "우측 하프스페이스",
        _ => "우측면",
    };
}
