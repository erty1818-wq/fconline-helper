using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FcHelper.NexonApi;

namespace FcHelper.Market;

/// <summary>The data center's three team colour tabs. The game lets one of each be active at a time.</summary>
public enum TeamColorCategory
{
    /// <summary>소속: club, nation and other affiliations. The bonus goes to the whole squad once enough members play.</summary>
    Affiliation,
    /// <summary>특성 (관계): e.g. "2026 프랑스", "갈락티코 1기". The bonus goes only to the members.</summary>
    Feature,
    /// <summary>강화: e.g. "금빛 물결" (8강 이상). Treated as whole squad [추정: 공식 설명에 적용 대상이 없음].</summary>
    Enhance,
}

public enum TeamColorKind { Club, Nation, Feature, Enhance, Other }

/// <summary>One level of a team colour: how many members it needs and what it adds.</summary>
public sealed record TeamColorLevel(int Level, int Members, int AllStats, IReadOnlyList<string> Effects)
{
    /// <summary>
    /// What the level adds to the OVR of a card at <paramref name="position"/>: "전체 능력치 +N" adds N; a single stat
    /// adds its bonus times that stat's weight in the position's OVR formula [추정: 가중치는 공개되지 않은 근사값].
    /// </summary>
    public double OvrGain(string position) =>
        AllStats + TeamColorParser.StatBonuses(Effects).Sum(kv => kv.Value * OvrWeights.Of(position, kv.Key));
}

/// <summary>
/// A team colour (팀컬러) from the data center. Ids do not tell the category (소속 also has 3xxxx and 4xxxx ids), so it
/// comes from the tab the colour is listed on. Levels come from its detail page; the members from the player list.
/// </summary>
public sealed record TeamColor(int Id, string Name, TeamColorCategory Category, int MaxMembers, IReadOnlyList<TeamColorLevel> Levels)
{
    public TeamColorKind Kind => Category switch
    {
        TeamColorCategory.Feature => TeamColorKind.Feature,
        TeamColorCategory.Enhance => TeamColorKind.Enhance,
        _ => Id switch
        {
            >= 1000 and < 2000 => TeamColorKind.Club,
            >= 2000 and < 3000 => TeamColorKind.Nation,
            _ => TeamColorKind.Other,
        },
    };

    /// <summary>
    /// 소속 (and 강화) bonuses reach every starter once the level is met; 특성 bonuses only the member cards: with
    /// "2026 프랑스" 8명 and "프랑스" 11명, the 2026 cards get both and everyone else the 프랑스 bonus only.
    /// </summary>
    public bool AppliesToSquad => Category != TeamColorCategory.Feature;

    /// <summary>The highest level reached with this many members in the squad, or null.</summary>
    public TeamColorLevel? LevelFor(int members) => LevelIndexFor(members) is var i and >= 0 ? Levels[i] : null;

    public int LevelIndexFor(int members)
    {
        var best = -1;
        for (var i = 0; i < Levels.Count; i++)
            if (members >= Levels[i].Members && (best < 0 || Levels[i].Level > Levels[best].Level)) best = i;
        return best;
    }

    /// <summary>
    /// 강화 colours count cards by grade, not by name: 백금빛 물결 +11 이상, 금빛 +8, 은빛 +5, 동빛 +3 (5명 / 8명 단계).
    /// Null for other colours and for 초심자 가호 (a beginners' colour).
    /// </summary>
    public int? EnhanceMinGrade => Category != TeamColorCategory.Enhance ? null
        : Name.Contains("백금빛") ? 11 : Name.Contains("금빛") ? 8 : Name.Contains("은빛") ? 5 : Name.Contains("동빛") ? 3 : null;

    public static string CategoryLabel(TeamColorCategory c) => c switch
    {
        TeamColorCategory.Affiliation => "소속",
        TeamColorCategory.Feature => "특성",
        _ => "강화",
    };
}

