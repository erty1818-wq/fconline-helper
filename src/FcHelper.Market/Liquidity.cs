using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using FcHelper.NexonApi;

namespace FcHelper.Market;

/// <summary>
/// Whether a card at a grade really trades: the data center's daily price (365 days) only moves on days it sold, so
/// a price that changed on fewer than <see cref="MinChanges"/> of the last 30 days is a listing nobody can buy
/// (e.g. 손흥민 25TOTY: +8 changed 30 times, +11 six times; TK +13 never).
/// </summary>
public sealed record CardLiquidity(long SpId, int Grade, int Days, int Changes30)
{
    public const int MinChanges = 8;

    /// <summary>New cards (under two weeks of history) are given the benefit of the doubt.</summary>
    public bool Tradable => Days < 14 || Changes30 >= MinChanges;
}

/// <summary>The data center's price graph of one card at one grade (POST /datacenter/PlayerPriceGraph).</summary>
public sealed partial class PriceHistoryClient(HttpClient http, RateLimiter limiter)
{
    public async Task<IReadOnlyList<long>> DailyPricesAsync(long spId, int grade, CancellationToken ct = default)
    {
        await limiter.WaitAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://fconline.nexon.com/datacenter/PlayerPriceGraph")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["spid"] = spId.ToString(CultureInfo.InvariantCulture), ["n1strong"] = grade.ToString(CultureInfo.InvariantCulture),
            }),
        };
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        req.Headers.UserAgent.ParseAdd("FcHelper/0.5 (personal use)");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"data center {(int)res.StatusCode}", null, res.StatusCode);
        return Parse(await res.Content.ReadAsStringAsync(ct));
    }

    /// <summary>The "value" array of the page's json1 object: one price per day, oldest first.</summary>
    public static IReadOnlyList<long> Parse(string html)
    {
        var start = html.IndexOf("json1", StringComparison.Ordinal);
        if (start < 0) return [];
        var m = ValueRegex().Match(html, start);
        if (!m.Success) return [];
        return m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => long.TryParse(v.Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : -1)
            .Where(p => p >= 0).ToList();
    }

    public static CardLiquidity Measure(long spId, int grade, IReadOnlyList<long> prices)
    {
        var last = prices.Skip(Math.Max(0, prices.Count - 31)).ToList();
        var changes = last.Zip(last.Skip(1)).Count(p => p.First != p.Second);
        return new CardLiquidity(spId, grade, prices.Count, changes);
    }

    [GeneratedRegex(@"""value""\s*:\s*\[(.*?)\]", RegexOptions.Singleline)] private static partial Regex ValueRegex();
}

/// <summary>Liquidity per card and grade in SQLite, measured on demand and kept three days.</summary>
public sealed class LiquidityCache(MarketStore store, PriceHistoryClient client, TimeProvider? time = null)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(3);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private static string Key(long spId, int grade) => $"liq.{spId}.{grade}";

    /// <summary>What is known without a request (null: not measured yet or too old).</summary>
    public CardLiquidity? Known(long spId, int grade)
    {
        if (store.GetValue(Key(spId, grade)) is not { } v || _time.GetUtcNow().UtcDateTime - v.UpdatedAt >= Ttl) return null;
        return v.Value.Split(':') is [var d, var c] ? new CardLiquidity(spId, grade, int.Parse(d, CultureInfo.InvariantCulture), int.Parse(c, CultureInfo.InvariantCulture)) : null;
    }

    public async Task<CardLiquidity> GetAsync(long spId, int grade, CancellationToken ct = default)
    {
        if (Known(spId, grade) is { } known) return known;
        var measured = PriceHistoryClient.Measure(spId, grade, await client.DailyPricesAsync(spId, grade, ct));
        store.SetValue(Key(spId, grade), $"{measured.Days}:{measured.Changes30}", _time.GetUtcNow().UtcDateTime);
        return measured;
    }
}
