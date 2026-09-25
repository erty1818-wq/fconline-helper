using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FcHelper.NexonApi;

namespace FcHelper.Market;

public enum TeamColorKind { Club, Nation, Special, SeasonClub, Other }

/// <summary>One level of a team colour: how many members it needs and what it adds.</summary>
public sealed record TeamColorLevel(int Level, int Members, int AllStats, IReadOnlyList<string> Effects);

/// <summary>
/// A team colour (팀컬러) from the data center: 1xxx club, 2xxx nation, 3xxxx special, 4xxxx club season.
/// Levels come from its detail page; the members from the player list filtered by it.
/// </summary>
public sealed record TeamColor(int Id, string Name, TeamColorKind Kind, int MaxMembers, IReadOnlyList<TeamColorLevel> Levels)
{
    public static TeamColorKind KindOf(int id) => id switch
    {
        >= 40000 => TeamColorKind.SeasonClub,
        >= 30000 => TeamColorKind.Special,
        >= 2000 and < 3000 => TeamColorKind.Nation,
        >= 1000 and < 2000 => TeamColorKind.Club,
        _ => TeamColorKind.Other,
    };

    /// <summary>The highest level reached with this many members in the squad, or null.</summary>
    public TeamColorLevel? LevelFor(int members) => Levels.Where(l => members >= l.Members).MaxBy(l => l.Level);
}

/// <summary>Team colour catalogue, level rules and members from the data center (personal use, cached by the caller).</summary>
public sealed partial class DataCenterTeamColorClient(HttpClient http, RateLimiter limiter, IMarketListSource lists)
{
    private const string Base = "https://fconline.nexon.com";

    /// <summary>All team colours with their top-level summary (one ~1 MB page).</summary>
    public async Task<IReadOnlyList<TeamColor>> CatalogAsync(CancellationToken ct = default) =>
        TeamColorParser.Catalog(await GetAsync("/datacenter/teamcolor", ct));

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
    public static IReadOnlyList<TeamColor> Catalog(string html) =>
        CatalogRegex().Matches(html).Select(m =>
        {
            var id = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var members = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var level = int.TryParse(DigitRegex().Match(m.Groups[4].Value).Value, out var l) ? l : 1;
            var effects = EffectItemRegex().Matches(m.Groups[5].Value).Select(e => WebUtility.HtmlDecode(e.Groups[1].Value.Trim())).ToList();
            // The catalogue shows only the top level; the full ladder comes from the detail page.
            return new TeamColor(id, WebUtility.HtmlDecode(m.Groups[3].Value.Trim()), TeamColor.KindOf(id), members,
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
    private static int AllStatsOf(IEnumerable<string> effects) =>
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
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<TeamColor>> CatalogAsync(CancellationToken ct = default)
    {
        if (store.GetValue("teamcolor.catalog") is { } v && Now - v.UpdatedAt < Ttl) return store.LoadTeamColors();
        var catalog = await client.CatalogAsync(ct);
        store.SaveTeamColors(catalog, Now);
        store.SetValue("teamcolor.catalog", catalog.Count.ToString(CultureInfo.InvariantCulture), Now);
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
            store.SaveTeamColorMembers(id, await client.MembersAsync(id, ct));
            store.SetValue(key, "1", Now);
        }
        color = store.LoadTeamColors().First(t => t.Id == id);
        return (color, store.LoadTeamColorMembers(id));
    }
}
