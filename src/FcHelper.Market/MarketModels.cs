using System.Globalization;
using System.Text.RegularExpressions;

namespace FcHelper.Market;

/// <summary>A stat that matters at a position in today's meta, with how much (3 핵심 / 2 중요 / 1 있으면 좋음).</summary>
public sealed record CoreStat(string Stat, int Weight);

/// <summary>
/// A position group of the data center's player list: which positions it covers (the API's spposition codes, as the
/// site's own filter sends them), the stats collected for it (passes of four: the list shows four stat columns per
/// request; pass 0 is re-read with every price refresh, the others once per card), the traits tagged, the core stats
/// of the position and its key new traits.
/// </summary>
public sealed record MarketGroup(string Key, string Name, string Positions, string[][] Stats, string[] Traits)
{
    public IEnumerable<string> AllStats => Stats.SelectMany(s => s).Distinct();

    /// <summary>What a top player looks at in this position (태연한경식이, "지금 메타에 필요한 포지션별 세부능력치").</summary>
    public IReadOnlyList<CoreStat> CoreStats { get; init; } = [];

    /// <summary>Stats that raise the OVR without helping in play at this position ("뻥스탯"), e.g. 헤더·슬라이딩 태클 at CB.</summary>
    public IReadOnlyList<string> InflatingStats { get; init; } = [];

    /// <summary>The new traits that decide this position's value (their price premium is worked out per group).</summary>
    public IReadOnlyList<string> KeyTraits { get; init; } = [];

    /// <summary>
    /// Core stat score: weighted average of the core stats minus the card's OVR, i.e. how much better (+) or worse (−)
    /// the stats that matter are than the OVR suggests. Null when too few of them were collected.
    /// </summary>
    public double? CoreGap(MarketCard c)
    {
        var have = CoreStats.Where(s => c.Stats.ContainsKey(s.Stat) && s.Stat is not ("height" or "weight")).ToList();
        if (have.Count < 3) return null;
        return have.Sum(s => s.Weight * (c.Stats[s.Stat] - c.Ovr1)) / (double)have.Sum(s => s.Weight);
    }
}

public static class MarketGroups
{
    private static readonly string[] AttackTraits =
        ["라인 브레이커", "트릭스터", "아크로바틱 피니셔", "스피드스터", "타이탄", "프레데터", "레이저 슈터", "크로스 포쳐", "파이터", "2개의 심장"];
    private static readonly string[] MidTraits = ["커맨더", "레이저 슈터", "와일드 태클러", "체이서", "파이터", "2개의 심장", "블로커", "타이탄", "스피드스터", "트릭스터"];
    private static readonly string[] DefTraits = ["파이터", "체이서", "와일드 태클러", "블로커", "커맨더", "타이탄", "스피드스터"];

