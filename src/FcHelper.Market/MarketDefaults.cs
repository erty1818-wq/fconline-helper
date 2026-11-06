namespace FcHelper.Market;

/// <summary>Playable market defaults (MV-10). Keep the same +1 OVR population when changing enhancement grade.</summary>
public static class MarketDefaults
{
    public const int Grade = 11;
    /// <summary>+11 OVR 140 = +1 OVR 119; +10 OVR 138 covers the same cards.</summary>
    public const int MinOvr1 = 119;
    public static int MinOvr(int grade = Grade) => MinOvr1 - Grades.Bonus[1] + Grades.Bonus[grade];

    /// <summary>
    /// How long a newly listed card is treated as new (<see cref="MarketCard.IsNew"/>): kept out of every fit and average,
    /// shown with its expected price only. Four weeks covers the launch packs and events [추정, 사용자 규칙 2026-11-06].
    /// </summary>
    public const int NewCardDays = 28;
}
