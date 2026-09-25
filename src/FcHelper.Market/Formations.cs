namespace FcHelper.Market;

/// <summary>A formation as the positions its eleven slots need, by the position names the data center shows OVR for.</summary>
public sealed record Formation(string Name, string[] Slots);

/// <summary>A position on the 7-row by 5-column tactical pitch grid.</summary>
public sealed record Spot(int Row, int Col);

public sealed record MoveResult(bool Success, IReadOnlyList<Spot> Spots, string? Reason = null);

/// <summary>
/// The formations top rankers use (daily chart, 2026-09), with slots as rated positions: LAM/RAM share the CAM rating,
/// LCB/RCB the CB rating and so on, which is how the data center lists a card's OVR.
/// </summary>
public static class Formations
{
    public static readonly IReadOnlyList<Formation> All =
    [
        new("4-2-2-2", ["GK", "LB", "CB", "CB", "RB", "CDM", "CDM", "CAM", "CAM", "ST", "ST"]),
        new("4-2-2-1-1", ["GK", "LB", "CB", "CB", "RB", "CDM", "CDM", "LM", "RM", "CF", "ST"]),
        new("4-2-3-1", ["GK", "LB", "CB", "CB", "RB", "CDM", "CDM", "LM", "CAM", "RM", "ST"]),
        new("4-1-2-3", ["GK", "LB", "CB", "CB", "RB", "CDM", "CM", "CM", "LW", "RW", "ST"]),
        new("4-2-4", ["GK", "LB", "CB", "CB", "RB", "CM", "CM", "LW", "RW", "ST", "ST"]),
        new("4-1-4-1", ["GK", "LB", "CB", "CB", "RB", "CDM", "LM", "CM", "CM", "RM", "ST"]),
        new("4-4-2", ["GK", "LB", "CB", "CB", "RB", "LM", "CM", "CM", "RM", "ST", "ST"]),
        new("4-2-1-3", ["GK", "LB", "CB", "CB", "RB", "CDM", "CDM", "CAM", "LW", "RW", "ST"]),
        new("4-1-2-1-2", ["GK", "LB", "CB", "CB", "RB", "CDM", "LM", "RM", "CAM", "ST", "ST"]),
        new("4-1-3-2", ["GK", "LB", "CB", "CB", "RB", "CDM", "LM", "CM", "RM", "ST", "ST"]),
        new("4-3-3", ["GK", "LB", "CB", "CB", "RB", "CM", "CDM", "CM", "LW", "RW", "ST"]),
        new("3-4-3", ["GK", "CB", "CB", "CB", "LM", "CM", "CM", "RM", "LW", "RW", "ST"]),
        new("5-2-1-2", ["GK", "LWB", "CB", "CB", "CB", "RWB", "CM", "CM", "CAM", "ST", "ST"]),
    ];

    public static Formation? Find(string name) => All.FirstOrDefault(f => f.Name == name);

    /// <summary>A custom formation from eleven position names, e.g. "GK,LB,CB,CB,RB,CDM,CDM,CAM,CAM,ST,ST".</summary>
    public static Formation Custom(string name, IEnumerable<string> slots)
    {
        var s = slots.Select(Normalize).ToArray();
        if (s.Length != 11) throw new ArgumentException("A formation has eleven slots.");
        if (s.Count(p => p == "GK") != 1) throw new ArgumentException("A formation has exactly one GK.");
        return new Formation(name, s);
    }

    /// <summary>Side and centre variants of a position share one rating (LCB → CB, LAM → CAM, RS → ST).</summary>
    public static string Normalize(string position) => position.Trim().ToUpperInvariant() switch
    {
        "LCB" or "RCB" or "SW" => "CB",
        "LDM" or "RDM" => "CDM",
        "LCM" or "RCM" => "CM",
        "LAM" or "RAM" => "CAM",
        "LS" or "RS" => "ST",
        "LF" or "RF" => "CF",
        var p => p,
    };

    /// <summary>Which market group a slot's candidates come from, plus groups whose cards often list that position.</summary>
    public static string GroupOf(string slot) => slot switch
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

