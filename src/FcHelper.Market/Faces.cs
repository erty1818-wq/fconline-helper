using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using FcHelper.NexonApi;

namespace FcHelper.Market;

/// <summary>One picture the official squad maker offers for a footballer (미니페이스온): the season it belongs to and its URL.</summary>
public sealed record FaceOption(long SpId, string Season, string Url)
{
    public bool IsAction => Url.Contains("playersAction", StringComparison.Ordinal);
}

public static class FaceUrls
{
    private const string Cdn = "https://fco.dn.nexoncdn.co.kr/live/externalAssets/common/";

    /// <summary>The picture a card shows by default: its own season's action picture.</summary>
    public static string Default(long spId) => $"{Cdn}playersAction/p{spId.ToString(CultureInfo.InvariantCulture)}.png";

    /// <summary>
    /// The URL rule of the squad maker's picture list: 1 = that season's action picture, 2 = its head shot, 3 = the
    /// footballer's shared action picture, 4 = the shared head shot (the last two by player id = spid % 1,000,000).
    /// </summary>
    public static string Of(long spId, int kind)
    {
        var pid = (spId % 1_000_000).ToString(CultureInfo.InvariantCulture);
        var sp = spId.ToString(CultureInfo.InvariantCulture);
        return kind switch
        {
            1 => $"{Cdn}playersAction/p{sp}.png",
            2 => $"{Cdn}players/p{sp}.png",
            3 => $"{Cdn}playersAction/p{pid}.png",
            _ => $"{Cdn}players/p{pid}.png",
        };
    }
}

/// <summary>POST /squadmaker/GetPlayerCustomImgList: the pictures of every season of a footballer (personal use, shared limiter).</summary>
public sealed class FaceClient(HttpClient http, RateLimiter limiter)
{
    private static readonly Uri Url = new("https://fconline.nexon.com/squadmaker/GetPlayerCustomImgList");

    private sealed record Row([property: JsonPropertyName("spid")] long SpId, [property: JsonPropertyName("n1Custom")] int Custom,
        [property: JsonPropertyName("season")] string Season);

    /// <summary>The list as the data center returns it (JSON), for caching; read it with <see cref="Parse"/>.</summary>
    public async Task<string> FetchAsync(long spId, CancellationToken ct = default)
    {
        await limiter.WaitAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["spid"] = spId.ToString(CultureInfo.InvariantCulture) }),
        };
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        req.Headers.UserAgent.ParseAdd("FcHelper/0.5 (personal use)");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"data center {(int)res.StatusCode}", null, res.StatusCode);
        return await res.Content.ReadAsStringAsync(ct);
    }

    public static IReadOnlyList<FaceOption> Parse(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<Row>>(json) ?? [])
                .Select(r => new FaceOption(r.SpId, r.Season, FaceUrls.Of(r.SpId, r.Custom))).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
