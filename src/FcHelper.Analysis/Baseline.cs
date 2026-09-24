using FcHelper.Core.Models;

namespace FcHelper.Analysis;

/// <summary>A count out of a total, e.g. 23 finesse goals out of 60 goals.</summary>
public readonly record struct Proportion(int Count, int Total)
{
    public double Value => Total == 0 ? 0 : (double)Count / Total;
    public static Proportion operator +(Proportion a, Proportion b) => new(a.Count + b.Count, a.Total + b.Total);
}

/// <summary>
/// Population rates that a single user is compared against. Built from every match side in the local cache,
/// so it describes "the users I actually meet" and improves as the cache grows.
/// </summary>
public sealed class Baseline
{
    /// <summary>Below this many goals the baseline is too noisy to support "평균 대비" claims.</summary>
    public const int MinGoalsForComparison = 300;

    public Dictionary<string, Proportion> Rates { get; init; } = [];
    public int Sides { get; init; }
    public int Goals { get; init; }
    public double AvgPossession { get; init; }
    public double AvgShots { get; init; }

    public bool IsUsable => Goals >= MinGoalsForComparison;

    public double? RateOf(string key) => Rates.TryGetValue(key, out var p) && p.Total > 0 ? p.Value : null;

    public static Baseline Build(IEnumerable<MatchDetail> matches, string? excludeOuid = null)
    {
        var rates = new Dictionary<string, Proportion>();
        int sides = 0, played = 0, goals = 0;
        double possession = 0, shots = 0;

        foreach (var match in matches)
        {
            if (match.MatchInfo.Count != 2) continue;
            foreach (var side in match.MatchInfo)
            {
                if (side.Ouid == excludeOuid) continue;
                var opponent = match.MatchInfo.First(m => !ReferenceEquals(m, side));
                var metrics = SideMetrics.From(side, opponent);
                foreach (var (key, p) in metrics.Rates) rates[key] = rates.GetValueOrDefault(key) + p;
                sides++;
                goals += metrics.GoalCount;
                if (!side.HasStats) continue;
                played++;
                possession += side.MatchDetail.Possession;
                shots += side.Shoot.ShootTotal;
            }
        }

        return new Baseline
        {
            Rates = rates,
            Sides = sides,
            Goals = goals,
            AvgPossession = played == 0 ? 0 : possession / played,
            AvgShots = played == 0 ? 0 : shots / played,
        };
    }
}