    // Core stats from the video (2024 넥스트필드 이후 메타). 3 = 핵심, 2 = 중요, 1 = 있으면 좋음.
    private static readonly CoreStat[] ForwardCore =
    [
        new("sprintspeed", 3), new("acceleration", 3), new("agility", 2), new("balance", 3), new("finishing", 3), new("reactions", 2),
        new("composure", 2), new("volleys", 2), new("headingaccuracy", 2), new("shotpower", 2), new("longshots", 1), new("strength", 2),
        new("stamina", 1), new("jumping", 1), new("curve", 1), new("height", 1), new("weight", 1),
    ];
    private static readonly CoreStat[] WingCore =
    [
        new("sprintspeed", 3), new("acceleration", 3), new("balance", 3), new("agility", 2), new("crossing", 2), new("curve", 2),
        new("dribbling", 2), new("ballcontrol", 2), new("shortpassing", 2), new("vision", 1), new("reactions", 1), new("finishing", 1),
    ];
    private static readonly CoreStat[] CamCore =
    [
        new("sprintspeed", 3), new("acceleration", 3), new("balance", 3), new("agility", 2), new("shortpassing", 3), new("vision", 3),
        new("dribbling", 2), new("ballcontrol", 2), new("finishing", 2), new("reactions", 2), new("composure", 2), new("volleys", 1),
        new("longshots", 1), new("shotpower", 1),
    ];
    private static readonly CoreStat[] MidCore =
    [
        new("longshots", 3), new("shotpower", 3), new("shortpassing", 2), new("longpassing", 2), new("marking", 3), new("aggression", 2),
        new("standingtackle", 2), new("finishing", 1), new("curve", 1), new("balance", 1), new("stamina", 1),
    ];
    private static readonly CoreStat[] CentreBackCore =
    [
        new("sprintspeed", 3), new("acceleration", 3), new("marking", 3), new("standingtackle", 3), new("interceptions", 2), new("aggression", 2),
        new("balance", 3), new("agility", 2), new("jumping", 2), new("strength", 2), new("reactions", 2), new("height", 2),
    ];
    private static readonly CoreStat[] FullBackCore =
    [
        new("height", 3), new("sprintspeed", 3), new("acceleration", 3), new("crossing", 2), new("curve", 2), new("stamina", 2),
        new("balance", 2), new("agility", 2), new("marking", 2), new("standingtackle", 2), new("interceptions", 2), new("shortpassing", 1),
    ];

    private static readonly string[][] ForwardStats =
    [
        ["sprintspeed", "acceleration", "strength", "finishing"], ["dribbling", "agility", "shotpower", "composure"],
        ["balance", "reactions", "volleys", "headingaccuracy"], ["longshots", "stamina", "jumping", "curve"], ["height", "weight", "ballcontrol", "positioning"],
    ];

    public static readonly IReadOnlyList<MarketGroup> All =
    [
        new("ST", "스트라이커", ",24,25,26,", ForwardStats, AttackTraits)
            { CoreStats = ForwardCore, KeyTraits = ["라인 브레이커", "트릭스터", "아크로바틱 피니셔"] },
        new("W", "윙어", ",23,27,",
            [ForwardStats[0], ForwardStats[1], ["balance", "crossing", "curve", "shortpassing"], ["ballcontrol", "vision", "reactions", "stamina"]], AttackTraits)
            { CoreStats = WingCore, KeyTraits = ["라인 브레이커", "트릭스터", "아크로바틱 피니셔"] },
        new("CF", "중앙 공격수", ",20,21,22,", ForwardStats, AttackTraits)
            { CoreStats = ForwardCore, KeyTraits = ["라인 브레이커", "트릭스터", "아크로바틱 피니셔"] },
        new("SM", "측면 미드필더", ",12,16,",
            [["sprintspeed", "acceleration", "crossing", "dribbling"], ["stamina", "agility", "shortpassing", "composure"],
             ["balance", "curve", "ballcontrol", "vision"], ["reactions", "finishing", "longshots", "strength"]], AttackTraits)
            { CoreStats = WingCore, KeyTraits = ["라인 브레이커", "트릭스터", "아크로바틱 피니셔"] },
        new("CAM", "공격형 미드필더", ",17,18,19,",
            [["sprintspeed", "acceleration", "dribbling", "shortpassing"], ["agility", "vision", "longshots", "composure"],
             ["balance", "ballcontrol", "finishing", "shotpower"], ["reactions", "volleys", "curve", "strength"]], AttackTraits)
            { CoreStats = CamCore, KeyTraits = ["라인 브레이커", "트릭스터", "아크로바틱 피니셔"] },
        new("CM", "중앙 미드필더", ",13,14,15,",
            [["shortpassing", "longpassing", "vision", "stamina"], ["strength", "interceptions", "composure", "sprintspeed"],
             ["longshots", "shotpower", "marking", "aggression"], ["standingtackle", "balance", "acceleration", "finishing"]], MidTraits)
            { CoreStats = MidCore, KeyTraits = ["커맨더", "레이저 슈터", "와일드 태클러", "체이서", "파이터"] },
        new("CDM", "수비형 미드필더", ",9,10,11,",
            [["interceptions", "standingtackle", "strength", "shortpassing"], ["stamina", "marking", "sprintspeed", "composure"],
             ["longshots", "shotpower", "longpassing", "aggression"], ["balance", "acceleration", "finishing", "curve"]], MidTraits)
            { CoreStats = MidCore, KeyTraits = ["커맨더", "레이저 슈터", "와일드 태클러", "체이서", "파이터"] },
        new("CB", "센터백", ",1,4,5,6,",
            [["sprintspeed", "strength", "marking", "standingtackle"], ["headingaccuracy", "jumping", "interceptions", "acceleration"],
             ["balance", "agility", "aggression", "reactions"], ["height", "weight", "slidingtackle", "composure"]], DefTraits)
            { CoreStats = CentreBackCore, InflatingStats = ["headingaccuracy", "slidingtackle"], KeyTraits = ["파이터", "체이서", "와일드 태클러"] },
        new("FB", "풀백", ",2,3,7,8,",
            [["sprintspeed", "acceleration", "stamina", "crossing"], ["standingtackle", "marking", "interceptions", "strength"],
             ["balance", "agility", "curve", "height"], ["shortpassing", "aggression", "reactions", "jumping"]], DefTraits)
            { CoreStats = FullBackCore, KeyTraits = ["파이터", "체이서", "와일드 태클러"] },
        new("GK", "골키퍼", ",0,",
            [["gkdiving", "gkhandling", "gkreflexes", "gkpositioning"], ["gkkicking", "reactions", "height", "composure"], ["jumping", "weight", "balance", "strength"]],
            ["GK 데드아이", "GK 빠른 반응", "GK 공중볼 장악"])
            { CoreStats = [new("gkdiving", 2), new("gkreflexes", 2), new("gkhandling", 2), new("gkpositioning", 2), new("jumping", 2), new("height", 3)] },
    ];

