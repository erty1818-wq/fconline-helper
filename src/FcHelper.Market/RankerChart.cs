using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using FcHelper.NexonApi;

namespace FcHelper.Market;

/// <summary>A card top rankers field at a position, at the grade they use it, from the data center's daily chart.</summary>
public sealed record RankerPick(string Position, long SpId, string Name, int Grade, int Ovr, int Pay, int Users, double Share)
{
    public int PlayerId => (int)(SpId % 1_000_000);
}

public sealed record FormationUsage(string Formation, int Users, double Share);

/// <summary>How <see cref="Formation"/> did against <see cref="Opponent"/> in rankers' official matches yesterday.</summary>
public sealed record FormationMatchup(string Formation, string Opponent, int Wins, int Draws, int Losses)
{
    public int Games => Wins + Draws + Losses;
    public double WinRate => Games == 0 ? 0 : (Wins + 0.5 * Draws) / Games;
}

/// <param name="Id">Team colour id (0 when read from the old top-10 box, which has names only).</param>
public sealed record TeamColorUsage(string Name, int Users, double Share)
{
    public int Id { get; init; }
}

/// <summary>One day of the daily chart for a ranker range (e.g. 1-10000), as fetched.</summary>
public sealed record RankerChartData(string ChartDate, int RankFrom, int RankTo, IReadOnlyList<RankerPick> Picks,
    IReadOnlyList<FormationUsage> Formations, IReadOnlyList<FormationMatchup> Matchups, IReadOnlyList<TeamColorUsage> TeamColors);

public interface IRankerChartSource
{
    Task<RankerChartData> FetchAsync(int rankFrom, int rankTo, CancellationToken ct = default);
}

/// <summary>
/// Reads the daily chart ("데일리 차트 → 스쿼드") of the official data center: which cards, grades, formations and
/// team colours the chosen ranker range fielded yesterday in official matches. It updates daily at 12:00 (KST).
/// About 30 small GET requests per range; personal use, spaced by the shared limiter.
/// </summary>
public sealed partial class DataCenterChartClient(HttpClient http, RateLimiter limiter) : IRankerChartSource
{
    private const string Base = "https://fconline.nexon.com";
    private const int OfficialMatch = 50;

    /// <summary>Position filters of the chart (the API's spposition codes; side variants are grouped).</summary>
    public static readonly IReadOnlyDictionary<int, string> ChartPositions = new Dictionary<int, string>
    {
        [0] = "GK", [2] = "RWB", [3] = "RB", [5] = "CB", [7] = "LB", [8] = "LWB", [10] = "CDM", [12] = "RM", [14] = "CM",
        [16] = "LM", [18] = "CAM", [21] = "CF", [23] = "RW", [25] = "ST", [27] = "LW",
    };

    public async Task<RankerChartData> FetchAsync(int rankFrom, int rankTo, CancellationToken ct = default)
    {
        var page = await GetAsync($"/datacenter/dailysquad?n1Type={OfficialMatch}&n4StartRanking={rankFrom}&n4EndRanking={rankTo}", ct);
        var m = ChartDateRegex().Match(page);
        var date = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        string Q(string extra) => $"strDate={date}&n1Type={OfficialMatch}&n4StartRanking={rankFrom}&n4EndRanking={rankTo}&{extra}";

        var picks = new List<RankerPick>();
        foreach (var (code, name) in ChartPositions)
            picks.AddRange(ChartParser.Picks(name, await GetAsync($"/Datacenter/BestUsePositionPlayer?{Q($"n4Position={code}&isGroup=false")}", ct)));

        var formations = ChartParser.Formations(await GetAsync($"/Datacenter/BestFormationInfo?{Q("strType=usage")}", ct));
        var matchups = new List<FormationMatchup>();
        foreach (var f in formations)
            matchups.AddRange(ChartParser.Matchups(f.Formation, await GetAsync($"/Datacenter/FormationVsInfo?{Q($"strType=usage&strFormation={f.Formation}")}", ct)));
        // The chart page itself lists the 30 team colours rankers used most, with ids; the usage box only the top 10.
        var colors = ChartParser.TeamColorList(page);
        if (colors.Count == 0) colors = ChartParser.TeamColors(await GetAsync($"/Datacenter/BestTeamColorInfo?{Q("strType=usage")}", ct));
        return new RankerChartData(date, rankFrom, rankTo, picks, formations, matchups, colors);
    }

    private async Task<string> GetAsync(string path, CancellationToken ct)
    {
        await limiter.WaitAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, Base + path);
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        req.Headers.UserAgent.ParseAdd("FcHelper/0.5 (personal use)");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"data center {(int)res.StatusCode}", null, res.StatusCode);
        return await res.Content.ReadAsStringAsync(ct);
    }

    [GeneratedRegex(@"id=""strDate""[^>]*value=""([\d.]+)""|value=""([\d.]+)""[^>]*id=""strDate""")]
    private static partial Regex ChartDateRegex();
}

