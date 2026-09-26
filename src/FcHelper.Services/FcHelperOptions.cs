namespace FcHelper.Services;

public sealed record FcHelperOptions
{
    /// <summary>공식경기. Loaded from meta/matchtype.json would be better; 50 is the documented value (확인 필요).</summary>
    public int MatchType { get; init; } = 50;

    /// <summary>Matches analysed per lookup. More costs one API call per uncached match.</summary>
    public int MatchWindow { get; init; } = 30;

    /// <summary>A partial result is published every this many fetched matches.</summary>
    public int ProgressBatch { get; init; } = 10;

    /// <summary>My own nickname; enables head-to-head records and matchup alerts.</summary>
    public string? MyNickname { get; init; }

    public TimeSpan UserInfoTtl { get; init; } = TimeSpan.FromDays(1);
    /// <summary>
    /// Opening the same manager again within this time answers from the cache without asking the API for new matches
    /// (the [갱신] button still does). The API lags about two hours anyway.
    /// </summary>
    public TimeSpan RecheckAfter { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan MetadataTtl { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan BaselineTtl { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How many of the card's dangerous players get overall and price from the data center (0 = none).
    /// Only used when the service is given a market source.
    /// </summary>
    public int MarketPlayers { get; init; } = 2;
    public TimeSpan MarketTtl { get; init; } = TimeSpan.FromHours(12);

    /// <summary>Matches read when rebuilding the baseline.</summary>
    public int BaselineSampleLimit { get; init; } = 3000;
}