    public static MarketGroup Get(string key) => All.First(g => g.Key == key);

    /// <summary>
    /// Changes whenever the stats or tags collected change, so the next refresh collects everything once (a full refresh);
    /// daily price refreshes then carry the extra passes over.
    /// </summary>
    public static string SchemaVersion =>
        string.Join("|", All.Select(g => $"{g.Key}:{string.Join(",", g.AllStats)}:{string.Join(",", g.Traits)}:{BodyGroups.Contains(g.Key)}"));

    /// <summary>
    /// Cards whose price follows the name more than the card (호날두, 호나우두, 굴리트: core stats, body and fame far
    /// above anything else). They are left out when the price model learns what things are worth.
    /// </summary>
    public static readonly string[] PriceOutliers = ["호날두", "호나우두", "굴리트"];

    public static bool IsPriceOutlier(MarketCard c) => PriceOutliers.Any(n => c.Name.Contains(n, StringComparison.Ordinal));

    /// <summary>OVR 105-130 at +1 covers squad-level cards; below that the market is mostly floor prices.</summary>
    public const int OvrMin = 105, OvrMax = 130;

    /// <summary>
    /// Keepers from older seasons (TB, HOT, COC, NHD, TT, OTW, LIVE: OVR 80-97 at +1) are still fielded at high grades
    /// (COC 알리송 +13 trades at 67억), so the GK group is collected from 85; outfield cards below 105 are not played.
    /// </summary>
    public const int GkOvrMin = 85;

    public static int OvrMinOf(string group) => group == "GK" ? GkOvrMin : OvrMin;

    public static readonly (int Min, int Max)[] SalaryBands = [(4, 14), (15, 18), (19, 21), (22, 24), (25, 27), (28, 99)];

