using FcHelper.Core.Models;

namespace FcHelper.Services;

/// <summary>
/// The last results of one manager, newest first, and the run they are on now
/// (e.g. 3 wins in a row). Straight from matchResult, so it is 직접 data.
/// </summary>
public sealed record RecentForm(IReadOnlyList<MatchOutcome> Results, MatchOutcome Streak, int StreakLength)
{
    public const int DefaultCount = 20;

    public static RecentForm Of(IEnumerable<MatchDetail> matches, string ouid, int count = DefaultCount)
    {
        var results = matches.OrderByDescending(m => m.MatchDate)
            .Select(m => m.SideOf(ouid)?.MatchDetail.Outcome ?? MatchOutcome.Unknown)
            .Where(o => o != MatchOutcome.Unknown)
            .Take(count)
            .ToList();
        if (results.Count == 0) return new RecentForm(results, MatchOutcome.Unknown, 0);
        var length = results.TakeWhile(o => o == results[0]).Count();
        return new RecentForm(results, results[0], length);
    }

    /// <summary>"3연승 중" and the like; empty below two in a row, where it says nothing.</summary>
    public string StreakText => StreakLength < 2 ? "" : Streak switch
    {
        MatchOutcome.Win => $"{StreakLength}연승 중",
        MatchOutcome.Loss => $"{StreakLength}연패 중",
        MatchOutcome.Draw => $"{StreakLength}경기 연속 무승부",
        _ => "",
    };
}

/// <summary>
/// Official division emblems. The data center names them ico_rank{n}.png, n being the division's place in
/// the division list sorted by id (800 슈퍼챔피언스 = 0 … 3100 유망주3 = 20); checked against the ranking page, 2026-09-26.
/// </summary>
public static class DivisionIcon
{
    private const string Base = "https://ssl.nexon.com/s2/game/fo4/obt/rank/large/update_2026/ico_rank";

    private static readonly int[] Order =
        [800, 900, 1000, 1100, 1200, 1300, 1700, 1800, 1900, 2000, 2100, 2200, 2300, 2400, 2500, 2600, 2700, 2800, 2900, 3000, 3100];

    /// <summary>The emblem URL, or null for an id outside the known list (the card then shows the name only).</summary>
    public static string? Url(int divisionId)
    {
        var i = Array.IndexOf(Order, divisionId);
        return i < 0 ? null : $"{Base}{i}.png";
    }
}
