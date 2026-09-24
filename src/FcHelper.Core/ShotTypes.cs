namespace FcHelper.Core;

/// <summary>
/// shootDetail.type codes 1-12 as listed in the official match-detail schema; real samples agree where they can be
/// checked (3 = header, 8 = free kick, 9 = penalty match the shoot summary counts). Real data also contains 13 and
/// 14, which the schema does not list, so they fall through to "기타".
/// </summary>
public static class ShotTypes
{
    public const int Normal = 1;
    public const int Finesse = 2;
    public const int Header = 3;
    public const int Lob = 4;
    public const int Flare = 5;
    public const int Low = 6;
    public const int Volley = 7;
    public const int FreeKick = 8;
    public const int Penalty = 9;
    public const int Knuckle = 10;
    public const int Bicycle = 11;
    public const int Power = 12;

    public static string Label(int type) => type switch
    {
        Normal => "일반슛",
        Finesse => "감아차기",
        Header => "헤더",
        Lob => "로빙슛",
        Flare => "플레어",
        Low => "낮은슛",
        Volley => "발리",
        FreeKick => "프리킥",
        Penalty => "PK",
        Knuckle => "무회전",
        Bicycle => "바이시클",
        Power => "파워샷",
        _ => $"기타({type})",
    };
}

public static class Positions
{
    public const int Substitute = 28;

    public static string Label(int spPosition) => spPosition switch
    {
        0 => "GK", 1 => "SW", 2 => "RWB", 3 => "RB", 4 => "RCB", 5 => "CB", 6 => "LCB", 7 => "LB", 8 => "LWB",
        9 => "RDM", 10 => "CDM", 11 => "LDM", 12 => "RM", 13 => "RCM", 14 => "CM", 15 => "LCM", 16 => "LM",
        17 => "RAM", 18 => "CAM", 19 => "LAM", 20 => "RF", 21 => "CF", 22 => "LF", 23 => "RW", 24 => "RS",
        25 => "ST", 26 => "LS", 27 => "LW", 28 => "SUB",
        _ => $"P{spPosition}",
    };
}

/// <summary>spId = seasonId (3 digits) + pid (6 digits).</summary>
public static class SpId
{
    public static int SeasonOf(int spId) => spId / 1_000_000;
    public static int PlayerOf(int spId) => spId % 1_000_000;
}

/// <summary>How much of an analysis result is taken straight from the API versus interpreted.</summary>
public enum Evidence
{
    /// <summary>An API field as-is.</summary>
    Direct,
    /// <summary>Aggregated from API fields.</summary>
    Computed,
    /// <summary>An interpretation of a pattern; the API has no such field.</summary>
    Inferred,
}

public static class EvidenceLabels
{
    public static string Label(this Evidence e) => e switch
    {
        Evidence.Direct => "직접",
        Evidence.Computed => "계산",
        _ => "추정",
    };
}