/// <summary>A team colour a squad is built on, with the cards that count for it (by grade for 강화 colours).</summary>
public sealed record TeamColorTarget(TeamColor Color, IReadOnlySet<long> Members)
{
    public bool Counts(long spId, int grade) => Color.EnhanceMinGrade is { } min ? grade >= min : Members.Contains(spId);

    /// <summary>
    /// The colours' OVR gain for one card, as the game applies them: every colour's bonus adds up, except that of the
    /// 강화 colours only the best one counts (one colour per kind).
    /// </summary>
    public static double Gain(IReadOnlyList<TeamColorTarget> targets, IReadOnlyList<int> counts, Func<int, bool> applies, string position)
    {
        double total = 0, enhance = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            if (targets[i].Color.LevelFor(counts[i]) is not { } level || !applies(i)) continue;
            var gain = level.OvrGain(position);
            if (targets[i].Color.Category == TeamColorCategory.Enhance) enhance = Math.Max(enhance, gain);
            else total += gain;
        }
        return total + enhance;
    }
}

/// <summary>A team colour as it ends up in a squad: how many members play and the level they reach.</summary>
public sealed record AppliedTeamColor(TeamColor Color, int Members, TeamColorLevel? Level);

/// <summary>
/// Approximate weight of each stat in a position's OVR, used only to value single-stat team colour bonuses. The game
/// does not publish its formula; these follow the long-known FIFA ratings formula and sum to 1 per position [추정].
/// </summary>
public static class OvrWeights
{
    private static readonly Dictionary<string, Dictionary<string, double>> ByGroup = new()
    {
        ["ST"] = W(("골 결정력", .18), ("위치 선정", .13), ("헤더", .10), ("슛 파워", .10), ("반응 속도", .08), ("드리블", .07), ("볼 컨트롤", .10),
            ("발리슛", .02), ("중거리 슛", .03), ("가속력", .04), ("속력", .05), ("몸싸움", .05), ("짧은 패스", .05)),
        ["CF"] = W(("골 결정력", .11), ("위치 선정", .13), ("헤더", .02), ("슛 파워", .05), ("반응 속도", .09), ("드리블", .14), ("볼 컨트롤", .15),
            ("짧은 패스", .09), ("중거리 슛", .04), ("가속력", .05), ("속력", .05), ("시야", .08)),
        ["W"] = W(("크로스", .09), ("골 결정력", .10), ("짧은 패스", .09), ("드리블", .16), ("볼 컨트롤", .14), ("가속력", .07), ("속력", .06),
            ("민첩성", .03), ("반응 속도", .07), ("위치 선정", .09), ("시야", .06), ("중거리 슛", .04)),
        ["CAM"] = W(("짧은 패스", .16), ("볼 컨트롤", .15), ("드리블", .13), ("시야", .14), ("위치 선정", .09), ("반응 속도", .07), ("골 결정력", .07),
            ("중거리 슛", .05), ("가속력", .04), ("민첩성", .03), ("긴 패스", .04), ("속력", .03)),
        ["SM"] = W(("크로스", .10), ("짧은 패스", .11), ("드리블", .15), ("볼 컨트롤", .13), ("가속력", .07), ("속력", .06), ("스태미너", .05),
            ("반응 속도", .07), ("위치 선정", .08), ("시야", .07), ("긴 패스", .05), ("중거리 슛", .06)),
        ["CM"] = W(("짧은 패스", .17), ("긴 패스", .13), ("시야", .13), ("볼 컨트롤", .14), ("드리블", .07), ("반응 속도", .08), ("가로채기", .05),
            ("위치 선정", .06), ("태클", .05), ("중거리 슛", .04), ("스태미너", .06), ("적극성", .02)),
        ["CDM"] = W(("짧은 패스", .14), ("긴 패스", .10), ("가로채기", .14), ("대인 수비", .12), ("태클", .07), ("슬라이딩 태클", .05),
            ("볼 컨트롤", .10), ("반응 속도", .07), ("몸싸움", .06), ("스태미너", .06), ("적극성", .05), ("시야", .04)),
        ["CB"] = W(("대인 수비", .14), ("태클", .17), ("슬라이딩 태클", .14), ("가로채기", .13), ("헤더", .10), ("몸싸움", .10), ("반응 속도", .05),
            ("점프", .03), ("짧은 패스", .05), ("볼 컨트롤", .04), ("적극성", .05)),
        ["FB"] = W(("가속력", .05), ("속력", .07), ("스태미너", .08), ("반응 속도", .08), ("가로채기", .12), ("볼 컨트롤", .07), ("크로스", .09),
            ("헤더", .04), ("짧은 패스", .07), ("대인 수비", .08), ("태클", .11), ("슬라이딩 태클", .14)),
        ["WB"] = W(("가속력", .04), ("속력", .06), ("스태미너", .10), ("반응 속도", .08), ("가로채기", .12), ("볼 컨트롤", .08), ("크로스", .12),
            ("드리블", .04), ("짧은 패스", .10), ("대인 수비", .07), ("태클", .08), ("슬라이딩 태클", .11)),
        ["GK"] = W(("GK 다이빙", .24), ("GK 핸들링", .22), ("GK 킥", .04), ("GK 반응속도", .24), ("GK 위치 선정", .22), ("반응 속도", .04)),
    };

