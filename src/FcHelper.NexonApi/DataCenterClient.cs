using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using FcHelper.Core.Models;

namespace FcHelper.NexonApi;

public interface IPlayerMarketSource
{
    /// <returns>Null when the data center has no such player.</returns>
    Task<PlayerMarket?> GetPlayerAsync(int spId, int strong, CancellationToken ct = default);
}

/// <summary>
/// Reads one player's overall and price from the official data center (fconline.nexon.com), the same two
/// requests its player page makes. The Open API has neither value. For personal use only: called for the few
/// players shown on a card, one request per second at most, and cached by the caller (docs/PLANNING.md 5).
/// robots.txt of fconline.nexon.com does not restrict these pages.
/// </summary>
public sealed class DataCenterClient(HttpClient http, RateLimiter limiter, TimeProvider? time = null) : IPlayerMarketSource
{
    private static readonly Uri Base = new("https://fconline.nexon.com/");
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<PlayerMarket?> GetPlayerAsync(int spId, int strong, CancellationToken ct = default)
    {
        strong = Math.Clamp(strong, 1, 13);
        var s = strong.ToString(CultureInfo.InvariantCulture);
        var ability = await PostAsync("datacenter/PlayerAbility",
            $"spid={spId}&n1Strong={s}&n1Grow=0&n4TeamColorId=0&n4TeamColorLv=0&n4TeamColorId_Enhance=0" +
            "&n4TeamColorLv_Enhance=0&n4TeamColorId_Feature=0&n1Change=0&strPlayerImg=", ct);
        var card = DataCenterParser.ParseAbility(ability);
        if (card is null) return null;
        var price = DataCenterParser.ParsePrice(await PostAsync("datacenter/PlayerPriceGraph", $"spid={spId}&n1strong={s}", ct));
        return new PlayerMarket(spId, strong, card.Value.Name, card.Value.Ovr, card.Value.Position, price, _time.GetUtcNow().UtcDateTime);
    }

    private async Task<string> PostAsync(string path, string form, CancellationToken ct)
    {
        await limiter.WaitAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(Base, path))
        {
            Content = new StringContent(form, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        req.Headers.UserAgent.ParseAdd("FcHelper/0.5 (personal use)");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"data center {(int)res.StatusCode}", null, res.StatusCode);
        return await res.Content.ReadAsStringAsync(ct);
    }
}

/// <summary>Pulls the few values we show out of the data center's HTML fragments.</summary>
public static partial class DataCenterParser
{
    public static (string Name, int Ovr, string Position)? ParseAbility(string html)
    {
        var ovr = OvrRegex().Match(html);
        var name = NameRegex().Match(html);
        if (!ovr.Success || !name.Success) return null;
        var position = PositionRegex().Match(html);
        return (WebUtility.HtmlDecode(name.Groups[1].Value.Trim()), int.Parse(ovr.Groups[1].Value, CultureInfo.InvariantCulture),
            position.Success ? position.Groups[1].Value.Trim() : "");
    }

    /// <summary>The "현재가" figure exactly as displayed, e.g. "4,820".</summary>
    public static string? ParsePrice(string html)
    {
        var m = PriceRegex().Match(html);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : null;
    }

    [GeneratedRegex("""class="ovr value">\s*(\d+)""")]
    private static partial Regex OvrRegex();

    [GeneratedRegex("""class="name">([^<]+)<""")]
    private static partial Regex NameRegex();

    [GeneratedRegex("""class="position">\s*([A-Z]{2,3})\s*<""")]
    private static partial Regex PositionRegex();

    [GeneratedRegex("""<span>현재가</span>\s*<strong[^>]*>\s*([^<]+?)\s*</strong>""")]
    private static partial Regex PriceRegex();
}
