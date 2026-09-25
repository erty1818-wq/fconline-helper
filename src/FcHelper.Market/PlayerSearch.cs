using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using FcHelper.NexonApi;

namespace FcHelper.Market;

public sealed record SearchSeason(int Id, string Code, string Name, string Icon);
public sealed record SearchPick(string Value, string Name);
public sealed record SearchClub(int Id, string Name, int LeagueId);
public sealed record SearchNation(int Id, string Name, int ConfederationId);

/// <summary>Everything the data center's player search offers to choose from, read from its search page (kept a week).</summary>
public sealed record SearchOptions(
    IReadOnlyList<SearchSeason> Seasons, IReadOnlyList<SearchPick> Leagues, IReadOnlyList<SearchClub> Clubs,
    IReadOnlyList<SearchPick> Confederations, IReadOnlyList<SearchNation> Nations, IReadOnlyList<SearchPick> TeamColors,
    IReadOnlyList<SearchPick> Traits, IReadOnlyList<SearchPick> Abilities, IReadOnlyList<SearchPick> Columns)
{
    /// <summary>Position buttons: label → the data center's position ids (ST also covers LS/RS).</summary>
    public static readonly IReadOnlyList<(string Label, string Group, string Ids)> Positions =
    [
        ("ST", "FW", "24,25,26"), ("CF", "FW", "20,21,22"), ("LW", "FW", "27"), ("RW", "FW", "23"),
        ("CAM", "MF", "17,18,19"), ("CM", "MF", "13,14,15"), ("CDM", "MF", "9,10,11"), ("LM", "MF", "16"), ("RM", "MF", "12"),
        ("CB", "DF", "1,4,5,6"), ("LWB", "DF", "8"), ("LB", "DF", "7"), ("RB", "DF", "3"), ("RWB", "DF", "2"),
        ("GK", "GK", "0"),
    ];

    public static readonly IReadOnlyList<(string Label, string Ids)> Bodies = [("마름", "1,4,7,11"), ("보통", "2,5,8,12"), ("건장", "3,6,9,13")];
}

/// <summary>One search, as the data center's player search takes it. Null / empty = any.</summary>
public sealed record PlayerSearchQuery
{
    /// <summary>Names, several with commas ("메시,호날두"); the data center matches parts of names.</summary>
    public string Names { get; init; } = "";
    public IReadOnlyList<int> Seasons { get; init; } = [];
    /// <summary>Data center position ids (from <see cref="SearchOptions.Positions"/>).</summary>
    public IReadOnlyList<string> PositionIds { get; init; } = [];
    public int League { get; init; }
    public int Club { get; init; }
    public int Confederation { get; init; }
    public int Nation { get; init; }
    public bool ClubHistory { get; init; }
    public int TeamColor { get; init; }
    public int Grade { get; init; } = 1;
    /// <summary>적응도 on the data center: 0 = +1, 4 = +5.</summary>
    public int Grow { get; init; }
    public int TeamColorLevel { get; init; }
    public int OvrMin { get; init; } = 40;
    public int OvrMax { get; init; } = 200;
    public int PayMin { get; init; } = 1;
    public int PayMax { get; init; } = 99;
    public int SkillMove { get; init; }
    public int Reputation { get; init; }
    public int PreferredFoot { get; init; }
    public int WeakFoot { get; init; }
    public IReadOnlyList<(string Stat, int Min, int Max)> Stats { get; init; } = [];
    public IReadOnlyList<string> Traits { get; init; } = [];
    public IReadOnlyList<string> NoTraits { get; init; } = [];
    public int HeightMin { get; init; } = 140;
    public int HeightMax { get; init; } = 250;
    public int WeightMin { get; init; } = 40;
    public int WeightMax { get; init; } = 200;
    public IReadOnlyList<string> Bodies { get; init; } = [];
    public int BirthYearMin { get; init; } = 1900;
    public int BirthYearMax { get; init; } = 2010;
    public int BirthMonth { get; init; }
    public int BirthDay { get; init; }
    public double RatingMin { get; init; }
    public double RatingMax { get; init; } = 10;
    /// <summary>The four stat columns of the result (data center keys such as "sprintspeed").</summary>
    public IReadOnlyList<string> Columns { get; init; } = ["sprintspeed", "acceleration", "strength", "stamina"];
    /// <summary>"overallrating", "salary", "n8playergrade1" (price), "n4AvgAssessmentPoint" (rating) or a column's stat.</summary>
    public string OrderBy { get; init; } = "overallrating";
    public bool Descending { get; init; } = true;
}

