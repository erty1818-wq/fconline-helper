namespace FcHelper.Analysis;

/// <summary>An opponent strength that lines up with one of my own weaknesses.</summary>
public sealed record MatchupAlert(string Text, Insight OpponentThreat, Insight MyWeakness);

public static class Matchup
{
    /// <summary>
    /// Pairs the opponent's scoring patterns ("goal.*") with the same pattern in what I concede ("conceded.*").
    /// Only patterns that stood out on both sides are reported.
    /// </summary>
    public static IReadOnlyList<MatchupAlert> Compare(UserAnalysis opponent, UserAnalysis me)
    {
        var myWeaknesses = me.Weaknesses.ToDictionary(w => w.Key);
        var alerts = new List<MatchupAlert>();
        foreach (var threat in opponent.Threats)
        {
            if (!threat.Key.StartsWith("goal.", StringComparison.Ordinal)) continue;
            var mirror = "conceded." + threat.Key["goal.".Length..];
            if (myWeaknesses.TryGetValue(mirror, out var weakness))
            {
                alerts.Add(new MatchupAlert($"상대 {threat.Short} ↑ × 내 {weakness.Short} ↑", threat, weakness));
            }
        }
        return alerts.OrderByDescending(a => a.OpponentThreat.Score + a.MyWeakness.Score).ToList();
    }
}
