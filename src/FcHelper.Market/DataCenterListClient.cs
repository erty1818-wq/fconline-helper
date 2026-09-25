using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using FcHelper.NexonApi;

namespace FcHelper.Market;

/// <summary>One player-list query. The endpoint returns at most 200 rows and has no paging, so callers slice.</summary>
public sealed record ListQuery(string Positions, int OvrMin, int OvrMax, string[] Stats)
{
    public const int MaxRows = 200;
    public int PayMin { get; init; } = 4;
    public int PayMax { get; init; } = 99;
    public string Trait { get; init; } = "";
    public int SkillMove { get; init; }
    public string Body { get; init; } = "";
    /// <summary>",100," style season filter (season id = the first three digits of spid).</summary>
    public string Seasons { get; init; } = "";
}

/// <summary>A list row before it is assigned to a group: stats hold only the four the query asked for.</summary>
public sealed record ListRow(long SpId, string Name, string Season, int Pay, int Ovr1, int WeakFoot,
    double? Rating, int RatingCount, Dictionary<int, long> Prices, Dictionary<string, int> Stats);

public interface IMarketListSource
{
    Task<IReadOnlyList<ListRow>> QueryAsync(ListQuery query, CancellationToken ct = default);
}

/// <summary>
/// POST /datacenter/PlayerList on the official data center, the request its player search makes. Personal use: one
/// request every two seconds (shared limiter), an honest User-Agent, and no more than the analysis needs.
/// </summary>
public sealed class DataCenterListClient(HttpClient http, RateLimiter limiter) : IMarketListSource
{
    private static readonly Uri Url = new("https://fconline.nexon.com/datacenter/PlayerList");

    public async Task<IReadOnlyList<ListRow>> QueryAsync(ListQuery q, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["n8PlayerGrade1Min"] = "0", ["n8PlayerGrade1Max"] = "999900000", ["n1Confederation"] = "0", ["n4LeagueId"] = "0",
            ["strSeason"] = q.Seasons, ["strPosition"] = q.Positions, ["strPhysical"] = q.Body, ["preferredfoot"] = "0",
            ["n1FootAblity"] = "0", ["n1SkillMove"] = q.SkillMove.ToString(CultureInfo.InvariantCulture), ["n1InterationalRep"] = "0",
            ["n4BirthMonth"] = "0", ["n4BirthDay"] = "0", ["n4TeamId"] = "0", ["n4NationId"] = "0",
            ["strAbility1"] = "", ["strAbility2"] = "", ["strAbility3"] = "", ["strTrait1"] = q.Trait, ["strTrait2"] = "", ["strTrait3"] = "",
            ["strTraitNon1"] = "", ["strTraitNon2"] = "", ["strTraitNon3"] = "", ["n1Strong"] = "1", ["n1Grow"] = "0", ["n1TeamColor"] = "0",
            ["strSkill1"] = q.Stats[0], ["strSkill2"] = q.Stats[1], ["strSkill3"] = q.Stats[2], ["strSkill4"] = q.Stats[3],
            ["strSearchStatus"] = "off", ["strOrderby"] = "", ["teamcolorid"] = "0", ["strTeamColorCategory"] = "", ["n1History"] = "0",
            ["n4PlayYear"] = "0", ["IsSummaryPlayer"] = "0", ["strPlayerName"] = "", ["strTeamName"] = "", ["strNationName"] = "",
            ["strTeamColorName"] = "", ["n4OvrMin"] = I(q.OvrMin), ["n4OvrMax"] = I(q.OvrMax), ["n4SalaryMin"] = I(q.PayMin),
            ["n4SalaryMax"] = I(q.PayMax), ["n1Ability1Min"] = "40", ["n1Ability1Max"] = "200", ["n1Ability2Min"] = "40",
            ["n1Ability2Max"] = "200", ["n1Ability3Min"] = "40", ["n1Ability3Max"] = "200", ["n4BirthYearMin"] = "1900",
            ["n4BirthYearMax"] = "2010", ["n4HeightMin"] = "140", ["n4HeightMax"] = "250", ["n4WeightMin"] = "40",
            ["n4WeightMax"] = "200", ["n4AvgPointMin"] = "0", ["n4AvgPointMax"] = "10", ["n4PageNo"] = "1",
        };
        for (var attempt = 1; ; attempt++)
        {
            await limiter.WaitAsync(ct);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new FormUrlEncodedContent(form) };
                req.Headers.Add("X-Requested-With", "XMLHttpRequest");
                req.Headers.UserAgent.ParseAdd("FcHelper/0.5 (personal use)");
                using var res = await http.SendAsync(req, ct);
                if (res.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"data center {(int)res.StatusCode}", null, res.StatusCode);
                return ListRowParser.Parse(await res.Content.ReadAsStringAsync(ct));
            }
            catch (Exception e) when (attempt < 4 && e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15 * attempt), ct);
            }
        }
    }

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
}

