using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FcHelper.NexonApi;

namespace FcHelper.Market;

/// <summary>
/// Every stat of a card at +1 as the data center's player page shows it (적응도 +1, no team colour), and the OVR it
/// lists for each position. With these and <see cref="OvrFormula"/> the in-game OVR after grade, 적응도, team colours
/// and 집중훈련 can be worked out exactly.
/// </summary>
public sealed record CardAbility(long SpId, IReadOnlyDictionary<string, int> Stats, IReadOnlyDictionary<string, int> Positions);

public static partial class AbilityParser
{
    /// <summary>Stats (the 34 detailed ones; GK stats as "GK 다이빙" …) and the per-position OVR row of a PlayerAbility fragment.</summary>
    public static CardAbility? Parse(long spId, string html)
    {
        var positions = PositionRegex().Matches(html)
            .GroupBy(m => m.Groups[1].Value.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => int.Parse(g.First().Groups[2].Value, CultureInfo.InvariantCulture));
        var stats = StatRegex().Matches(ScriptRegex().Replace(html, ""))
            .GroupBy(m => WebUtility.HtmlDecode(m.Groups[1].Value.Trim()))
            .ToDictionary(g => g.Key, g => int.Parse(g.First().Groups[2].Value, CultureInfo.InvariantCulture));
        return positions.Count == 0 || stats.Count == 0 ? null : new CardAbility(spId, stats, positions);
    }

    [GeneratedRegex("""<div class="position (\w+) value">(\d+)</div>""")] private static partial Regex PositionRegex();
    // Only the stats tied to positions (data-positon) — the summary block above them repeats a few under other names.
    [GeneratedRegex("""data-positon="[^"]*">\s*<div class="txt">([^<]+)</div>\s*<div class="value[^"]*">\s*(\d+)""")] private static partial Regex StatRegex();
    [GeneratedRegex("<script.*?</script>", RegexOptions.Singleline)] private static partial Regex ScriptRegex();
}

/// <summary>POST /datacenter/PlayerAbility for one card at +1: the request the data center's player page makes.</summary>
public sealed class AbilityClient(HttpClient http, RateLimiter limiter)
{
    private static readonly Uri Url = new("https://fconline.nexon.com/datacenter/PlayerAbility");

    public async Task<CardAbility?> FetchAsync(long spId, CancellationToken ct = default)
    {
        await limiter.WaitAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["spid"] = spId.ToString(CultureInfo.InvariantCulture), ["n1Strong"] = "1", ["n1Grow"] = "0", ["n4TeamColorId"] = "0",
                ["n4TeamColorLv"] = "0", ["n4TeamColorId_Enhance"] = "0", ["n4TeamColorLv_Enhance"] = "0", ["n4TeamColorId_Feature"] = "0",
                ["n1Change"] = "0", ["strPlayerImg"] = "",
            }),
        };
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        req.Headers.UserAgent.ParseAdd("FcHelper/0.5 (personal use)");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"data center {(int)res.StatusCode}", null, res.StatusCode);
        return AbilityParser.Parse(spId, await res.Content.ReadAsStringAsync(ct));
    }
}

/// <summary>Card stats in SQLite, fetched the first time a card is put in a squad and then kept a week (stats change only with live updates).</summary>
public sealed class AbilityCache(MarketStore store, AbilityClient client, TimeProvider? time = null)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(7);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<long, CardAbility> _memory = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed record Saved(Dictionary<string, int> Stats, Dictionary<string, int> Positions);

    /// <summary>Stats already here (memory or database, even when old), without a request.</summary>
    public CardAbility? Known(long spId)
    {
        lock (_memory) if (_memory.TryGetValue(spId, out var a)) return a;
        if (store.GetValue(Key(spId)) is not { } v) return null;
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(v.Value);
            if (saved is null) return null;
            var ability = new CardAbility(spId, saved.Stats, saved.Positions);
            lock (_memory) _memory[spId] = ability;
            return ability;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<CardAbility?> GetAsync(long spId, CancellationToken ct = default)
    {
        if (store.GetValue(Key(spId)) is { } v && _time.GetUtcNow().UtcDateTime - v.UpdatedAt < Ttl && Known(spId) is { } fresh) return fresh;
        await _gate.WaitAsync(ct);
        try
        {
            var ability = await client.FetchAsync(spId, ct);
            if (ability is null) return Known(spId);
            store.SetValue(Key(spId), JsonSerializer.Serialize(new Saved(ability.Stats.ToDictionary(), ability.Positions.ToDictionary())), _time.GetUtcNow().UtcDateTime);
            lock (_memory) _memory[spId] = ability;
            return ability;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Key(long spId) => $"ability.{spId.ToString(CultureInfo.InvariantCulture)}";
}
