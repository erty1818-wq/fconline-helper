using FcHelper.Core;
using FcHelper.Core.Models;

namespace FcHelper.Analysis;

/// <summary>
/// Per-match proportions shared by the baseline and by a single user's analysis, so both are always
/// computed the same way. Keys look like "goal.type.2" or "conceded.zone.BoxCenter".
/// </summary>
public sealed class SideMetrics
{
    public const string GoalType = "goal.type.";
    public const string GoalZone = "goal.zone.";
    public const string GoalLongRange = "goal.longrange";
    public const string GoalLate = "goal.late";
    public const string GoalAssisted = "goal.assisted";
    public const string RouteCutback = "route.cutback";
    public const string RouteCross = "route.crossheader";
    public const string RouteThrough = "route.through";
    public const string ConcededType = "conceded.type.";
    public const string ConcededZone = "conceded.zone.";
    public const string ConcededLongRange = "conceded.longrange";
    public const string ConcededLate = "conceded.late";
    public const string PassThrough = "pass.through";
    public const string PassLobbedThrough = "pass.lobbedthrough";
    public const string PassLong = "pass.long";
    public const string Forfeit = "match.forfeit";

    /// <summary>Goals from this minute on count as "late".</summary>
    public const int LateMinute = 75;

    public Dictionary<string, Proportion> Rates { get; } = [];
    public int GoalCount { get; private set; }

    public static SideMetrics From(MatchInfo side, MatchInfo opponent)
    {
        var m = new SideMetrics();
        var goals = side.ShootDetail.Where(s => s.IsGoal).ToList();
        var conceded = opponent.ShootDetail.Where(s => s.IsGoal).ToList();
        m.GoalCount = goals.Count;

        AddShare(m, goals, GoalType, s => s.Type.ToString());
        AddShare(m, goals, GoalZone, s => Pitch.ZoneOf(s.X, s.Y, s.InPenalty).ToString());
        m.Add(GoalLongRange, goals.Count(s => Pitch.IsLongRange(Pitch.ZoneOf(s.X, s.Y, s.InPenalty))), goals.Count);
        m.Add(GoalLate, goals.Count(s => GoalTime.ToMinute(s.GoalTime) >= LateMinute), goals.Count);
        m.Add(GoalAssisted, goals.Count(s => s.AssistSpId is not null), goals.Count);

        var assisted = goals.Where(s => s.AssistSpId is not null).ToList();
        m.Add(RouteCutback, assisted.Count(GoalRoutes.IsCutback), assisted.Count);
        m.Add(RouteCross, assisted.Count(GoalRoutes.IsCrossHeader), assisted.Count);
        m.Add(RouteThrough, assisted.Count(GoalRoutes.IsThroughRun), assisted.Count);

        AddShare(m, conceded, ConcededType, s => s.Type.ToString());
        AddShare(m, conceded, ConcededZone, s => Pitch.ZoneOf(s.X, s.Y, s.InPenalty).ToString());
        m.Add(ConcededLongRange, conceded.Count(s => Pitch.IsLongRange(Pitch.ZoneOf(s.X, s.Y, s.InPenalty))), conceded.Count);
        m.Add(ConcededLate, conceded.Count(s => GoalTime.ToMinute(s.GoalTime) >= LateMinute), conceded.Count);

        var pass = side.Pass;
        m.Add(PassThrough, pass.ThroughPassTry, pass.PassTry);
        m.Add(PassLobbedThrough, pass.LobbedThroughPassTry, pass.PassTry);
        m.Add(PassLong, pass.LongPassTry, pass.PassTry);

        // matchEndType 2 = this side forfeited (quit or disconnected).
        m.Add(Forfeit, side.MatchDetail.MatchEndType == 2 ? 1 : 0, 1);
        return m;
    }

    private void Add(string key, int count, int total)
    {
        if (total <= 0) return;
        Rates[key] = Rates.GetValueOrDefault(key) + new Proportion(count, total);
    }

    private static void AddShare(SideMetrics m, List<ShootDetail> shots, string prefix, Func<ShootDetail, string> keyOf)
    {
        if (shots.Count == 0) return;
        // Every known category gets the total, including zero counts, so an absent category still lowers its rate.
        var counts = shots.GroupBy(keyOf).ToDictionary(g => g.Key, g => g.Count());
        foreach (var key in AllKeys(prefix).Concat(counts.Keys).Distinct())
        {
            m.Add(prefix + key, counts.GetValueOrDefault(key), shots.Count);
        }
    }

    private static IEnumerable<string> AllKeys(string prefix) => prefix switch
    {
        GoalType or ConcededType => Enumerable.Range(1, 12).Select(i => i.ToString()),
        GoalZone or ConcededZone => Enum.GetNames<Core.GoalZone>(),
        _ => [],
    };
}
