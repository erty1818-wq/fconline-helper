using FcHelper.Core;

namespace FcHelper.Analysis;

public enum InsightKind
{
    /// <summary>How this user scores (shown under "주의").</summary>
    Threat,
    /// <summary>How this user concedes (shown under "약점").</summary>
    Weakness,
    /// <summary>Other traits: controller, forfeits, style.</summary>
    Trait,
}

/// <summary>One line of the summary card, with the evidence behind it.</summary>
public sealed record Insight(
    string Key,
    InsightKind Kind,
    string Text,
    Evidence Evidence,
    int SampleSize,
    double Score)
{
    /// <summary>The user's rate after shrinkage toward the baseline, when a baseline exists.</summary>
    public double? Rate { get; init; }
    public double? BaselineRate { get; init; }
    /// <summary>The text without numbers, for the one-line summary and voice briefing.</summary>
    public string Short { get; init; } = "";
}

public sealed record Share(string Label, int Count, int Total)
{
    public double Ratio => Total == 0 ? 0 : (double)Count / Total;
}

public sealed record PlayerThreat(int SpId, int Goals, int Assists, int Shots, int Appearances, double GoalShare, int TopGrade);

public sealed record GoalCombo(int AssistSpId, int ScorerSpId, int Count);

/// <summary>The single most repeated way this user scores.</summary>
public sealed record SignatureGoal(int ScorerSpId, int? AssistSpId, GoalZone Zone, int ShotType, int Count, int TotalGoals);

public sealed record FirstGoalStats(int MatchesScoredFirst, int WinsWhenScoredFirst, int MatchesConcededFirst, int WinsWhenConcededFirst, int Goalless);

public sealed record RecordSummary(int Matches, int Wins, int Draws, int Losses, int Forfeits)
{
    public double WinRate => Matches == 0 ? 0 : (double)Wins / Matches;
}

public sealed record UserAnalysis
{
    public required string Ouid { get; init; }
    public required RecordSummary Record { get; init; }
    public double AvgGoalsFor { get; init; }
    public double AvgGoalsAgainst { get; init; }
    public double AvgPossession { get; init; }
    public double AvgShots { get; init; }
    public double AvgShotsOnTarget { get; init; }
    public double ShotAccuracy { get; init; }
    public double Conversion { get; init; }
    public double PassSuccess { get; init; }
    public double AvgTackleTry { get; init; }
    public double TackleSuccess { get; init; }
    public double AvgIntercept { get; init; }
    public double AvgPause { get; init; }
    public Share? Controller { get; init; }

    public int GoalCount { get; init; }
    public int ConcededCount { get; init; }
    public IReadOnlyList<Share> GoalTypes { get; init; } = [];
    public IReadOnlyList<Share> GoalZones { get; init; } = [];
    public IReadOnlyList<Share> ConcededTypes { get; init; } = [];
    public IReadOnlyList<Share> ConcededZones { get; init; } = [];
    public IReadOnlyList<Share> PassMix { get; init; } = [];
    public IReadOnlyList<PlayerThreat> Players { get; init; } = [];
    public IReadOnlyList<GoalCombo> Combos { get; init; } = [];
    public SignatureGoal? Signature { get; init; }
    public FirstGoalStats? FirstGoal { get; init; }

    /// <summary>Everything that stood out, strongest first. The card shows the top few of each kind.</summary>
    public IReadOnlyList<Insight> Insights { get; init; } = [];

    /// <summary>False when the baseline was too small, so insights use absolute thresholds and make no "평균 대비" claims.</summary>
    public bool ComparedToBaseline { get; init; }

    /// <summary>Raw proportions keyed like "goal.type.2"; used to compare two users.</summary>
    public IReadOnlyDictionary<string, Proportion> Rates { get; init; } = new Dictionary<string, Proportion>();

    public IEnumerable<Insight> Threats => Insights.Where(i => i.Kind == InsightKind.Threat);
    public IEnumerable<Insight> Weaknesses => Insights.Where(i => i.Kind == InsightKind.Weakness);
    public IEnumerable<Insight> Traits => Insights.Where(i => i.Kind == InsightKind.Trait);
}