    public static double Of(string position, string stat) =>
        ByGroup.TryGetValue(GroupOf(Formations.Normalize(position)), out var w) ? w.GetValueOrDefault(stat) : 0;

    private static string GroupOf(string position) => position switch
    {
        "ST" or "CF" or "CAM" or "CM" or "CDM" or "CB" or "GK" => position,
        "LW" or "RW" => "W",
        "LM" or "RM" => "SM",
        "LB" or "RB" => "FB",
        "LWB" or "RWB" => "WB",
        _ => "",
    };

    private static Dictionary<string, double> W(params (string Stat, double Weight)[] w) => w.ToDictionary(x => x.Stat, x => x.Weight);
}

/// <summary>Team colour catalogue, level rules and members from the data center (personal use, cached by the caller).</summary>
public sealed partial class DataCenterTeamColorClient(HttpClient http, RateLimiter limiter, IMarketListSource lists)
{
    private const string Base = "https://fconline.nexon.com";

    /// <summary>All team colours with their top-level summary, one page per tab (소속 / 특성 / 강화).</summary>
    public async Task<IReadOnlyList<TeamColor>> CatalogAsync(CancellationToken ct = default)
    {
        var all = new List<TeamColor>();
        foreach (var (category, tab) in new[] { (TeamColorCategory.Affiliation, "affiliation"), (TeamColorCategory.Feature, "feature"), (TeamColorCategory.Enhance, "enhance") })
            all.AddRange(TeamColorParser.Catalog(await GetAsync($"/datacenter/teamcolor?strTeamColorCategory={tab}", ct), category));
        return all.GroupBy(t => t.Id).Select(g => g.First()).ToList();
    }

    public async Task<IReadOnlyList<TeamColorLevel>> LevelsAsync(int id, CancellationToken ct = default) =>
        TeamColorParser.Levels(await GetAsync($"/datacenter/TeamColorDetail?teamcolorid={id}", ct));

    /// <summary>Cards that count for the team colour, over the squad-level OVR range.</summary>
    public async Task<IReadOnlyList<long>> MembersAsync(int id, CancellationToken ct = default)
    {
        var found = new List<long>();
        var stack = new Stack<(int Lo, int Hi)>();
        stack.Push((MarketGroups.OvrMin, MarketGroups.OvrMax + 15));
        while (stack.Count > 0)
        {
            var (lo, hi) = stack.Pop();
            var rows = await lists.QueryAsync(new ListQuery("", lo, hi, MarketGroups.All[0].Stats[0]) { TeamColor = id }, ct);
            if (rows.Count >= ListQuery.MaxRows && lo < hi)
            {
                var mid = (lo + hi) / 2;
                stack.Push((lo, mid));
                stack.Push((mid + 1, hi));
            }
            else found.AddRange(rows.Select(r => r.SpId));
        }
        return found.Distinct().ToList();
    }

