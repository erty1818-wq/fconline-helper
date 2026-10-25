namespace FcHelper.Market;

/// <summary>Playable market defaults (MV-10). Keep the same +1 OVR population when changing enhancement grade.</summary>
public static class MarketDefaults
{
    public const int Grade = 11;
    /// <summary>+11 OVR 140 = +1 OVR 119; +10 OVR 138 covers the same cards.</summary>
    public const int MinOvr1 = 119;
    public static int MinOvr(int grade = Grade) => MinOvr1 - Grades.Bonus[1] + Grades.Bonus[grade];
}
