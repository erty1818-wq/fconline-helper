namespace FcHelper.Market;

/// <summary>A formation as the positions its eleven slots need, by the position names the data center shows OVR for.</summary>
public sealed record Formation(string Name, string[] Slots);

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
}