    /// <summary>The team colours one card counts for (소속 / 관계 / 강화), read from its detail fragment.</summary>
    public async Task<IReadOnlyList<int>> CardTeamColorsAsync(long spId, CancellationToken ct = default)
    {
        await limiter.WaitAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, Base + "/datacenter/PlayerAbility")
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
        return TeamColorParser.CardTeamColors(await res.Content.ReadAsStringAsync(ct));
    }

    private async Task<string> GetAsync(string path, CancellationToken ct)
    {
        await limiter.WaitAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, Base + path);
        req.Headers.Add("X-Requested-With", "XMLHttpRequest"); // the detail page redirects plain requests
        req.Headers.UserAgent.ParseAdd("FcHelper/0.5 (personal use)");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"data center {(int)res.StatusCode}", null, res.StatusCode);
        return await res.Content.ReadAsStringAsync(ct);
    }
}

public static partial class TeamColorParser
{
    public static IReadOnlyList<TeamColor> Catalog(string html, TeamColorCategory category) =>
        CatalogRegex().Matches(html).Select(m =>
        {
            var id = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var members = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var level = int.TryParse(DigitRegex().Match(m.Groups[4].Value).Value, out var l) ? l : 1;
            var effects = EffectItemRegex().Matches(m.Groups[5].Value).Select(e => WebUtility.HtmlDecode(e.Groups[1].Value.Trim())).ToList();
            // The catalogue shows only the top level; the full ladder comes from the detail page.
            return new TeamColor(id, WebUtility.HtmlDecode(m.Groups[3].Value.Trim()), category, members,
                [new TeamColorLevel(level, members, AllStatsOf(effects), effects)]);
        }).GroupBy(t => t.Id).Select(g => g.First()).ToList();

    public static IReadOnlyList<TeamColorLevel> Levels(string html)
    {
        var start = html.IndexOf("적용조건", StringComparison.Ordinal);
        var end = html.IndexOf("적용 선수 목록", StringComparison.Ordinal);
        if (start < 0) return [];
        var section = html[start..(end > start ? end : html.Length)];
        return LevelRegex().Matches(section).Select(m =>
        {
            var effects = LiRegex().Matches(m.Groups[3].Value).Select(e => WebUtility.HtmlDecode(e.Groups[1].Value.Trim())).Where(e => e != "-").ToList();
            return new TeamColorLevel(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                AllStatsOf(effects), effects);
        }).ToList();
    }

    /// <summary>Team colour ids in a card's selector ("selector_item tdefault2001", "tspecial…"); 0 is the "none" entry.</summary>
    public static IReadOnlyList<int> CardTeamColors(string html) =>
        CardTeamColorRegex().Matches(html).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Where(id => id > 0).Distinct().ToList();

    [GeneratedRegex("""selector_item t(?:default|special|enhance)(\d+)""")] private static partial Regex CardTeamColorRegex();

    /// <summary>"전체 능력치 +4" is the part that raises every stat of the members, i.e. roughly their OVR.</summary>
    private static int AllStatsOf(IEnumerable<string> effects) => AllStatsOfEffects(effects);