public static partial class SearchOptionsParser
{
    public static SearchOptions Parse(string html)
    {
        var s = ScriptRegex().Replace(html, "");
        var seasons = SeasonRegex().Matches(s).Select(m => new SearchSeason(Int(m.Groups[2].Value), m.Groups[1].Value.ToUpperInvariant(),
            WebUtility.HtmlDecode(m.Groups[3].Value), m.Groups[4].Value)).DistinctBy(x => x.Id).ToList();
        var leagues = LeagueRegex().Matches(s).Select(m => new SearchPick(m.Groups[1].Value, Decode(m.Groups[2].Value))).Where(x => x.Value != "0").DistinctBy(x => x.Value).ToList();
        var clubs = ClubRegex().Matches(s).Select(m => new SearchClub(Int(m.Groups[2].Value), Decode(m.Groups[3].Value), Int(m.Groups[1].Value)))
            .Where(c => c.Id > 0).DistinctBy(c => c.Id).ToList();
        var confeds = ConfedRegex().Matches(s).Select(m => new SearchPick(m.Groups[1].Value, Decode(m.Groups[2].Value))).Where(x => x.Value != "0").DistinctBy(x => x.Value).ToList();
        var nations = NationRegex().Matches(s).Select(m => new SearchNation(Int(m.Groups[2].Value), Decode(m.Groups[3].Value), Int(m.Groups[1].Value)))
            .Where(n => n.Id > 0).DistinctBy(n => n.Id).ToList();
        var colors = TeamColorRegex().Matches(s).Select(m => new SearchPick(m.Groups[1].Value, Decode(m.Groups[2].Value))).Where(x => x.Value != "0").DistinctBy(x => x.Value).ToList();
        return new SearchOptions(seasons, leagues, clubs, confeds, nations, colors, Picks(s, "strTrait1"), Picks(s, "strAbility1"), Picks(s, "strSkill1"));
    }

    private static List<SearchPick> Picks(string s, string field) =>
        Regex.Matches(s, @"SetAbilitySearch\('([^']*)', '" + field + @"'\)[^>]*>\s*<span>([^<]*)")
            .Select(m => new SearchPick(m.Groups[1].Value, Decode(m.Groups[2].Value))).Where(p => p.Value.Length > 0).DistinctBy(p => p.Value).ToList();

    private static string Decode(string s) => WebUtility.HtmlDecode(s.Trim());
    private static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    [GeneratedRegex("<script.*?</script>", RegexOptions.Singleline)] private static partial Regex ScriptRegex();
    [GeneratedRegex("""id="season_([a-z0-9_]+)" type="checkbox" class="season_check[^"]*" data-no="(\d+)"\s*>\s*<label[^>]*title="([^"]*)"[^>]*>\s*<img src="([^"]+)""")]
    private static partial Regex SeasonRegex();
    [GeneratedRegex("""SetTeamOption\((\d+)\);" class="league_item[^"]*"[^>]*><span>([^<]*)</span>""")] private static partial Regex LeagueRegex();
    [GeneratedRegex("""class="club_item selector_item _\d+" data-no="(\d+)" onclick="DataCenter\.SetAbilitySearch\('(\d*)', 'n4TeamId'\);[^"]*"><span>([^<]*)</span>""")]
    private static partial Regex ClubRegex();
    [GeneratedRegex("""SetNationOption\((\d+)\)[^>]*>\s*<span>([^<]*)""")] private static partial Regex ConfedRegex();
    [GeneratedRegex("""class="nationality_item selector_item _\d+" data-no="(\d+)" onclick="DataCenter\.SetAbilitySearch\('(\d*)', 'n4NationId'\);"><span>([^<]*)</span>""")]
    private static partial Regex NationRegex();
    [GeneratedRegex("""SetTeamColorId\('(\d+)','teamcolorid'\);"><span>([^<]*)</span>""")] private static partial Regex TeamColorRegex();
}

