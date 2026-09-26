using FcHelper.Core;
using FcHelper.Core.Models;

namespace FcHelper.Services;

/// <summary>
/// How the win rate differs between the user's matches above and below the median of one stat
/// (e.g. "슈팅 정확도가 높은 경기 승률 64% vs 낮은 경기 41%") [추정: it shows a link, not a cause].
/// </summary>
public sealed record WinFactor(string Label, string Unit, double Median, int HighMatches, double HighWinRate, int LowMatches, double LowWinRate)
{
    /// <summary>Win-rate points gained in the high half over the low half.</summary>
    public double Gap => HighWinRate - LowWinRate;
}

/// <summary>
/// 내 전적 tab (OS-18). Split comparison rather than regression: with 30–100 matches a fitted coefficient swings a
/// lot, while "above vs below the median" can be checked by hand. Only stats a player can steer are used, not the
/// score itself. Needs <see cref="MinMatches"/> matches with records.
/// </summary>
public static class WinFactors
{
    public const int MinMatches = 30;
    /// <summary>Each half needs this many matches, or the stat is skipped (many equal values make lopsided halves).</summary>
    public const int MinPerHalf = 8;

    private static readonly (string Label, string Unit, Func<MatchInfo, double?> Value)[] Stats =
    [
        ("점유율", "%", s => s.MatchDetail.Possession),
        ("슈팅 수", "개", s => s.Shoot.ShootTotal),
        ("슈팅 정확도", "%", s => s.Shoot.ShootTotal == 0 ? null : 100.0 * s.Shoot.EffectiveShootTotal / s.Shoot.ShootTotal),
        ("박스 안 슈팅 비율", "%", s => s.ShootDetail.Count == 0 ? null : 100.0 * s.ShootDetail.Count(d => Pitch.IsInBox(d.X, d.Y)) / s.ShootDetail.Count),
        ("패스 성공률", "%", s => s.Pass.PassTry == 0 ? null : 100.0 * s.Pass.PassSuccess / s.Pass.PassTry),
        ("스루패스 비율", "%", s => s.Pass.PassTry == 0 ? null : 100.0 * s.Pass.ThroughPassTry / s.Pass.PassTry),
        ("태클 성공률", "%", s => s.Defence.TackleTry == 0 ? null : 100.0 * s.Defence.TackleSuccess / s.Defence.TackleTry),
        ("가로채기", "개", s => s.Player.Sum(p => p.Status.Intercept)),
        ("드리블 성공률", "%", s => s.Player.Sum(p => p.Status.DribbleTry) is var t and > 0 ? 100.0 * s.Player.Sum(p => p.Status.DribbleSuccess) / t : null),
        ("일시정지", "회", s => s.MatchDetail.SystemPause),
    ];

    /// <returns>The matches used, and the stats ordered by how far apart the two halves are.</returns>
    public static (int Matches, IReadOnlyList<WinFactor> Factors) Of(IEnumerable<MatchDetail> matches, string ouid)
    {
        // Forfeits say nothing about how the game was played.
        var games = matches.Select(m => m.SideOf(ouid)).Where(s => s is { HasStats: true, MatchDetail.IsForfeit: false }).Cast<MatchInfo>().ToList();
        if (games.Count < MinMatches) return (games.Count, []);

        var factors = new List<WinFactor>();
        foreach (var (label, unit, value) in Stats)
        {
            var rows = games.Select(s => (Value: value(s), Win: s.MatchDetail.Outcome == MatchOutcome.Win)).Where(r => r.Value is not null).ToList();
            if (rows.Count < MinMatches) continue;
            var sorted = rows.Select(r => r.Value!.Value).Order().ToList();
            var median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
            var high = rows.Where(r => r.Value > median).ToList();
            var low = rows.Where(r => r.Value <= median).ToList();
            if (high.Count < MinPerHalf || low.Count < MinPerHalf) continue;
            factors.Add(new WinFactor(label, unit, median, high.Count, (double)high.Count(r => r.Win) / high.Count, low.Count, (double)low.Count(r => r.Win) / low.Count));
        }
        return (games.Count, factors.OrderByDescending(f => Math.Abs(f.Gap)).ToList());
    }
}
