using System.Text.Json.Serialization;

namespace FcHelper.Core.Models;

public sealed record OuidResponse
{
    [JsonPropertyName("ouid")] public string Ouid { get; init; } = "";
}

public sealed record UserBasic
{
    [JsonPropertyName("ouid")] public string Ouid { get; init; } = "";
    [JsonPropertyName("nickname")] public string Nickname { get; init; } = "";
    [JsonPropertyName("level")] public int Level { get; init; }
}

/// <summary>Highest division ever reached for a match type. The API does not expose the current division.</summary>
public sealed record MaxDivision
{
    [JsonPropertyName("matchType")] public int MatchType { get; init; }
    [JsonPropertyName("division")] public int Division { get; init; }
    [JsonPropertyName("achievementDate")] public DateTime AchievementDate { get; init; }
}

/// <summary>
/// ranker-stats: the TOP 10,000 rankers' average per match with one card at one position, over 20 matches
/// (official API). Cards rankers did not use are missing from the response.
/// </summary>
public sealed record RankerStat
{
    [JsonPropertyName("spid")] public long SpId { get; init; }
    [JsonPropertyName("spPosition")] public int SpPosition { get; init; }
    [JsonPropertyName("status")] public RankerStatus Status { get; init; } = new();
    [JsonPropertyName("createDate")] public DateTime CreateDate { get; init; }
}

public sealed record RankerStatus
{
    [JsonPropertyName("shoot")] public double Shoot { get; init; }
    [JsonPropertyName("effectiveShoot")] public double EffectiveShoot { get; init; }
    [JsonPropertyName("assist")] public double Assist { get; init; }
    [JsonPropertyName("goal")] public double Goal { get; init; }
    [JsonPropertyName("dribble")] public double Dribble { get; init; }
    [JsonPropertyName("dribbleTry")] public double DribbleTry { get; init; }
    [JsonPropertyName("dribbleSuccess")] public double DribbleSuccess { get; init; }
    [JsonPropertyName("passTry")] public double PassTry { get; init; }
    [JsonPropertyName("passSuccess")] public double PassSuccess { get; init; }
    [JsonPropertyName("block")] public double Block { get; init; }
    [JsonPropertyName("tackle")] public double Tackle { get; init; }
    [JsonPropertyName("matchCount")] public int MatchCount { get; init; }
}

public sealed record MatchTypeMeta
{
    [JsonPropertyName("matchtype")] public int MatchType { get; init; }
    [JsonPropertyName("desc")] public string Desc { get; init; } = "";
}

public sealed record SpIdMeta
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
}

public sealed record DivisionMeta
{
    [JsonPropertyName("divisionId")] public int DivisionId { get; init; }
    [JsonPropertyName("divisionName")] public string DivisionName { get; init; } = "";
}

public sealed record SpPositionMeta
{
    [JsonPropertyName("spposition")] public int SpPosition { get; init; }
    [JsonPropertyName("desc")] public string Desc { get; init; } = "";
}

public sealed record SeasonIdMeta
{
    [JsonPropertyName("seasonId")] public int SeasonId { get; init; }
    [JsonPropertyName("className")] public string ClassName { get; init; } = "";
    [JsonPropertyName("seasonImg")] public string SeasonImg { get; init; } = "";
}