    /// <summary>Body-type filters as the site sends them (마름 / 건장); the rest are 보통.</summary>
    public static readonly IReadOnlyDictionary<string, string> Bodies = new Dictionary<string, string> { ["thin"] = ",1,4,7,11,", ["heavy"] = ",3,6,9,13," };
    public static readonly string[] BodyGroups = ["ST", "W", "CF", "CAM", "SM", "CM", "CDM", "CB", "FB"];

    /// <summary>The skill-move filter is an exact star count; only the premium ones are tagged.</summary>
    public static readonly int[] SkillTags = [5, 6];

    public static readonly IReadOnlyDictionary<string, string> StatNames = new Dictionary<string, string>
    {
        ["sprintspeed"] = "속력", ["acceleration"] = "가속력", ["strength"] = "몸싸움", ["finishing"] = "골 결정력", ["dribbling"] = "드리블",
        ["agility"] = "민첩성", ["shotpower"] = "슛 파워", ["composure"] = "침착성", ["crossing"] = "크로스", ["stamina"] = "스태미너",
        ["shortpassing"] = "짧은 패스", ["longpassing"] = "긴 패스", ["vision"] = "시야", ["longshots"] = "중거리 슛",
        ["interceptions"] = "가로채기", ["standingtackle"] = "태클", ["marking"] = "대인 수비", ["headingaccuracy"] = "헤더",
        ["jumping"] = "점프", ["gkdiving"] = "GK 다이빙", ["gkhandling"] = "GK 핸들링", ["gkreflexes"] = "GK 반응속도",
        ["gkpositioning"] = "GK 위치 선정", ["gkkicking"] = "GK 킥", ["reactions"] = "반응 속도", ["height"] = "키",
        ["weight"] = "체중", ["balance"] = "밸런스", ["volleys"] = "발리슛", ["curve"] = "커브", ["ballcontrol"] = "볼 컨트롤",
        ["positioning"] = "위치 선정", ["aggression"] = "적극성", ["slidingtackle"] = "슬라이딩 태클",
    };

    public static string TagLabel(string tag) => tag switch
    {
        "skill:5" => "개인기 5성",
        "skill:6" => "개인기 6성",
        "body:thin" => "마름",
        "body:heavy" => "건장",
        PriceModel.RankerColorTag => "랭커 주요 팀컬러 소속",
        _ => tag.StartsWith("trait:") ? tag[6..] : tag,
    };
}

/// <summary>One card as the data center list shows it. Prices are per enhancement grade (1-13), in BP.</summary>
public sealed record MarketCard
{
    public required string Group { get; init; }
    public required long SpId { get; init; }
    public required string Name { get; init; }
    public required string Season { get; init; }
    public int Pay { get; init; }
    /// <summary>Best position OVR at +1.</summary>
    public int Ovr1 { get; init; }
    public int WeakFoot { get; init; }
    public double? Rating { get; init; }
    public int RatingCount { get; init; }
    public IReadOnlyDictionary<int, long> Prices { get; init; } = new Dictionary<int, long>();
    public IReadOnlyDictionary<string, int> Stats { get; init; } = new Dictionary<string, int>();
    public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>();
    /// <summary>OVR at +1 for each position the card lists; the card can be fielded only there.</summary>
    public IReadOnlyDictionary<string, int> Positions { get; init; } = new Dictionary<string, int>();

    /// <summary>The card's own position: the data center lists it first (케인 ST, 올리세 RW, 키미히 CDM, 데이비스 LB).</summary>
    public string MainPosition => Positions.Count > 0 ? Positions.Keys.First() : "";

    /// <summary>Whether a slot is the card's own role (LB for a LB card also covers RB; a RW card is not a CAM).</summary>
    public bool PlaysAsMain(string position) => MainPosition.Length > 0 && RankerAllocation.RoleOf(MainPosition) == RankerAllocation.RoleOf(position);

    public int SeasonId => (int)(SpId / 1_000_000);
    /// <summary>The same footballer across seasons: a squad cannot field two of them.</summary>
    public int PlayerId => (int)(SpId % 1_000_000);
    public int OvrAt(int grade) => Ovr1 - Grades.Bonus[1] + Grades.Bonus[grade];