/// <summary>The data center's player search (personal use, the shared two-second limiter).</summary>
public sealed class PlayerSearchClient(HttpClient http, RateLimiter limiter)
{
    private static readonly Uri ListUrl = new("https://fconline.nexon.com/datacenter/PlayerList");
    private static readonly Uri PageUrl = new("https://fconline.nexon.com/datacenter");

    public async Task<SearchOptions> OptionsAsync(CancellationToken ct = default)
    {
        await limiter.WaitAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, PageUrl);
        req.Headers.UserAgent.ParseAdd("FcHelper/0.5 (personal use)");
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return SearchOptionsParser.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// The matching cards, up to <paramref name="maxRows"/>. One request returns at most 200 and has no paging, so a full
    /// answer is split by OVR and asked again (strongest first), a request every two seconds.
    /// </summary>
    public async Task<(IReadOnlyList<ListRow> Rows, bool Truncated)> SearchAsync(PlayerSearchQuery q, int maxRows = 800,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var rows = new List<ListRow>();
        var truncated = false;
        var ranges = new Stack<(int Lo, int Hi)>();
        ranges.Push((q.OvrMin, q.OvrMax));
        var requests = 0;
        while (ranges.Count > 0)
        {
            var (lo, hi) = ranges.Pop();
            if (rows.Count >= maxRows) { truncated = true; break; }
            progress?.Report(requests == 0 ? "공식 데이터센터에서 찾는 중…" : $"결과가 많아 OVR 구간을 나눠 더 받는 중… ({rows.Count}장)");
            var found = await QueryAsync(q with { OvrMin = lo, OvrMax = hi }, ct);
            requests++;
            if (found.Count >= ListQuery.MaxRows && lo < hi)
            {
                // Split: the upper half is asked first so the strongest cards come in before the limit.
                var mid = (lo + hi) / 2;
                ranges.Push((lo, mid));
                ranges.Push((mid + 1, hi));
                continue;
            }
            if (found.Count >= ListQuery.MaxRows) truncated = true; // one OVR alone holds more than 200
            rows.AddRange(found);
        }
        return (rows.DistinctBy(r => r.SpId).ToList(), truncated);
    }