    public static double ColX(int col) => col switch
    {
        0 => 0.1,
        1 => 0.3,
        2 => 0.5,
        3 => 0.7,
        4 => 0.9,
        _ => 0.5,
    };

    public static double RowY(int row) => row switch
    {
        0 => 0.08,
        1 => 0.215,
        2 => 0.345,
        3 => 0.475,
        4 => 0.605,
        5 => 0.76,
        6 => 0.92,
        _ => 0.5,
    };

    /// <summary>Returns the detailed position name at a grid spot (Row 0..6, Col 0..4).</summary>
    public static string PositionAt(Spot spot) => (spot.Row, spot.Col) switch
    {
        (0, 0) => "LW",
        (0, 1) => "LS",
        (0, 2) => "ST",
        (0, 3) => "RS",
        (0, 4) => "RW",

        (1, 0) => "LW",
        (1, 1) => "LF",
        (1, 2) => "CF",
        (1, 3) => "RF",
        (1, 4) => "RW",

        (2, 0) => "LM",
        (2, 1) => "LAM",
        (2, 2) => "CAM",
        (2, 3) => "RAM",
        (2, 4) => "RM",

        (3, 0) => "LM",
        (3, 1) => "LCM",
        (3, 2) => "CM",
        (3, 3) => "RCM",
        (3, 4) => "RM",

        (4, 0) => "LWB",
        (4, 1) => "LDM",
        (4, 2) => "CDM",
        (4, 3) => "RDM",
        (4, 4) => "RWB",

        (5, 0) => "LB",
        (5, 1) => "LCB",
        (5, 2) => "CB",
        (5, 3) => "RCB",
        (5, 4) => "RB",

        (6, 2) => "GK",
        _ => "",
    };

    /// <summary>11 spots for each of the 13 preset formations.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<Spot>> PresetSpots = new Dictionary<string, IReadOnlyList<Spot>>
    {
        ["4-2-2-2"] = [new(6,2), new(5,0), new(5,1), new(5,3), new(5,4), new(4,1), new(4,3), new(2,1), new(2,3), new(0,1), new(0,3)],
        ["4-2-2-1-1"] = [new(6,2), new(5,0), new(5,1), new(5,3), new(5,4), new(4,1), new(4,3), new(2,0), new(2,4), new(1,2), new(0,2)],
        ["4-2-3-1"] = [new(6,2), new(5,0), new(5,1), new(5,3), new(5,4), new(4,1), new(4,3), new(2,0), new(2,2), new(2,4), new(0,2)],
        ["4-1-2-3"] = [new(6,2), new(5,0), new(5,1), new(5,3), new(5,4), new(4,2), new(3,1), new(3,3), new(0,0), new(0,4), new(0,2)],
        ["4-2-4"] = [new(6,2), new(5,0), new(5,1), new(5,3), new(5,4), new(3,1), new(3,3), new(0,0), new(0,4), new(0,1), new(0,3)],
        ["4-1-4-1"] = [new(6,2), new(5,0), new(5,1), new(5,3), new(5,4), new(4,2), new(3,0), new(3,1), new(3,3), new(3,4), new(0,2)],
        ["4-4-2"] = [new(6,2), new(5,0), new(5,1), new(5,3), new(5,4), new(3,0), new(3,1), new(3,3), new(3,4), new(0,1), new(0,3)],
        ["4-2-1-3"] = [new(6,2), new(5,0), new(5,1), new(5,3), new(5,4), new(4,1), new(4,3), new(2,2), new(0,0), new(0,4), new(0,2)],
        ["4-1-2-1-2"] = [new(6,2), new(5,0), new(5,1), new(5,3), new(5,4), new(4,2), new(3,0), new(3,4), new(2,2), new(0,1), new(0,3)],
        ["4-1-3-2"] = [new(6,2), new(5,0), new(5,1), new(5,3), new(5,4), new(4,2), new(3,0), new(3,2), new(3,4), new(0,1), new(0,3)],
        ["4-3-3"] = [new(6,2), new(5,0), new(5,1), new(5,3), new(5,4), new(3,1), new(4,2), new(3,3), new(0,0), new(0,4), new(0,2)],
        ["3-4-3"] = [new(6,2), new(5,1), new(5,2), new(5,3), new(3,0), new(3,1), new(3,3), new(3,4), new(0,0), new(0,4), new(0,2)],
        ["5-2-1-2"] = [new(6,2), new(4,0), new(5,1), new(5,2), new(5,3), new(4,4), new(3,1), new(3,3), new(2,2), new(0,1), new(0,3)],
    };

