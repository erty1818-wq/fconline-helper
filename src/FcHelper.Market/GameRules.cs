using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using FcHelper.NexonApi;

namespace FcHelper.Market;

/// <summary>
/// The market's sale fee, as the official calculator (fconline.nexon.com/common/calculator) works it out: 40% of the
/// sale; PC방 cuts that fee by 30%, TOP CLASS by 20%, both by 50%; a discount coupon cuts it by its % more (at most
/// 100 − that), capped at the coupon's maximum discount per card.
/// </summary>
public sealed record SaleFee(bool PcRoom = false, bool TopClass = false, int CouponPercent = 0, long CouponMaxDiscount = 0)
{
    public const int BaseRate = 40;

    public static readonly SaleFee Standard = new();

    /// <summary>Discount on the fee from PC방 / TOP CLASS, in %.</summary>
    public int BenefitPercent => PcRoom && TopClass ? 50 : PcRoom ? 30 : TopClass ? 20 : 0;

    public long FeeOn(long price)
    {
        var fee = price * BaseRate / 100;
        var benefit = fee * BenefitPercent / 100;
        var couponPercent = Math.Clamp(CouponPercent, 0, 100 - BenefitPercent);
        var coupon = fee * couponPercent / 100;
        if (CouponMaxDiscount > 0) coupon = Math.Min(coupon, CouponMaxDiscount);
        return fee - benefit - coupon;
    }

    /// <summary>What the seller receives.</summary>
    public long NetOf(long price) => price - FeeOn(price);

    /// <summary>Effective fee rate before a coupon cap, e.g. 0.28 with PC방.</summary>
    public double Rate => BaseRate / 100.0 * (100 - BenefitPercent - Math.Clamp(CouponPercent, 0, 100 - BenefitPercent)) / 100;
}

/// <summary>
/// The squad salary cap (급여 한도). The Open API does not have it; the official squad maker has it built into its
/// script (e.g. <c>Number(t??0)&gt;310</c>), so it is read from there. Falls back to the last known value.
/// </summary>
public sealed partial class SalaryCapSource(HttpClient http, RateLimiter limiter)
{
    public const int Fallback = 310;
    private const string Page = "https://fconline.nexon.com/squadmaker";

    /// <returns>The cap, or null when the page layout changed and no value could be read.</returns>
    public async Task<int?> FetchAsync(CancellationToken ct = default)
    {
        var page = await GetAsync(Page, ct);
        if (LoaderRegex().Match(page) is not { Success: true } loader) return null;
        var loaderUrl = Absolute(WebUtility.HtmlDecode(loader.Groups[1].Value), Page);
        var loaderJs = await GetAsync(loaderUrl, ct);
        if (BundleRegex().Match(loaderJs) is not { Success: true } bundle) return null;
        var bundleUrl = loaderUrl[..(loaderUrl.IndexOf('?') is var q and >= 0 ? q : loaderUrl.Length)];
        bundleUrl = bundleUrl[..(bundleUrl.LastIndexOf('/') + 1)] + bundle.Groups[1].Value;
        return Parse(await GetAsync(bundleUrl, ct));
    }

    /// <summary>The cap the squad maker compares the total salary with; null unless the checks agree.</summary>
    public static int? Parse(string js)
    {
        var values = CapRegex().Matches(js).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Concat(PercentRegex().Matches(js).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)))
            .Where(v => v is >= 100 and <= 999).ToList();
        if (values.Count == 0 || values.Distinct().Count() != 1) return null;
        return values[0];
    }

    private static string Absolute(string src, string page) =>
        src.StartsWith("//", StringComparison.Ordinal) ? "https:" + src : new Uri(new Uri(page), src).ToString();

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        await limiter.WaitAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("FcHelper/0.5 (personal use)");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"squad maker {(int)res.StatusCode}", null, res.StatusCode);
        return await res.Content.ReadAsStringAsync(ct);
    }

    [GeneratedRegex("""<script[^>]+src="([^"]*squadMaker/app\.js[^"]*)""", RegexOptions.IgnoreCase)] private static partial Regex LoaderRegex();
    [GeneratedRegex("""["'](app\.[\w-]+\.js)["']""")] private static partial Regex BundleRegex();
    [GeneratedRegex(@"Number\(\w+\?\?0\)>(\d{3})\b")] private static partial Regex CapRegex();
    [GeneratedRegex(@"Number\(\w+\?\?0\)/(\d{3})\*100")] private static partial Regex PercentRegex();
}

/// <summary>The salary cap in SQLite, re-read weekly from the squad maker; the last known value if that fails.</summary>
public sealed class SalaryCapCache(MarketStore store, SalaryCapSource source, TimeProvider? time = null)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(7);
    private const string Key = "salary.cap";
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The known cap without any request (the fallback before the first check).</summary>
    public int Current => store.GetValue(Key) is { } v && int.TryParse(v.Value, CultureInfo.InvariantCulture, out var cap) ? cap : SalaryCapSource.Fallback;

    public async Task<int> GetAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (store.GetValue(Key) is { } v && now - v.UpdatedAt < Ttl) return Current;
        // A failed check (offline, page changed) waits a day before the 1.3 MB script is fetched again.
        if (store.GetValue(Key + ".tried") is { } t && now - t.UpdatedAt < TimeSpan.FromDays(1)) return Current;
        store.SetValue(Key + ".tried", "1", now);
        try
        {
            if (await source.FetchAsync(ct) is { } cap) store.SetValue(Key, cap.ToString(CultureInfo.InvariantCulture), now);
        }
        catch (HttpRequestException)
        {
            // Offline or the page moved: keep the last value and try again next time.
        }
        return Current;
    }
}