    private async Task<IReadOnlyList<ListRow>> QueryAsync(PlayerSearchQuery q, CancellationToken ct)
    {
        string I(int v) => v.ToString(CultureInfo.InvariantCulture);
        string Ids(IEnumerable<string> ids) => ids.Any() ? "," + string.Join(",", ids) + "," : "";
        var stat = q.Stats.Concat(Enumerable.Repeat(("", 40, 200), 3)).Take(3).ToList();
        var traits = q.Traits.Concat(Enumerable.Repeat("", 3)).Take(3).ToList();
        var noTraits = q.NoTraits.Concat(Enumerable.Repeat("", 3)).Take(3).ToList();
        var columns = q.Columns.Concat(["sprintspeed", "acceleration", "strength", "stamina"]).Take(4).ToList();
        var form = new Dictionary<string, string>
        {
            ["n8PlayerGrade1Min"] = "0", ["n8PlayerGrade1Max"] = "999900000", ["n1Confederation"] = I(q.Confederation), ["n4LeagueId"] = I(q.League),
            ["strSeason"] = Ids(q.Seasons.Select(I)), ["strPosition"] = Ids(q.PositionIds), ["strPhysical"] = Ids(q.Bodies),
            ["preferredfoot"] = I(q.PreferredFoot), ["n1FootAblity"] = I(q.WeakFoot), ["n1SkillMove"] = I(q.SkillMove), ["n1InterationalRep"] = I(q.Reputation),
            ["n4BirthMonth"] = I(q.BirthMonth), ["n4BirthDay"] = I(q.BirthDay), ["n4TeamId"] = I(q.Club), ["n4NationId"] = I(q.Nation),
            ["strAbility1"] = stat[0].Item1, ["strAbility2"] = stat[1].Item1, ["strAbility3"] = stat[2].Item1,
            ["n1Ability1Min"] = I(stat[0].Item2), ["n1Ability1Max"] = I(stat[0].Item3), ["n1Ability2Min"] = I(stat[1].Item2),
            ["n1Ability2Max"] = I(stat[1].Item3), ["n1Ability3Min"] = I(stat[2].Item2), ["n1Ability3Max"] = I(stat[2].Item3),
            ["strTrait1"] = traits[0], ["strTrait2"] = traits[1], ["strTrait3"] = traits[2],
            ["strTraitNon1"] = noTraits[0], ["strTraitNon2"] = noTraits[1], ["strTraitNon3"] = noTraits[2],
            ["n1Strong"] = I(Math.Clamp(q.Grade, 1, 13)), ["n1Grow"] = I(q.Grow), ["n1TeamColor"] = I(q.TeamColorLevel),
            ["strSkill1"] = columns[0], ["strSkill2"] = columns[1], ["strSkill3"] = columns[2], ["strSkill4"] = columns[3],
            ["strSearchStatus"] = "on", ["strOrderby"] = $" {q.OrderBy} {(q.Descending ? "descending" : "")}".TrimEnd(),
            ["teamcolorid"] = I(q.TeamColor), ["strTeamColorCategory"] = "", ["n1History"] = q.ClubHistory ? "1" : "0",
            ["n4PlayYear"] = "0", ["IsSummaryPlayer"] = "0", ["strPlayerName"] = q.Names.Trim(), ["strTeamName"] = "", ["strNationName"] = "",
            ["strTeamColorName"] = "", ["n4OvrMin"] = I(q.OvrMin), ["n4OvrMax"] = I(q.OvrMax), ["n4SalaryMin"] = I(q.PayMin), ["n4SalaryMax"] = I(q.PayMax),
            ["n4BirthYearMin"] = I(q.BirthYearMin), ["n4BirthYearMax"] = I(q.BirthYearMax), ["n4HeightMin"] = I(q.HeightMin), ["n4HeightMax"] = I(q.HeightMax),
            ["n4WeightMin"] = I(q.WeightMin), ["n4WeightMax"] = I(q.WeightMax),
            ["n4AvgPointMin"] = q.RatingMin.ToString(CultureInfo.InvariantCulture), ["n4AvgPointMax"] = q.RatingMax.ToString(CultureInfo.InvariantCulture),
            ["n4PageNo"] = "1",
        };
        await limiter.WaitAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, ListUrl) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        req.Headers.UserAgent.ParseAdd("FcHelper/0.5 (personal use)");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"data center {(int)res.StatusCode}", null, res.StatusCode);
        return ListRowParser.Parse(await res.Content.ReadAsStringAsync(ct));
    }
}

/// <summary>초성 검색: "ㅁㅅ" matches 메시, "ㅋㄹㅅㅌㅇㄴ" 크리스티아누. The data center only takes whole letters, so names are found locally first.</summary>
public static class Initials
{
    private static readonly char[] Choseong = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ".ToCharArray();

    /// <summary>Whether the text is only initial consonants (and spaces), i.e. an initials search.</summary>
    public static bool IsInitials(string text) => text.Trim().Length > 0 && text.Trim().All(c => c == ' ' || Array.IndexOf(Choseong, c) >= 0);

    public static string Of(string name) =>
        new(name.Where(c => c != ' ').Select(c => c is >= '가' and <= '힣' ? Choseong[(c - '가') / 588] : c).ToArray());

    /// <summary>Whether a name contains the initials in a row ("ㅁㅅ" in 리오넬 메시).</summary>
    public static bool Matches(string name, string initials) => Of(name).Contains(initials.Replace(" ", ""), StringComparison.Ordinal);
}