    public static IReadOnlyList<Spot> LayoutOf(string name) =>
        PresetSpots.TryGetValue(name, out var spots) ? spots : PresetSpots["4-2-2-2"];

    /// <summary>
    /// Detects formation name from 11 spots. Checks exact preset layout match first; otherwise groups non-GK
    /// spots by line (Defense, DM center, CM, AM, CF, ST) from back to front and joins non-zero counts with '-'.
    /// </summary>
    public static string Detect(IReadOnlyList<Spot> spots)
    {
        if (spots.Count != 11) return "알 수 없음";

        // 1. Exact preset spot sequence check
        foreach (var (presetName, presetList) in PresetSpots)
        {
            if (spots.SequenceEqual(presetList)) return presetName;
        }

        // 2. Zone grouping:
        // Defence: Row 5 + LWB/RWB (Row 4, Col 0 and 4)
        var def = spots.Count(s => s.Row == 5 || (s.Row == 4 && (s.Col == 0 || s.Col == 4)));
        // DM: Row 4 center 3 (Col 1, 2, 3)
        var dm = spots.Count(s => s.Row == 4 && s.Col >= 1 && s.Col <= 3);
        // CM: Row 3
        var cm = spots.Count(s => s.Row == 3);
        // AM: Row 2
        var am = spots.Count(s => s.Row == 2);
        // CF: Row 1
        var cf = spots.Count(s => s.Row == 1);
        // ST: Row 0
        var st = spots.Count(s => s.Row == 0);

        var lines = new List<int>();
        if (def > 0) lines.Add(def);
        if (dm > 0) lines.Add(dm);
        if (cm > 0) lines.Add(cm);
        if (am > 0) lines.Add(am);
        if (cf > 0) lines.Add(cf);
        if (st > 0) lines.Add(st);

        var joined = string.Join("-", lines);
        return joined;
    }

    /// <summary>Pure function to move a slot to another spot or swap with the player already there.</summary>
    public static MoveResult Move(IReadOnlyList<Spot> spots, int slot, Spot to)
    {
        if (slot < 0 || slot >= spots.Count) return new(false, spots, "유효하지 않은 슬롯입니다.");
        if (to.Row < 0 || to.Row > 6 || to.Col < 0 || to.Col > 4) return new(false, spots, "피치 밖으로 놓을 수 없습니다.");
        if (string.IsNullOrEmpty(PositionAt(to))) return new(false, spots, "놓을 수 없는 자리입니다.");

        var current = spots[slot];
        if (current == to) return new(true, spots);

        // GK constraints
        var isGk = current.Row == 6;
        if (isGk && to.Row != 6) return new(false, spots, "골키퍼는 골문 앞(GK 줄)을 벗어날 수 없습니다.");
        if (!isGk && to.Row == 6) return new(false, spots, "필드 플레이어는 골문 자리로 이동할 수 없습니다.");
        var otherIndex = -1;
        for (var i = 0; i < spots.Count; i++)
        {
            if (i != slot && spots[i] == to)
            {
                otherIndex = i;
                break;
            }
        }
        var next = spots.ToArray();
        if (otherIndex >= 0)
        {
            var otherIsGk = spots[otherIndex].Row == 6;
            if (isGk != otherIsGk) return new(false, spots, "골키퍼는 필드 플레이어와 교체할 수 없습니다.");
            next[slot] = to;
            next[otherIndex] = current;
        }
        else
        {
            next[slot] = to;
        }

        return new(true, next);
    }
}

