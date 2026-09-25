using System.Globalization;
using System.Text.RegularExpressions;

namespace FcHelper.Market;

/// <summary>
/// A position group of the data center's player list: which positions it covers (the API's spposition codes, as the
/// site's own filter sends them), the eight stats collected for it (two passes of four) and the traits tagged.
/// </summary>
public sealed record MarketGroup(string Key, string Name, string Positions, string[][] Stats, string[] Traits)
{
    public IEnumerable<string> AllStats => Stats.SelectMany(s => s).Distinct();
}

public static class MarketGroups
{
    private static readonly string[] AttackTraits =
        ["라인 브레이커", "트릭스터", "스피드스터", "타이탄", "프레데터", "레이저 슈터", "아크로바틱 피니셔", "크로스 포쳐", "파이터", "2개의 심장"];
    private static readonly string[] MidTraits = ["2개의 심장", "파이터", "체이서", "블로커", "커맨더", "타이탄", "레이저 슈터", "스피드스터", "트릭스터"];
    private static readonly string[] DefTraits = ["블로커", "와일드 태클러", "커맨더", "체이서", "타이탄", "파이터", "스피드스터"];
    private static readonly string[][] ForwardStats =
        [["sprintspeed", "acceleration", "strength", "finishing"], ["dribbling", "agility", "shotpower", "composure"]];

    public static readonly IReadOnlyList<MarketGroup> All =
    [
        new("ST", "스트라이커", ",24,25,26,", ForwardStats, AttackTraits),
        new("W", "윙어", ",23,27,", ForwardStats, AttackTraits),
        new("CF", "중앙 공격수", ",20,21,22,", ForwardStats, AttackTraits),
        new("SM", "측면 미드필더", ",12,16,", [["sprintspeed", "acceleration", "crossing", "dribbling"], ["stamina", "agility", "shortpassing", "composure"]], AttackTraits),
        new("CAM", "공격형 미드필더", ",17,18,19,", [["sprintspeed", "acceleration", "dribbling", "shortpassing"], ["agility", "vision", "longshots", "composure"]], AttackTraits),
        new("CM", "중앙 미드필더", ",13,14,15,", [["shortpassing", "longpassing", "vision", "stamina"], ["strength", "interceptions", "composure", "sprintspeed"]], MidTraits),
        new("CDM", "수비형 미드필더", ",9,10,11,", [["interceptions", "standingtackle", "strength", "shortpassing"], ["stamina", "marking", "sprintspeed", "composure"]], MidTraits),
        new("CB", "센터백", ",1,4,5,6,", [["sprintspeed", "strength", "marking", "standingtackle"], ["headingaccuracy", "jumping", "interceptions", "acceleration"]], DefTraits),
        new("FB", "풀백", ",2,3,7,8,", [["sprintspeed", "acceleration", "stamina", "crossing"], ["standingtackle", "marking", "interceptions", "strength"]], DefTraits),
        new("GK", "골키퍼", ",0,", [["gkdiving", "gkhandling", "gkreflexes", "gkpositioning"], ["gkkicking", "reactions", "height", "composure"]], ["GK 데드아이", "GK 빠른 반응", "GK 공중볼 장악"]),
    ];

    public static MarketGroup Get(string key) => All.First(g => g.Key == key);

    /// <summary>OVR 105-130 at +1 covers squad-level cards; below that the market is mostly floor prices.</summary>
    public const int OvrMin = 105, OvrMax = 130;

    public static readonly (int Min, int Max)[] SalaryBands = [(4, 14), (15, 18), (19, 21), (22, 24), (25, 27), (28, 99)];

    /// <summary>Body-type filters as the site sends them (마름 / 건장); the rest are 보통. Tagged for ST and W.</summary>
    public static readonly IReadOnlyDictionary<string, string> Bodies = new Dictionary<string, string> { ["thin"] = ",1,4,7,11,", ["heavy"] = ",3,6,9,13," };
    public static readonly string[] BodyGroups = ["ST", "W"];

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
    };

    public static string TagLabel(string tag) => tag switch
    {
        "skill:5" => "개인기 5성",
        "skill:6" => "개인기 6성",
        "body:thin" => "마름",
        "body:heavy" => "건장",
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

    public int SeasonId => (int)(SpId / 1_000_000);
    public int OvrAt(int grade) => Ovr1 - Grades.Bonus[1] + Grades.Bonus[grade];
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
}

/// <summary>BP amounts as the game writes them: "1억 5,000만", "3,000만", "1.5조".</summary>
public static partial class Bp
{
    private static readonly (string Unit, long Size)[] Units = [("조", 1_000_000_000_000), ("억", 100_000_000), ("만", 10_000)];

    public static bool TryParse(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Replace(",", "").Replace(" ", "").Replace("BP", "", StringComparison.OrdinalIgnoreCase);
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain))
        {
            value = (long)plain;
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

    public static string Format(long bp)
    {
        foreach (var (unit, size) in Units[..2])
        {
            if (bp >= size) return (bp / (double)size).ToString(bp >= size * 100 ? "#,0" : "#,0.#", CultureInfo.InvariantCulture) + unit;
        }
        return bp >= 10_000 ? $"{bp / 10_000:#,0}만" : bp.ToString("#,0", CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)(조|억|만)")]
    private static partial Regex UnitRegex();
}
