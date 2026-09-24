using FcHelper.Analysis;
using FcHelper.Data;

namespace FcHelper.Services;

public sealed record HeadToHead(int Matches, int Wins, int Draws, int Losses, DateTime? LastPlayed, string? LastScore);

/// <summary>Everything the summary card needs about one opponent.</summary>
public sealed record OpponentReport
{
    public required string Ouid { get; init; }
    public required string Nickname { get; init; }
    public int Level { get; init; }
    /// <summary>Highest division ever reached in the match type (user/maxdivision).</summary>
    public string? MaxDivisionName { get; init; }
    /// <summary>
    /// Division recorded on the newest analysed match. The API has no live grade; this is the closest thing,
    /// and it can be hours old (data lags about two hours).
    /// </summary>
    public string? RecentDivisionName { get; init; }
    public IReadOnlyList<string> PreviousNicknames { get; init; } = [];
    public required UserAnalysis Analysis { get; init; }
    public required string OneLine { get; init; }
    public IReadOnlyList<MatchupAlert> MatchupAlerts { get; init; } = [];
    public HeadToHead? HeadToHead { get; init; }
    public Memo? Memo { get; init; }
    public IReadOnlyDictionary<int, string> PlayerNames { get; init; } = new Dictionary<int, string>();

    /// <summary>How many matches the analysis used, and how many were requested.</summary>
    public int LoadedMatches { get; init; }
    public int RequestedMatches { get; init; }
    public bool IsComplete => LoadedMatches >= RequestedMatches;

    public string PlayerName(int spId) => PlayerNames.TryGetValue(spId, out var n) ? n : $"#{spId}";
}

public enum LookupStage { ResolvingUser, ShowingCache, FetchingMatches, Done }

public sealed record LookupProgress(LookupStage Stage, OpponentReport? Report, int Fetched, int ToFetch);
