namespace FcHelper.Core;

public enum MatchPeriod { FirstHalf, SecondHalf, ExtraFirstHalf, ExtraSecondHalf, PenaltyShootout }

/// <summary>
/// Decodes <c>shootDetail.goalTime</c>. The upper bits (units of 2^24) carry the period and the
/// lower bits the seconds elapsed within it (NEXON "매치 정보 관련 변환식" notice).
/// </summary>
public static class GoalTime
{
    private const long PeriodUnit = 1L << 24;
    private static readonly int[] PeriodStartSeconds = [0, 45 * 60, 90 * 60, 105 * 60, 120 * 60];

    public static MatchPeriod PeriodOf(long encoded) =>
        (MatchPeriod)Math.Clamp(encoded / PeriodUnit, 0, PeriodStartSeconds.Length - 1);

    /// <summary>Seconds since kickoff on the match clock (second half starts at 45:00).</summary>
    public static int ToSeconds(long encoded)
    {
        if (encoded < 0) return 0;
        var period = (int)PeriodOf(encoded);
        return (int)(encoded - period * PeriodUnit) + PeriodStartSeconds[period];
    }

    public static int ToMinute(long encoded) => ToSeconds(encoded) / 60;
}
