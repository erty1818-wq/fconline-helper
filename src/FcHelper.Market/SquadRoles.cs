namespace FcHelper.Market;

/// <summary>Playing roles of a formation, distinct from the position whose OVR is shown on the pitch.</summary>
public static class SquadRoles
{
    public static bool WideAm(Formation formation, int slot) => Formations.Normalize(formation.Slots[slot]) == "CAM"
        && formation.Slots.Count(p => Formations.Normalize(p) == "CAM") >= 2
        && formation.Slots.Count(p => Formations.Normalize(p) == "ST") >= 2;

    public static bool SupportingAm(Formation formation, int slot) => Formations.Normalize(formation.Slots[slot]) == "CF"
        && formation.Slots.Any(p => Formations.Normalize(p) == "ST")
        && formation.Slots.Count(p => Formations.Normalize(p) is "LM" or "RM") >= 2;

    public static IReadOnlyList<string> PreferredRoles(Formation formation, int slot) => WideAm(formation, slot) ? ["W", "SM"]
        : SupportingAm(formation, slot) ? ["CAM", "CF"] : [RankerAllocation.RoleOf(formation.Slots[slot])];

    public static bool Preferred(MarketCard card, Formation formation, int slot) =>
        PreferredRoles(formation, slot).Contains(RankerAllocation.RoleOf(card.MainPosition));

    /// <summary>Keep the target position's actual rating; calculated unlisted ratings are limited to these attacking roles.</summary>
    public static int? Ovr(MarketCard card, Formation formation, int slot, int grade)
    {
        var pos = Formations.Normalize(formation.Slots[slot]);
        if ((pos == "GK") != (card.MainPosition == "GK" || card.Group == "GK")) return null;
        return card.OvrAt(pos, grade) ?? ((WideAm(formation, slot) || SupportingAm(formation, slot)) && Preferred(card, formation, slot)
            ? PositionRatings.At(card, pos, grade) : null);
    }
}

/// <summary>Listed ratings first; otherwise calculate the target role only when every weighted stat is available.</summary>
public static class PositionRatings
{
    public static IReadOnlyDictionary<string, int> Stats(MarketCard card) => card.Stats
        .Where(kv => MarketGroups.StatNames.ContainsKey(kv.Key)).ToDictionary(kv => MarketGroups.StatNames[kv.Key], kv => kv.Value);

    public static bool Complete(string position, IReadOnlyDictionary<string, int> stats) =>
        OvrFormula.Of(position) is { Count: > 0 } weights && weights.Keys.All(stats.ContainsKey);

    public static int? At(MarketCard card, string position, int grade)
    {
        var pos = Formations.Normalize(position);
        if (card.OvrAt(pos, grade) is { } listed) return listed;
        var stats = Stats(card);
        return Complete(pos, stats) ? OvrFormula.Ovr(pos, stats) + Grades.Bonus[grade] - Grades.Bonus[1] : null;
    }
}