public static partial class ListRowParser
{
    public static IReadOnlyList<ListRow> Parse(string html)
    {
        var rows = new List<ListRow>();
        foreach (var chunk in html.Split("<div id=\"area_playerunit_").Skip(1))
        {
            var spid = SpIdRegex().Match(chunk);
            if (!spid.Success) continue;
            var foot = FootRegex().Match(chunk).Groups[1].Value;
            var left = FootLeftRegex().Match(foot);
            var right = FootRightRegex().Match(foot);
            var positions = PositionRegex().Matches(chunk).Select(m => int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)).ToList();
            var score = ScoreRegex().Match(chunk);
            rows.Add(new ListRow(
                long.Parse(spid.Groups[1].Value, CultureInfo.InvariantCulture),
                WebUtility.HtmlDecode(NameRegex().Match(chunk).Groups[1].Value.Trim()),
                SeasonRegex().Match(chunk).Groups[1].Value,
                Int(PayRegex().Match(chunk).Groups[1].Value),
                positions.Count == 0 ? 0 : positions.Max(),
                Math.Min(left.Success ? Int(left.Groups[1].Value) : 0, right.Success ? Int(right.Groups[1].Value) : 0),
                score.Success ? double.Parse(score.Groups[1].Value, CultureInfo.InvariantCulture) : null,
                score.Success ? Int(score.Groups[2].Value) : 0,
                PriceRegex().Matches(chunk).ToDictionary(m => Int(m.Groups[1].Value), m => long.Parse(m.Groups[2].Value.Replace(",", ""), CultureInfo.InvariantCulture)),
                StatRegex().Matches(chunk).GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => Int(g.First().Groups[2].Value))));
        }
        return rows;
    }

    private static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    [GeneratedRegex(@"^(\d+)")] private static partial Regex SpIdRegex();
    [GeneratedRegex(@"/season/([A-Za-z0-9_]+)\.png")] private static partial Regex SeasonRegex();
    [GeneratedRegex("""<div class="name">([^<]+)</div>""")] private static partial Regex NameRegex();
    [GeneratedRegex("""<span class="pay">(\d+)</span>""")] private static partial Regex PayRegex();
    [GeneratedRegex("""class="foot">(.*?)</div>""", RegexOptions.Singleline)] private static partial Regex FootRegex();
    [GeneratedRegex(@"L(\d)")] private static partial Regex FootLeftRegex();
    [GeneratedRegex(@"R(\d)")] private static partial Regex FootRightRegex();
    [GeneratedRegex("""<span class="txt">(\w+)</span><span class="skillData_\d+">(\d+)</span>""")] private static partial Regex PositionRegex();
    [GeneratedRegex("""data-type="(\w+)">\s*(\d+)""")] private static partial Regex StatRegex();
    [GeneratedRegex("""span_bp(\d+)"[^>]*title="([\d,]+)""")] private static partial Regex PriceRegex();
    [GeneratedRegex("""td_ar_score"><span>([\d.]+)\s*<em>\((\d+)\)""")] private static partial Regex ScoreRegex();
}
