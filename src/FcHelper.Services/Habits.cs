using FcHelper.Core.Models;

namespace FcHelper.Services;

/// <summary>
/// How a manager plays (흐름 tab, OS-10): forfeits, pauses, and when they play, in Korean time.
/// matchDate carries no zone; it is read as UTC (docs/opponent-search/PROGRESS.md explains why) and shifted by +9.
/// </summary>
public sealed record Habits(int Matches, int ForfeitLosses, int ForfeitWins, double AvgPauses, int[] ByHour, int[] ByDay)
{
    public static readonly TimeSpan Kst = TimeSpan.FromHours(9);
    /// <summary>Monday first, the way Korean calendars read.</summary>
    public static readonly string[] DayLabels = ["월", "화", "수", "목", "금", "토", "일"];

    public static Habits Of(IEnumerable<MatchDetail> matches, string ouid)
    {
        var byHour = new int[24];
        var byDay = new int[7];
        int played = 0, lost = 0, won = 0, pausedIn = 0;
        double pauses = 0;
        foreach (var m in matches)
        {
            if (m.SideOf(ouid) is not { } side) continue;
            played++;
            if (side.MatchDetail.MatchEndType == 2) lost++;
            else if (side.MatchDetail.MatchEndType == 1) won++;
            if (side.HasStats) { pauses += side.MatchDetail.SystemPause; pausedIn++; }
            var kst = Korean(m.MatchDate);
            byHour[kst.Hour]++;
            byDay[((int)kst.DayOfWeek + 6) % 7]++;
        }
        return new Habits(played, lost, won, pausedIn == 0 ? 0 : pauses / pausedIn, byHour, byDay);
    }

    public static DateTime Korean(DateTime matchDate) =>
        (matchDate.Kind == DateTimeKind.Local ? matchDate.ToUniversalTime() : matchDate) + Kst;

    public double ForfeitLossRate => Matches == 0 ? 0 : (double)ForfeitLosses / Matches;

    /// <summary>The busiest block of <paramref name="width"/> consecutive hours (wrapping past midnight): start hour and matches in it.</summary>
    public (int Start, int Count) PeakHours(int width = 3)
    {
        var best = (Start: 0, Count: -1);
        for (var h = 0; h < 24; h++)
        {
            var n = Enumerable.Range(h, width).Sum(i => ByHour[i % 24]);
            if (n > best.Count) best = (h, n);
        }
        return best;
    }
}
