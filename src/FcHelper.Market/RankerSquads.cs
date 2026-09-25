using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FcHelper.Core;
using FcHelper.Core.Models;
using FcHelper.NexonApi;

namespace FcHelper.Market;

public sealed record RankerSquadPlayer(long SpId, int Grade, string Position);

/// <summary>A top ranker's starting eleven from their latest official match, with the 구단가치 the ranking shows.</summary>
public sealed record RankerSquad(int Rank, string Nickname, long TeamValue, IReadOnlyList<RankerSquadPlayer> Starters);

public interface IRankerSquadSource
{
    Task<IReadOnlyList<RankerSquad>> FetchAsync(int count, IProgress<string>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// Who the top rankers are from the official ranking (data center, 20 per page), then each one's latest official
/// match through the NEXON Open API: id → match list → match detail, three calls per ranker. Personal use.
/// </summary>
public sealed partial class RankerSquadClient(HttpClient http, RateLimiter dataCenter, Func<FcOnlineApi?> api) : IRankerSquadSource
{
    private const int OfficialMatch = 50, PerPage = 20;

    public async Task<IReadOnlyList<RankerSquad>> FetchAsync(int count, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var client = api() ?? throw new InvalidOperationException("랭커 스쿼드는 NEXON Open API 키가 있어야 받을 수 있습니다.");
        var rankers = new List<(int Rank, string Nickname, long Value)>();
        for (var page = 1; rankers.Count < count && page <= (count + PerPage - 1) / PerPage; page++)
        {
            progress?.Report($"랭킹 {page}페이지 읽는 중");
            rankers.AddRange(Parse(await GetAsync($"https://fconline.nexon.com/datacenter/rank_inner?rt=1vs1&n4seasonno=0&n4pageno={page}", ct)));
        }
        var squads = new List<RankerSquad>();
        foreach (var (rank, nickname, value) in rankers.Take(count))
        {
            progress?.Report($"랭커 스쿼드 {squads.Count + 1}/{Math.Min(count, rankers.Count)} · {rank}위");
            try
            {
                if (await client.GetOuidAsync(nickname, ct) is not { } ouid) continue;
                var ids = await client.GetUserMatchIdsAsync(ouid, OfficialMatch, 0, 1, ct);
                if (ids.Count == 0) continue;
                var detail = JsonSerializer.Deserialize(await client.GetMatchDetailJsonAsync(ids[0], ct), FcJsonContext.Default.MatchDetail);
                var side = detail?.SideOf(ouid);
                if (side is null) continue;
                var starters = side.Player.Where(p => !p.IsSubstitute)
                    .Select(p => new RankerSquadPlayer(p.SpId, Math.Max(1, p.SpGrade), Positions.Label(p.SpPosition))).ToList();
                if (starters.Count == 11) squads.Add(new RankerSquad(rank, nickname, value, starters));
            }
            catch (NexonApiException e) when (!e.IsAuthError)
            {
                // One ranker's odd data (renamed, no matches): skip them.
            }
        }
        return squads;
    }

    /// <summary>Rank, nickname and 구단가치 of each row of a ranking page.</summary>
    public static IReadOnlyList<(int Rank, string Nickname, long Value)> Parse(string html) =>
        RowRegex().Matches(html).Select(m => (
            int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            WebUtility.HtmlDecode(m.Groups[2].Value.Trim()),
            long.TryParse(m.Groups[3].Value.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0)).ToList();

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        await dataCenter.WaitAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        req.Headers.UserAgent.ParseAdd("FcHelper/0.5 (personal use)");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"data center {(int)res.StatusCode}", null, res.StatusCode);
        return await res.Content.ReadAsStringAsync(ct);
    }

    [GeneratedRegex(""""<span class="td rank_no">(\d+)</span>.*?<span class="name profile_pointer"[^>]*>([^<]+)</span>.*?<span class="price"[^>]*title="([\d,]+)"""", RegexOptions.Singleline)]
    private static partial Regex RowRegex();
}

/// <summary>How rankers split price and salary over one role (e.g. "ST"): per slot, mean and the 10–90% range.</summary>
/// <param name="PriceShare">Share of the eleven's total price one slot of this role takes (0.15 = 15%).</param>
public sealed record RoleShare(string Role, int Slots, double PriceShare, double PriceShareLow, double PriceShareHigh,
    double Pay, int PayLow, int PayHigh, double Ovr);

/// <summary>The price and salary split of top rankers' squads, from squads worth at least <see cref="MinSquadValue"/>.</summary>
public sealed record RankerAllocation(int Squads, int Skipped, long MedianValue, double MedianPay, IReadOnlyDictionary<string, RoleShare> Roles)
{
    /// <summary>Squads cheaper than this are usually being rebuilt (sold cards, fillers) and are left out.</summary>
    public const long MinSquadValue = 1_000_000_000;

    /// <summary>Roles group positions the same way whatever the formation: LS/RS → ST, LCB/RCB → CB, LB/RB → FB, …</summary>
    public static string RoleOf(string position) => Formations.Normalize(position) switch
    {
        "GK" => "GK",
        "CB" => "CB",
        "LB" or "RB" or "LWB" or "RWB" => "FB",
        "CDM" => "CDM",
        "CM" => "CM",
        "CAM" => "CAM",
        "LM" or "RM" => "SM",
        "LW" or "RW" => "W",
        "CF" => "CF",
        _ => "ST",
    };

    public static string RoleName(string role) => role switch
    {
        "GK" => "골키퍼", "CB" => "센터백", "FB" => "풀백", "CDM" => "수비형 미드", "CM" => "중앙 미드", "CAM" => "공격형 미드",
        "SM" => "측면 미드", "W" => "윙어", "CF" => "중앙 공격수", _ => "스트라이커",
    };

    public RoleShare? For(string position) => Roles.GetValueOrDefault(RoleOf(position));

    /// <summary>Prices the rankers' eleven at today's market (their grades); squads with unknown cards are skipped.</summary>
    public static RankerAllocation Analyse(IEnumerable<RankerSquad> squads, IReadOnlyDictionary<long, MarketCard> cards, long minSquadValue = MinSquadValue)
    {
        var rows = new List<(string Role, double Share, int Pay, int Ovr)>();
        var values = new List<long>();
        var pays = new List<int>();
        var skipped = 0;
        foreach (var squad in squads)
        {
            var priced = squad.Starters.Select(p => (p, Card: cards.GetValueOrDefault(p.SpId))).ToList();
            if (priced.Count(x => x.Card is null) > 1) { skipped++; continue; }
            var total = priced.Sum(x => x.Card?.PriceAt(x.p.Grade) ?? 0);
            if (total < minSquadValue) { skipped++; continue; }
            values.Add(total);
            pays.Add(priced.Sum(x => x.Card?.Pay ?? 0));
            foreach (var (p, card) in priced.Where(x => x.Card is not null))
                rows.Add((RoleOf(p.Position), card!.PriceAt(p.Grade) / (double)total, card.Pay, card.OvrAt(Formations.Normalize(p.Position), p.Grade) ?? card.OvrAt(p.Grade)));
        }
        var roles = rows.GroupBy(r => r.Role).ToDictionary(g => g.Key, g =>
        {
            var shares = g.Select(r => r.Share).Order().ToList();
            var slotPays = g.Select(r => r.Pay).Order().ToList();
            return new RoleShare(g.Key, g.Count(), shares.Average(), Quantile(shares, 0.1), Quantile(shares, 0.9),
                slotPays.Average(), (int)Quantile(slotPays.Select(p => (double)p).ToList(), 0.1), (int)Math.Ceiling(Quantile(slotPays.Select(p => (double)p).ToList(), 0.9)),
                g.Average(r => r.Ovr));
        });
        values.Sort();
        pays.Sort();
        return new RankerAllocation(values.Count, skipped, values.Count > 0 ? values[values.Count / 2] : 0, pays.Count > 0 ? pays[pays.Count / 2] : 0, roles);
    }

    private static double Quantile(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        var pos = q * (sorted.Count - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}