    /// <summary>Single-stat bonuses of a level ("속력 +3" → 속력: 3); "전체 능력치" is left to <see cref="TeamColorLevel.AllStats"/>.</summary>
    public static IReadOnlyDictionary<string, int> StatBonuses(IEnumerable<string> effects)
    {
        var result = new Dictionary<string, int>();
        foreach (var e in effects)
        {
            var m = StatBonusRegex().Match(e);
            if (!m.Success) continue;
            var stat = m.Groups[1].Value;
            if (stat == "전체 능력치") continue;
            result[stat] = result.GetValueOrDefault(stat) + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        return result;
    }

    [GeneratedRegex(@"^\s*(.+?)\s*\+(\d+)\s*$")] private static partial Regex StatBonusRegex();

    private static int AllStatsOfEffects(IEnumerable<string> effects) =>
        effects.Select(e => AllStatsRegex().Match(e)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();

    [GeneratedRegex("""GetTeamColorDetail\((\d+)\).*?<div class="num">(\d+)</div>\s*</div>\s*<div class="name">([^<]+)</div>\s*<div class="level">([^<]+)</div>\s*<div class="desc">(.*?)</div>""", RegexOptions.Singleline)]
    private static partial Regex CatalogRegex();
    [GeneratedRegex("""<span class="item">([^<]+)</span>""")] private static partial Regex EffectItemRegex();
    [GeneratedRegex("""<div class="tit">(\d+) 단계</div>.*?<div class="desc">(\d+)명</div>\s*<div class="ap_list">(.*?)</ul>""", RegexOptions.Singleline)]
    private static partial Regex LevelRegex();
    [GeneratedRegex("<li>([^<]+)</li>")] private static partial Regex LiRegex();
    [GeneratedRegex(@"전체 능력치 \+(\d+)")] private static partial Regex AllStatsRegex();
    [GeneratedRegex(@"\d+")] private static partial Regex DigitRegex();
}

/// <summary>Team colours in SQLite: the catalogue and levels (weekly) and members of the ones the user asks for.</summary>
public sealed class TeamColorCache(MarketStore store, DataCenterTeamColorClient client, TimeProvider? time = null)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(7);
    /// <summary>v2: categories come from the three tabs (v1 guessed a kind from the id).</summary>
    private const string CatalogKey = "teamcolor.catalog.v2";
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<TeamColor>> CatalogAsync(CancellationToken ct = default)
    {
        if (store.GetValue(CatalogKey) is { } v && Now - v.UpdatedAt < Ttl) return store.LoadTeamColors();
        var catalog = await client.CatalogAsync(ct);
        // The catalogue shows only the top level: keep the full ladders fetched earlier instead of overwriting them.
        var known = store.LoadTeamColors().ToDictionary(t => t.Id);
        catalog = catalog.Select(t => known.TryGetValue(t.Id, out var k) && k.Levels.Count > t.Levels.Count ? t with { Levels = k.Levels } : t).ToList();
        store.SaveTeamColors(catalog, Now);
        store.SetValue(CatalogKey, catalog.Count.ToString(CultureInfo.InvariantCulture), Now);
        return store.LoadTeamColors();
    }

    public Task<IReadOnlyList<int>> CardTeamColorsAsync(long spId, CancellationToken ct = default) => client.CardTeamColorsAsync(spId, ct);

    /// <summary>The team colour with its full level ladder and members, fetched on first use and then weekly.</summary>
    public async Task<(TeamColor Color, IReadOnlySet<long> Members)?> GetAsync(int id, CancellationToken ct = default)
    {
        var color = (await CatalogAsync(ct)).FirstOrDefault(t => t.Id == id);
        if (color is null) return null;
        var key = $"teamcolor.detail.{id}";
        if (store.GetValue(key) is not { } v || Now - v.UpdatedAt >= Ttl)
        {
            var levels = await client.LevelsAsync(id, ct);
            if (levels.Count > 0) color = color with { Levels = levels };
            store.SaveTeamColors([color], Now);
            // 강화 colours go by grade: their "members" would be every card, so they are not fetched.
            if (color.Category != TeamColorCategory.Enhance) store.SaveTeamColorMembers(id, await client.MembersAsync(id, ct));
            store.SetValue(key, "1", Now);
        }
        color = store.LoadTeamColors().First(t => t.Id == id);
        return (color, store.LoadTeamColorMembers(id));
    }
}