public static partial class ChartParser
{
    public static IReadOnlyList<RankerPick> Picks(string position, string html)
    {
        var picks = new List<RankerPick>();
        foreach (var chunk in html.Split("class=\"item_list").Skip(1))
        {
            var link = LinkRegex().Match(chunk);
            var users = UsersRegex().Match(chunk);
            if (!link.Success || !users.Success) continue;
            picks.Add(new RankerPick(position,
                long.Parse(link.Groups[1].Value, CultureInfo.InvariantCulture),
                WebUtility.HtmlDecode(NameRegex().Match(chunk).Groups[1].Value.Trim()),
                I(link.Groups[2].Value), I(OvrRegex().Match(chunk).Groups[1].Value), I(PayRegex().Match(chunk).Groups[1].Value),
                I(users.Groups[1].Value.Replace(",", "")), D(users.Groups[2].Value) / 100));
        }
        return picks;
    }

    public static IReadOnlyList<FormationUsage> Formations(string html) =>
        FormationRegex().Matches(Text(html))
            .Select(m => new FormationUsage(m.Groups[1].Value, I(m.Groups[2].Value.Replace(",", "")), D(m.Groups[3].Value) / 100))
            .GroupBy(f => f.Formation).Select(g => g.First()).ToList();

    /// <summary>
    /// Rows read "4-2-4 222명 (4.3%) 79.3% 73승 5무 19패 20.7% 19승 5무 73패": the first ("win") block is the selected
    /// formation's record against that opponent (79.3% = 73 / (73 + 19)), the second the opponent's.
    /// </summary>
    public static IReadOnlyList<FormationMatchup> Matchups(string formation, string html) =>
        MatchupRegex().Matches(Text(html))
            .Select(m => new FormationMatchup(formation, m.Groups[1].Value, I(m.Groups[3].Value), I(m.Groups[4].Value), I(m.Groups[5].Value)))
            .ToList();

    public static IReadOnlyList<TeamColorUsage> TeamColors(string html) =>
        TeamColorRegex().Matches(Text(html))
            .Select(m => new TeamColorUsage(m.Groups[1].Value.Trim(), I(m.Groups[2].Value.Replace(",", "")), D(m.Groups[3].Value) / 100))
            .ToList();

    /// <summary>The team colour strip of the daily chart page: name, "409명 (6.9%)" and the id in its link.</summary>
    public static IReadOnlyList<TeamColorUsage> TeamColorList(string html) =>
        TeamColorItemRegex().Matches(html)
            .Select(m => new TeamColorUsage(WebUtility.HtmlDecode(m.Groups[1].Value.Trim()), I(m.Groups[2].Value.Replace(",", "")), D(m.Groups[3].Value) / 100)
                { Id = I(m.Groups[4].Value) })
            .GroupBy(t => t.Id).Select(g => g.First()).ToList();

    private static string Text(string html) =>
        WebUtility.HtmlDecode(SpaceRegex().Replace(TagRegex().Replace(ScriptRegex().Replace(html, " "), " "), " "));

    private static int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    [GeneratedRegex("""PlayerInfo\?spid=(\d+)&(?:amp;)?n1Strong=(\d+)""")] private static partial Regex LinkRegex();
    [GeneratedRegex("""([\d,]+)명\s*\(([\d.]+)%\)""")] private static partial Regex UsersRegex();
    [GeneratedRegex("""class="name"><span>([^<]+)</span>""")] private static partial Regex NameRegex();
    [GeneratedRegex("""class="ovr value">\s*(\d+)""")] private static partial Regex OvrRegex();
    [GeneratedRegex("""class="pay">.*?<span>(\d+)</span>""", RegexOptions.Singleline)] private static partial Regex PayRegex();
    [GeneratedRegex(@"(\d(?:-\d){2,4})\s+([\d,]+)명\s*\(([\d.]+)%\)")] private static partial Regex FormationRegex();
    [GeneratedRegex(@"(\d(?:-\d){2,4})\s+[\d,]+명\s*\([\d.]+%\)\s*([\d.]+)%\s*(\d+)승\s*(\d+)무\s*(\d+)패\s*[\d.]+%\s*(\d+)승\s*(\d+)무\s*(\d+)패")] private static partial Regex MatchupRegex();
    [GeneratedRegex(@"([^\d%()]+?)\s+([\d,]+)명\s*\(([\d.]+)%\)")] private static partial Regex TeamColorRegex();
    [GeneratedRegex("""<div class="txt">([^<]+)</div>\s*<div class="per">([\d,]+)명\s*\(([\d.]+)%\)</div>\s*<a[^>]*GetTeamColorVsInfo\('(\d+)'\)""")]
    private static partial Regex TeamColorItemRegex();
    [GeneratedRegex("<script.*?</script>", RegexOptions.Singleline)] private static partial Regex ScriptRegex();
    [GeneratedRegex("<[^>]+>")] private static partial Regex TagRegex();
    [GeneratedRegex(@"\s+")] private static partial Regex SpaceRegex();
}