    /// <returns>OVR at a rated position and grade, or null when the card does not list that position.</returns>
    public int? OvrAt(string position, int grade) =>
        Positions.TryGetValue(position, out var ovr) ? ovr - Grades.Bonus[1] + Grades.Bonus[grade] : null;
    public long PriceAt(int grade) => Prices.GetValueOrDefault(grade);
    /// <summary>Cards with one price at every grade are not really traded (e.g. bound cards).</summary>
    public bool IsTraded => Prices.Values.Distinct().Count() > 1;
}

public static class Grades
{
    /// <summary>OVR added by each enhancement grade, from the data center's grade selector.</summary>
    public static readonly IReadOnlyDictionary<int, int> Bonus = new Dictionary<int, int>
    {
        [1] = 3, [2] = 4, [3] = 5, [4] = 7, [5] = 9, [6] = 11, [7] = 14, [8] = 18, [9] = 20, [10] = 22, [11] = 24, [12] = 27, [13] = 30,
    };

    /// <summary>At or below this a price is the market floor, not a valuation.</summary>
    public const long FloorPrice = 1200;

    /// <summary>
    /// The highest grade one can buy: +12 and +13 come from no pack, only from enhancing, and their owners do not sell
    /// them on the market (listed prices there are not real offers). They count only for cards the user owns.
    /// </summary>
    public const int MaxTradable = 11;

    /// <summary>Grades offered for buying: 1 to +11.</summary>
    public static IEnumerable<int> Tradable => Enumerable.Range(1, MaxTradable);
}

/// <summary>
/// BP amounts in 억, the one unit the app shows and takes: a plain number means 억 ("10" = 10억, "0.5" = 0.5억), and
/// the game's own spellings still work ("1억 5,000만", "3000만", "1.5조", "12345BP" for raw BP).
/// </summary>
public static partial class Bp
{
    public const long Eok = 100_000_000;
    private static readonly (string Unit, long Size)[] Units = [("조", 1_000_000_000_000), ("억", Eok), ("만", 10_000)];

    /// <summary>What the price boxes' tooltips say.</summary>
    public const string InputHint = "억 단위로 숫자만 입력: 10 = 10억, 0.5 = 0.5억(5,000만). 10억·5000만처럼 단위를 붙여도 됩니다.";

    public static bool TryParse(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var raw = text.Contains("BP", StringComparison.OrdinalIgnoreCase);
        var s = text.Replace(",", "").Replace(" ", "").Replace("BP", "", StringComparison.OrdinalIgnoreCase);
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain))
        {
            value = (long)Math.Round(raw ? plain : plain * Eok);
            return plain >= 0;
        }
        double total = 0;
        var rest = s;
        foreach (Match m in UnitRegex().Matches(s))
        {
            total += double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * Units.First(u => u.Unit == m.Groups[2].Value).Size;
            rest = rest.Replace(m.Value, "");
        }
        if (rest.Length > 0 || total <= 0) return false;
        value = (long)total;
        return true;
    }

    /// <summary>Always in 억: "1,234억", "12.3억", "0.42억"; under 100만 "0.01억 미만" (market floor listings).</summary>
    public static string Format(long bp)
    {
        var eok = bp / (double)Eok;
        if (bp < 0) return "-" + Format(-bp);
        if (bp == 0) return "0억";
        if (eok >= 100) return eok.ToString("#,0", CultureInfo.InvariantCulture) + "억";
        if (eok >= 1) return eok.ToString("#,0.#", CultureInfo.InvariantCulture) + "억";
        if (eok >= 0.01) return eok.ToString("0.0#", CultureInfo.InvariantCulture) + "억";
        return "0.01억 미만";
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)(조|억|만)")]
    private static partial Regex UnitRegex();
}
