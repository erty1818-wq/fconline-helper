namespace FcHelper.Market;

public sealed record PriceEffect(string Name, double Percent, double Low, double High, double T, int? Cards);

public enum FactorKind { Stat, Height, Weight, Trait, Skill, Body, Foot, Salary, TeamColor, FeatureTeamColor }

/// <summary>A separately priced team-colour membership. Affiliation and feature colours must not be merged.</summary>
public sealed record MarketMembership(string Key, string Name, FactorKind Kind, IReadOnlySet<long> Members);

/// <summary>
/// What one thing is worth on the market at a position and grade, everything else equal: in % of the price, in OVR
/// points (the price of that many extra OVR) and in BP on a typical card of the group. [추정: 시장 회귀]
/// </summary>
public sealed record PriceFactor(string Key, string Name, FactorKind Kind, double Percent, double Low, double High, double OvrEquivalent,
    long BpAtMedian, int? Cards, bool IsCore, bool IsInflating)
{
    public int? Players { get; init; }
    /// <summary>Why the factor is left out of the price model (MV-07: not a consistent price factor); null when priced.</summary>
    public string? Excluded { get; init; }
    public bool Estimated => double.IsFinite(Percent);
    /// <summary>At least ten footballers and a player-clustered interval excluding zero.</summary>
    public bool Clear => Estimated && (Players is null || Players >= 10) && (Low > 0 || High < 0);
}

/// <summary>
/// Hedonic price model for one position group at one enhancement grade:
///   ln(price) ~ OVR + OVR² + salary + season (not shrunk) + weak foot + (stat − OVR) per stat + the consistent tags (ridge)
/// A coefficient reads as "everything else equal, this changes the price by X%" in today's market. It is an
/// association: a tag can also stand in for things the model does not see (feel, team colour, uncollected stats).
/// MV-08 (docs/market-value/DETERMINANTS.md): only factors MV-07 found consistent are priced; prices sitting on their
/// enhancement floor (<see cref="Floors"/>) are fitted as "worth at most this" (Tobit EM), not as values.
/// </summary>
public sealed class PriceModel
{
    private const int MinPlayers = 8, MinSeasonCards = 5;
    private sealed record UnpricedFactor(string Key, string Name, FactorKind Kind, int Cards, int Players, bool IsCore, string? Excluded = null);

    /// <summary>
    /// Enhancement price floors found in MV-07: a +k price never goes below a fixed multiple of the grade under it
    /// (+5 ≥ 1.5×+3, +8 ≥ 9.5×+5, +10 ≥ 2.1×+8, +11 ≥ 1.55×+10). About 28% of +8 prices sit exactly there.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, (int Lower, double Ratio)> Floors = new Dictionary<int, (int, double)>
    {
        [5] = (3, 1.5), [8] = (5, 9.5), [10] = (8, 2.1), [11] = (10, 1.55),
    };
    /// <summary>Prices are rounded to three significant digits, so a floor price can read up to 0.7% above the multiple.</summary>
    public const double FloorTolerance = 1.007;

    /// <summary>The lowest price the market allows at this grade, from the card's price one floor step down; null when there is none.</summary>
    public static long? FloorAt(MarketCard c, int grade) =>
        Floors.TryGetValue(grade, out var f) && c.PriceAt(f.Lower) is var lower and > 0 ? (long)Math.Round(f.Ratio * lower) : null;

    /// <summary>True when the price at this grade is its floor: the rule set it, not demand, so it says "worth at most this".</summary>
    public static bool AtFloor(MarketCard c, int grade) =>
        FloorAt(c, grade) is { } floor && c.PriceAt(grade) is var price and > 0 && price <= floor * FloorTolerance;

    /// <summary>Ridge strength on standardised columns for the factors other than OVR, OVR², salary and season (as in MV-07).</summary>
    public const double Ridge = 300;

    /// <summary>The traits MV-07 found consistent (트릭스터 확실, 라인 브레이커 대체로, 체이서 유력); the others are left out.</summary>
    public static readonly IReadOnlySet<string> PricedTraits = new HashSet<string> { "트릭스터", "라인 브레이커", "체이서" };

    /// <summary>
    /// Height and weight buckets are priced only where they matter: 스트라이커 and 골키퍼 (MV-07), 수비형 미드 height, and
    /// 센터백 (the user's call, 2026-10-25; MV-07 found no consistent price effect there, so the ridge keeps it small).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> PhysicalByGroup = new Dictionary<string, string[]>
    {
        ["ST"] = ["height", "weight"], ["CB"] = ["height", "weight"], ["CDM"] = ["height"], ["GK"] = ["weight"],
    };

    private const string NotConsistent = "가격 모델에서 뺌 (MV-07: 같은 시즌·OVR 안에서 일관된 가격 차이 없음)";
    private sealed record PhysicalFeature(string Key, string Name, string Stat, FactorKind Kind, int Min, int Max, int Cards, bool IsCore)
    {
        public bool Has(MarketCard c) => c.Stats.TryGetValue(Stat, out var value) && value >= Min && value <= Max;
    }

    private static readonly (string Key, string Label, int Min, int Max)[] HeightBuckets =
    [
        ("169-", "169cm 이하", int.MinValue, 169), ("170-174", "170~174cm", 170, 174),
        ("175-179", "175~179cm", 175, 179), ("180-184", "180~184cm", 180, 184),
        ("185-189", "185~189cm", 185, 189), ("190+", "190cm 이상", 190, int.MaxValue),
    ];
    private static readonly (string Key, string Label, int Min, int Max)[] WeightBuckets =
    [
        ("64-", "64kg 이하", int.MinValue, 64), ("65-69", "65~69kg", 65, 69),
        ("70-74", "70~74kg", 70, 74), ("75-79", "75~79kg", 75, 79),
        ("80-84", "80~84kg", 80, 84), ("85+", "85kg 이상", 85, int.MaxValue),
    ];

    private readonly string[] _stats;
    private readonly PhysicalFeature[] _physical;
    private readonly string[] _tags;
    private readonly long _medianPrice;
    private readonly MarketGroup _group;
    private readonly IReadOnlyDictionary<string, MarketMembership> _memberships;

    /// <summary>Pseudo-tag: the card counts for one of the team colours rankers use most (priced like a tag).</summary>
    public const string RankerColorTag = "tc:ranker";
    private readonly string[] _seasons;
    private readonly string _baseSeason;
    private readonly double _ovrMean;
    private readonly double[] _beta;
    private readonly double[] _se;
    private readonly string[] _names;
    private readonly Dictionary<string, int> _counts;
    private readonly Dictionary<string, int> _playerCounts;
    private readonly List<UnpricedFactor> _unpriced = [];
    private readonly double _critical;
    /// <summary>Spread of log prices around the model (Tobit sigma).</summary>
    public double Sigma { get; }
    /// <summary>Training cards whose price sat on its enhancement floor.</summary>
    public int FloorCards { get; }

    public string Group { get; }
    public int Grade { get; }
    public int Cards { get; }
    public int Players { get; }
    public double R2 { get; }
    /// <summary>Median absolute percentage error on a deterministic 20% holdout.</summary>
    public double ValidationError { get; }
    /// <summary>Robust spread of holdout log-price errors; used to compare price gaps across position models.</summary>
    public double ValidationLogScale { get; }

    private PriceModel(string group, int grade, IReadOnlyList<MarketCard> data, IReadOnlyList<MarketMembership>? memberships, bool validate = true)
    {
        _memberships = (memberships ?? []).ToDictionary(m => m.Key);
        Group = group;
        Grade = grade;
        Cards = data.Count;
        Players = data.Select(c => c.PlayerId).Distinct().Count();
        _critical = Critical95(Players - 1);
        var g = _group = MarketGroups.Get(group);
        _stats = g.AllStats.Where(s => s is not ("height" or "weight") && data.Count(c => c.Stats.ContainsKey(s)) >= 0.9 * data.Count).ToArray();
        _physical = PhysicalFeatures(data, g);
        var prices = data.Select(c => c.PriceAt(grade)).Order().ToList();
        _medianPrice = prices[prices.Count / 2];
        var seasonCounts = data.GroupBy(c => c.Season).ToDictionary(x => x.Key, x => x.Count());
        _baseSeason = seasonCounts.MaxBy(kv => kv.Value).Key;
        _seasons = seasonCounts.Where(kv => kv.Value >= MinSeasonCards && kv.Key != _baseSeason).Select(kv => kv.Key).Order().ToArray();
        var tagCounts = data.SelectMany(c => c.Tags).GroupBy(t => t).ToDictionary(x => x.Key, x => x.Count());
        foreach (var membership in _memberships.Values)
            tagCounts[membership.Key] = data.Count(c => membership.Members.Contains(c.SpId));
        // MV-07: body types, skill moves, feature team colours and most traits are not consistent price factors.
        foreach (var tag in tagCounts.Keys.Where(t => ExclusionOf(t) is not null).ToList())
        {
            _unpriced.Add(new(tag, Label(tag), KindOf(tag), tagCounts[tag], data.Where(c => Has(c, tag)).Select(c => c.PlayerId).Distinct().Count(),
                KindOf(tag) == FactorKind.Trait && g.KeyTraits.Contains(tag[6..]), ExclusionOf(tag)));
            tagCounts.Remove(tag);
        }
        var tagPlayers = tagCounts.Keys.ToDictionary(t => t, t => data.Where(c => Has(c, t)).Select(c => c.PlayerId).Distinct().Count());
        var tagControls = tagCounts.Keys.ToDictionary(t => t, t => data.Where(c => !Has(c, t)).Select(c => c.PlayerId).Distinct().Count());
        bool Supported(string t) => tagPlayers[t] >= MinPlayers && tagControls[t] >= MinPlayers;
        foreach (var tag in tagCounts.Keys.Where(t => !Supported(t)))
        {
            var kind = KindOf(tag);
            _unpriced.Add(new(tag, Label(tag), kind, tagCounts[tag], tagPlayers[tag], kind == FactorKind.Trait && g.KeyTraits.Contains(tag[6..])));
        }
        var ordinaryTags = tagCounts.Where(kv => Supported(kv.Key) && !_memberships.ContainsKey(kv.Key))
            .OrderByDescending(kv => kv.Value).Select(kv => kv.Key);
        var distinctMemberships = new List<string>();
        var seenMembershipColumns = new List<HashSet<long>>();
        foreach (var membership in _memberships.Values.Where(m => tagCounts.ContainsKey(m.Key) && Supported(m.Key))
                     .OrderBy(m => m.Kind == FactorKind.TeamColor ? 0 : 1).ThenByDescending(m => tagCounts[m.Key]))
        {
            var inGroup = data.Where(c => membership.Members.Contains(c.SpId)).Select(c => c.SpId).ToHashSet();
            if (seenMembershipColumns.Any(s => s.SetEquals(inGroup))) continue;
            seenMembershipColumns.Add(inGroup);
            distinctMemberships.Add(membership.Key);
        }
        _tags = [.. ordinaryTags, .. distinctMemberships];
        _ovrMean = data.Average(c => c.Ovr1);

        _names = ["const", "OVR +1", "OVR²", "양발 (약발 5)", "약발 4", "급여 +1",
            .. _stats.Select(s => $"{MarketGroups.StatNames.GetValueOrDefault(s, s)} +1 (같은 OVR)"),
            .. _physical.Select(p => p.Name),
            .. _tags.Select(Label), .. _seasons.Select(s => $"season:{s}"), "season:기타"];
        _counts = tagCounts.ToDictionary(kv => Label(kv.Key), kv => kv.Value);
        _playerCounts = tagPlayers.ToDictionary(kv => Label(kv.Key), kv => kv.Value);
        foreach (var p in _physical) _counts[p.Name] = p.Cards;
        foreach (var p in _physical) _playerCounts[p.Name] = data.Where(p.Has).Select(c => c.PlayerId).Distinct().Count();
        _counts["양발 (약발 5)"] = data.Count(c => c.WeakFoot >= 5);
        _counts["약발 4"] = data.Count(c => c.WeakFoot == 4);
        _playerCounts["양발 (약발 5)"] = data.Where(c => c.WeakFoot >= 5).Select(c => c.PlayerId).Distinct().Count();
        _playerCounts["약발 4"] = data.Where(c => c.WeakFoot == 4).Select(c => c.PlayerId).Distinct().Count();

        var x = data.Select(Features).ToList();
        var y = data.Select(c => Math.Log(c.PriceAt(grade))).ToList();
        var censored = data.Select(c => AtFloor(c, grade)).ToList();
        FloorCards = censored.Count(b => b);
        // Not shrunk: OVR, OVR², salary and the seasons (shrinking the seasons lets other factors take their premium).
        var seasonStart = _names.Length - _seasons.Length - 1;
        var controls = Enumerable.Range(0, _names.Length).Select(i => i <= 2 || i == 5 || i >= seasonStart).ToList();
        (_beta, _se, R2, Sigma) = RidgeTobit(x, y, data.Select(c => c.PlayerId).ToList(), controls, censored);
        (ValidationError, ValidationLogScale) = validate ? Validate(group, grade, data, memberships) : (double.NaN, double.NaN);
    }

    /// <returns>Null when there are too few traded cards to fit.</returns>
    /// <remarks>Name-priced cards (<see cref="MarketGroups.PriceOutliers"/>) are left out of the fit; they are still priced by it.</remarks>
    /// <param name="memberships">Separate affiliation and feature team-colour memberships to price.</param>
    public static PriceModel? Fit(string group, int grade, IEnumerable<MarketCard> cards, IReadOnlyList<MarketMembership>? memberships = null)
    {
        var data = cards.Where(c => c.Group == group && c.IsTraded && c.PriceAt(grade) > Grades.FloorPrice && !MarketGroups.IsPriceOutlier(c)).ToList();
        return data.Count < 60 || data.Select(c => c.PlayerId).Distinct().Count() < 10 ? null : new PriceModel(group, grade, data, memberships);
    }

    private string Label(string tag) => _memberships.TryGetValue(tag, out var membership) ? membership.Name : MarketGroups.TagLabel(tag);

    /// <summary>Why a tag is not priced (MV-07), or null when it is.</summary>
    private string? ExclusionOf(string tag) => tag switch
    {
        _ when tag.StartsWith("body:") => "가격 모델에서 뺌 (MV-07: 체형은 어느 포지션에서도 가격 차이 없음)",
        _ when tag.StartsWith("skill:") => "가격 모델에서 뺌 (MV-07: 개인기는 카드마다 들쭉날쭉)",
        _ when tag.StartsWith("trait:") && !PricedTraits.Contains(tag[6..]) => NotConsistent,
        _ when _memberships.TryGetValue(tag, out var m) && m.Kind == FactorKind.FeatureTeamColor => "가격 모델에서 뺌 (MV-07: 특성 팀컬러는 일관된 가격 차이 없음)",
        _ => null,
    };

    private bool Has(MarketCard c, string tag) => _memberships.TryGetValue(tag, out var membership)
        ? membership.Members.Contains(c.SpId) : c.Tags.Contains(tag);

    private FactorKind KindOf(string tag) => tag.StartsWith("trait:") ? FactorKind.Trait : tag.StartsWith("skill:") ? FactorKind.Skill
        : _memberships.TryGetValue(tag, out var membership) ? membership.Kind : FactorKind.Body;

    private PhysicalFeature[] PhysicalFeatures(IReadOnlyList<MarketCard> data, MarketGroup group)
    {
        var result = new List<PhysicalFeature>();
        var allowed = PhysicalByGroup.GetValueOrDefault(group.Key, []);
        Add("height", "키", FactorKind.Height, HeightBuckets);
        Add("weight", "체중", FactorKind.Weight, WeightBuckets);
        return [.. result];

        void Add(string stat, string title, FactorKind kind, (string Key, string Label, int Min, int Max)[] buckets)
        {
            if (data.Count(c => c.Stats.ContainsKey(stat)) < 0.9 * data.Count) return;
            if (!allowed.Contains(stat))
            {
                _unpriced.Add(new($"{stat}:excluded", title, kind, data.Count, data.Select(c => c.PlayerId).Distinct().Count(), false,
                    $"가격 모델에서 뺌 (MV-07: 이 포지션은 {title}에 따른 일관된 가격 차이 없음)"));
                return;
            }
            var counts = buckets.Select(b => (Bucket: b, Cards: data.Count(c => c.Stats.TryGetValue(stat, out var v) && v >= b.Min && v <= b.Max))).ToList();
            var reference = counts.MaxBy(x => x.Cards).Bucket;
            var isCore = group.CoreStats.Any(s => s.Stat == stat);
            foreach (var (bucket, cards) in counts.Where(x => x.Cards > 0 && x.Bucket.Key != reference.Key))
            {
                var p = new PhysicalFeature($"{stat}:{bucket.Key}", $"{title} {bucket.Label} (기준 {reference.Label})", stat, kind,
                    bucket.Min, bucket.Max, cards, isCore);
                var players = data.Where(p.Has).Select(c => c.PlayerId).Distinct().Count();
                var controls = data.Where(c => c.Stats.TryGetValue(stat, out var v) && v >= reference.Min && v <= reference.Max)
                    .Select(c => c.PlayerId).Distinct().Count();
                if (players >= MinPlayers && controls >= MinPlayers) result.Add(p);
                else _unpriced.Add(new(p.Key, p.Name, kind, cards, players, isCore));
            }
        }
    }

    /// <summary>The model's price for the card, before the enhancement floor.</summary>
    public double Predict(MarketCard c) => Math.Exp(Dot(_beta, Features(c)));

    /// <summary>The price to show and compare with: the prediction, but never below the card's floor at this grade.</summary>
    public long Expected(MarketCard c) => Math.Max((long)Predict(c), FloorAt(c, Grade) ?? 0);

    /// <summary>The expectation carried to another grade along the card's own grade curve, and floored there.</summary>
    public long ExpectedAt(MarketCard c, int grade)
    {
        var price = c.PriceAt(grade);
        var atModelGrade = c.PriceAt(Grade);
        var expected = atModelGrade > 0 ? (long)(Predict(c) * price / atModelGrade) : price;
        return Math.Max(expected, FloorAt(c, grade) ?? 0);
    }

    /// <summary>
    /// What the market pays for this card beyond its OVR, season and salary (weak foot, stats relative to OVR, traits,
    /// skill moves, body), converted to OVR points at the card's own OVR: e.g. +1.4 means "priced like 1.4 OVR more".
    /// Used as the playing-value premium of a card; a market reading, not a measurement [추정].
    /// </summary>
    public double PremiumInOvr(MarketCard c)
    {
        var x = Features(c);
        const int firstPremium = 3; // after const, OVR, OVR²; salary (index 5) is a cost, not a quality
        var tagStart = 6 + _stats.Length + _physical.Length;
        var end = tagStart + _tags.Length;
        double premium = 0;
        for (var i = firstPremium; i < end; i++)
        {
            if (i == 5) continue;
            if (i >= tagStart && KindOf(_tags[i - tagStart]) is FactorKind.TeamColor or FactorKind.FeatureTeamColor) continue;
            premium += _beta[i] * x[i];
        }
        var slope = _beta[1] + 2 * _beta[2] * (c.Ovr1 - _ovrMean);
        return slope > 0.01 ? Math.Clamp(premium / slope, -6, 10) : 0;
    }

    public IReadOnlyList<PriceEffect> Effects() =>
        _names.Select((n, i) => (n, i))
            .Where(t => t.i > 0 && t.n != "OVR²" && !t.n.StartsWith("season:"))
            .Select(t => new PriceEffect(t.n, Pct(_beta[t.i]), Pct(_beta[t.i] - _critical * _se[t.i]), Pct(_beta[t.i] + _critical * _se[t.i]),
                _se[t.i] > 0 ? _beta[t.i] / _se[t.i] : 0, _counts.TryGetValue(t.n, out var n) ? n : null))
            .ToList();

    /// <summary>
    /// Every factor the model prices, in %, OVR points and BP on the group's median card. Stats are "+1 with the same
    /// OVR" (so a stat that only inflates the OVR shows as negative), traits and body types against cards without them.
    /// </summary>
    public IReadOnlyList<PriceFactor> Factors()
    {
        var slope = _beta[1];
        var core = _group.CoreStats.Select(s => s.Stat).ToHashSet();
        var result = new List<PriceFactor>();
        void Add(int i, string key, FactorKind kind, bool isCore, bool inflating = false)
        {
            var name = _names[i];
            result.Add(new PriceFactor(key, name, kind, Pct(_beta[i]), Pct(_beta[i] - _critical * _se[i]), Pct(_beta[i] + _critical * _se[i]),
                slope > 0.01 ? _beta[i] / slope : 0, (long)(_medianPrice * (Math.Exp(_beta[i]) - 1)),
                _counts.TryGetValue(name, out var n) ? n : null, isCore, inflating)
                { Players = _playerCounts.GetValueOrDefault(name, Players) });
        }
        Add(3, "foot:5", FactorKind.Foot, true);
        Add(4, "foot:4", FactorKind.Foot, false);
        Add(5, "pay", FactorKind.Salary, false);
        for (var i = 0; i < _stats.Length; i++)
            Add(6 + i, _stats[i], FactorKind.Stat, core.Contains(_stats[i]), _group.InflatingStats.Contains(_stats[i]));
        for (var i = 0; i < _physical.Length; i++)
            Add(6 + _stats.Length + i, _physical[i].Key, _physical[i].Kind, _physical[i].IsCore);
        for (var i = 0; i < _tags.Length; i++)
        {
            var tag = _tags[i];
            var kind = KindOf(tag);
            Add(6 + _stats.Length + _physical.Length + i, tag, kind, kind == FactorKind.Trait && _group.KeyTraits.Contains(tag[6..]));
        }
        result.AddRange(_unpriced.Select(f => new PriceFactor(f.Key, f.Name, f.Kind, double.NaN, double.NaN, double.NaN, 0, 0,
            f.Cards, f.IsCore, false) { Players = f.Players, Excluded = f.Excluded }));
        return result;
    }

    /// <summary>Typical (median) price of a traded card of the group at the model's grade.</summary>
    public long MedianPrice => _medianPrice;

    private double[] Features(MarketCard c)
    {
        var d = c.Ovr1 - _ovrMean;
        var x = new List<double>(_names.Length) { 1, d, d * d, c.WeakFoot >= 5 ? 1 : 0, c.WeakFoot == 4 ? 1 : 0, c.Pay };
        foreach (var s in _stats)
            x.Add(c.Stats.TryGetValue(s, out var v) ? v - c.Ovr1 : 0);
        foreach (var physical in _physical) x.Add(physical.Has(c) ? 1 : 0);
        foreach (var t in _tags) x.Add(Has(c, t) ? 1 : 0);
        foreach (var s in _seasons) x.Add(c.Season == s ? 1 : 0);
        x.Add(c.Season != _baseSeason && !_seasons.Contains(c.Season) ? 1 : 0);
        return [.. x];
    }

    private static double Pct(double b) => (Math.Exp(b) - 1) * 100;
    private static double Dot(double[] a, double[] b) { double s = 0; for (var i = 0; i < a.Length; i++) s += a[i] * b[i]; return s; }

    private static (double Error, double LogScale) Validate(string group, int grade, IReadOnlyList<MarketCard> data, IReadOnlyList<MarketMembership>? memberships)
    {
        var train = data.Where(c => Fold(c.PlayerId) != 0).ToList();
        var holdout = data.Where(c => Fold(c.PlayerId) == 0).ToList();
        if (train.Count < 50 || holdout.Count < 10 || train.Select(c => c.PlayerId).Distinct().Count() < 10) return (double.NaN, double.NaN);
        // Feature selection and reference buckets must also use training data only.
        var model = new PriceModel(group, grade, train, memberships, validate: false);
        // Against what the model shows: a floor price is only wrong by the part of the prediction above the floor.
        var residuals = holdout.Select(c => Math.Log(c.PriceAt(grade)) - Math.Log(Math.Max(model.Expected(c), 1))).ToList();
        var errors = residuals.Select(e => Math.Abs(Math.Exp(-e) - 1)).Order().ToList();
        var median = Median(residuals);
        var mad = Median(residuals.Select(e => Math.Abs(e - median)));
        return (Median(errors), Math.Max(Math.Max(1.4826 * mad, Math.Abs(median)), 0.05));
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    }

    internal static double Critical95(int degreesOfFreedom)
    {
        double[] t = [12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
            2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,
            2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042];
        if (degreesOfFreedom <= 30) return t[Math.Max(degreesOfFreedom, 1) - 1];
        const double z = 1.95996398454;
        return z + (z * z * z + z) / (4 * degreesOfFreedom)
            + (5 * Math.Pow(z, 5) + 16 * z * z * z + 3 * z) / (96 * degreesOfFreedom * degreesOfFreedom);
    }

    private static int Fold(long id)
    {
        unchecked
        {
            var x = (ulong)id;
            x ^= x >> 33;
            x *= 0xff51afd7ed558ccdUL;
            x ^= x >> 33;
            x *= 0xc4ceb9fe1a85ec53UL;
            x ^= x >> 33;
            return (int)(x % 5);
        }
    }

    /// <summary>
    /// Ridge regression on standardised columns, as in MV-07 (tools/market/determinants.py <c>fit</c>): the columns in
    /// <paramref name="controls"/> are not shrunk, the others are pressed toward 0 with <paramref name="lambda"/>. Censored
    /// rows (the true value is at most the observed one: a floor price) are handled by Tobit EM: each round replaces them
    /// by their expected value below the floor given the current fit. Standard errors are player-clustered (CR1) on the
    /// completed data, so they lean a little narrow for heavily censored groups.
    /// </summary>
    internal static (double[] Beta, double[] Se, double R2, double Sigma) RidgeTobit(IReadOnlyList<double[]> x, IReadOnlyList<double> y,
        IReadOnlyList<int> clusters, IReadOnlyList<bool> controls, IReadOnlyList<bool>? censored = null, double lambda = Ridge, int iterations = 60)
    {
        var n = x.Count;
        var k = x[0].Length;
        var mu = new double[k];
        var sd = new double[k];
        for (var j = 1; j < k; j++)
        {
            for (var i = 0; i < n; i++) mu[j] += x[i][j];
            mu[j] /= n;
            for (var i = 0; i < n; i++) sd[j] += (x[i][j] - mu[j]) * (x[i][j] - mu[j]);
            sd[j] = Math.Sqrt(sd[j] / n);
            if (sd[j] == 0) sd[j] = 1;
        }
        sd[0] = 1;
        var z = new double[n][];
        for (var i = 0; i < n; i++)
        {
            z[i] = new double[k];
            z[i][0] = 1;
            for (var j = 1; j < k; j++) z[i][j] = (x[i][j] - mu[j]) / sd[j];
        }
        var a = new double[k, k];
        foreach (var row in z)
            for (var p = 0; p < k; p++)
                for (var q = 0; q < k; q++)
                    a[p, q] += row[p] * row[q];
        for (var j = 1; j < k; j++) a[j, j] += controls[j] ? 1e-3 : lambda;
        var inv = Invert(a);

        double[] Solve(double[] target)
        {
            var zty = new double[k];
            for (var i = 0; i < n; i++)
                for (var j = 0; j < k; j++)
                    zty[j] += z[i][j] * target[i];
            var b = new double[k];
            for (var p = 0; p < k; p++) for (var q = 0; q < k; q++) b[p] += inv[p, q] * zty[q];
            return b;
        }
        double Fitted(double[] b, int i) => Dot(b, z[i]);

        var yi = y.ToArray();
        var bz = Solve(yi);
        var cens = censored?.ToArray() ?? new bool[n];
        var sigma = double.NaN;
        if (cens.Any(c => c))
        {
            var extra = new double[n];
            for (var round = 0; round < iterations; round++)
            {
                if (double.IsNaN(sigma))
                {
                    var free = Enumerable.Range(0, n).Where(i => !cens[i]).Select(i => y[i] - Fitted(bz, i)).ToList();
                    sigma = Math.Max(StdDev(free), 0.05);
                }
                for (var i = 0; i < n; i++)
                {
                    if (!cens[i]) continue;
                    var mhat = Fitted(bz, i);
                    var t = (y[i] - mhat) / sigma;
                    var m = Mills(t);
                    yi[i] = mhat - sigma * m;
                    extra[i] = sigma * sigma * Math.Max(1 - t * m - m * m, 1e-6);
                }
                var next = Solve(yi);
                double sse = 0;
                for (var i = 0; i < n; i++) { var e = yi[i] - Fitted(next, i); sse += e * e + (cens[i] ? extra[i] : 0); }
                sigma = Math.Max(Math.Sqrt(sse / n), 0.05);
                var change = next.Zip(bz, (u, v) => Math.Abs(u - v)).Max();
                bz = next;
                if (change < 1e-6) break;
            }
        }

        var mean = yi.Average();
        double sseAll = 0, sst = 0;
        var scores = new Dictionary<int, double[]>();
        for (var i = 0; i < n; i++)
        {
            var e = yi[i] - Fitted(bz, i);
            sseAll += e * e;
            sst += (yi[i] - mean) * (yi[i] - mean);
            if (!scores.TryGetValue(clusters[i], out var score)) scores[clusters[i]] = score = new double[k];
            for (var j = 0; j < k; j++) score[j] += e * z[i][j];
        }
        if (double.IsNaN(sigma)) sigma = Math.Max(Math.Sqrt(sseAll / n), 0.05);
        var cr1 = scores.Count / (double)Math.Max(scores.Count - 1, 1) * (n - 1) / Math.Max(n - k, 1);
        var beta = new double[k];
        var se = new double[k];
        for (var j = 0; j < k; j++)
        {
            double variance = 0;
            foreach (var score in scores.Values)
            {
                double s = 0;
                for (var q = 0; q < k; q++) s += inv[j, q] * score[q];
                variance += s * s;
            }
            se[j] = Math.Sqrt(Math.Max(variance * cr1, 0)) / sd[j];
            beta[j] = bz[j] / sd[j];
        }
        // Back to the original columns: the constant absorbs the centring.
        beta[0] = bz[0] - Enumerable.Range(1, k - 1).Sum(j => mu[j] * bz[j] / sd[j]);
        return (beta, se, sst > 0 ? 1 - sseAll / sst : 0, sigma);
    }

    private static double StdDev(IReadOnlyList<double> v)
    {
        if (v.Count == 0) return 0;
        var m = v.Average();
        return Math.Sqrt(v.Sum(e => (e - m) * (e - m)) / v.Count);
    }

    private static readonly double[] Erfc = [-1.26551223, 1.00002368, 0.37409196, 0.09678418, -0.18628806, 0.27886807,
        -1.13520398, 1.48851587, -0.82215223, 0.17087277];

    /// <summary>φ(a) / Φ(a), stable for very negative a (Numerical Recipes erfc, relative error under 1.2e-7).</summary>
    internal static double Mills(double a)
    {
        var u = -a / Math.Sqrt(2);
        var zAbs = Math.Abs(u);
        var t = 1 / (1 + 0.5 * zAbs);
        double poly = 0;
        for (var i = Erfc.Length - 1; i >= 0; i--) poly = poly * t + Erfc[i];
        if (u >= 0) return 2 / (Math.Sqrt(2 * Math.PI) * t * Math.Exp(poly));
        var bigPhi = 1 - 0.5 * t * Math.Exp(-zAbs * zAbs + poly);
        return Math.Exp(-a * a / 2) / Math.Sqrt(2 * Math.PI) / bigPhi;
    }

    /// <summary>Regularized least squares. Binary factors with tiny samples are shrunk instead of producing huge premiums.</summary>
    internal static (double[] Beta, double[] Se, double R2) Ols(IReadOnlyList<(double[] X, double Y)> rows, IReadOnlyList<int>? clusters = null,
        double ridge = 2, int absorbedParameters = 0)
    {
        var k = rows[0].X.Length;
        var xtx = new double[k, k];
        var xty = new double[k];
        foreach (var (x, y) in rows)
        {
            for (var i = 0; i < k; i++)
            {
                if (x[i] == 0) continue;
                xty[i] += x[i] * y;
                for (var j = 0; j < k; j++) xtx[i, j] += x[i] * x[j];
            }
        }
        for (var i = 1; i < k; i++) xtx[i, i] += i <= 2 ? 1e-6 : ridge;
        var inv = Invert(xtx);
        var beta = new double[k];
        for (var i = 0; i < k; i++) for (var j = 0; j < k; j++) beta[i] += inv[i, j] * xty[j];
        var mean = rows.Average(r => r.Y);
        double sse = 0, sst = 0;
        foreach (var (x, y) in rows)
        {
            var e = y - Dot(beta, x);
            sse += e * e;
            sst += (y - mean) * (y - mean);
        }
        var scores = new Dictionary<int, double[]>();
        for (var row = 0; row < rows.Count; row++)
        {
            var (x, y) = rows[row];
            var e = y - Dot(beta, x);
            var cluster = clusters?[row] ?? row;
            if (!scores.TryGetValue(cluster, out var score)) scores[cluster] = score = new double[k];
            for (var i = 0; i < k; i++) score[i] += e * x[i];
        }
        var meat = new double[k, k];
        foreach (var score in scores.Values)
        {
            for (var i = 0; i < k; i++)
                for (var j = 0; j < k; j++)
                    meat[i, j] += score[i] * score[j];
        }
        var cr1 = scores.Count / (double)Math.Max(scores.Count - 1, 1)
            * (rows.Count - 1) / Math.Max(rows.Count - k - absorbedParameters, 1);
        var se = new double[k];
        // Player-clustered ridge covariance: CR1 × A⁻¹(Σ score_g score_g')A⁻¹.
        for (var i = 0; i < k; i++)
        {
            double variance = 0;
            for (var j = 0; j < k; j++)
                for (var l = 0; l < k; l++)
                    variance += inv[i, j] * meat[j, l] * inv[l, i];
            se[i] = Math.Sqrt(Math.Max(variance * cr1, 0));
        }
        return (beta, se, sst > 0 ? 1 - sse / sst : 0);
    }

    private static double[,] Invert(double[,] a)
    {
        var n = a.GetLength(0);
        var m = new double[n, 2 * n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++) m[i, j] = a[i, j];
            m[i, n + i] = 1;
        }
        for (var col = 0; col < n; col++)
        {
            var piv = col;
            for (var r = col + 1; r < n; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
            if (Math.Abs(m[piv, col]) < 1e-12) throw new InvalidOperationException($"Singular matrix at column {col}.");
            if (piv != col) for (var j = 0; j < 2 * n; j++) (m[col, j], m[piv, j]) = (m[piv, j], m[col, j]);
            var p = m[col, col];
            for (var j = 0; j < 2 * n; j++) m[col, j] /= p;
            for (var r = 0; r < n; r++)
            {
                var f = m[r, col];
                if (r == col || f == 0) continue;
                for (var j = 0; j < 2 * n; j++) m[r, j] -= f * m[col, j];
            }
        }
        var inv = new double[n, n];
        for (var i = 0; i < n; i++) for (var j = 0; j < n; j++) inv[i, j] = m[i, n + j];
        return inv;
    }
}

/// <summary>
/// Detailed card search shared by the value finder and the hidden ranker picks. Everything is optional; OVR is at the
/// grade being looked at and at the position (or the group's best position).
/// </summary>
public sealed record CardFilter
{
    public long MinPrice { get; init; }
    public long MaxPrice { get; init; } = long.MaxValue;
    /// <summary>
    /// Cards below this OVR are not playable at the top: e.g. 135 + 적응도 5 + 팀컬러·강화 팀컬러 8 = 148.
    /// </summary>
    public int? MinOvr { get; init; }
    public int? MaxOvr { get; init; }
    public int MinWeakFoot { get; init; }
    /// <summary>Traits the card must all have ("라인 브레이커").</summary>
    public IReadOnlyList<string> Traits { get; init; } = [];
    public int SkillMove { get; init; }
    /// <summary>"thin" (마름), "normal" (보통) or "heavy" (건장); null = any. Only for groups whose body type is collected.</summary>
    public string? Body { get; init; }
    public int? MinHeight { get; init; }
    public int? MaxHeight { get; init; }
    public int? MaxPay { get; init; }
    /// <summary>Stat floors, e.g. 속력 ≥ 130 (stat key → value at +1).</summary>
    public IReadOnlyDictionary<string, int> MinStats { get; init; } = new Dictionary<string, int>();
    /// <summary>Only cards whose core stats are at least this far above (−: below) their OVR, see <see cref="MarketGroup.CoreGap"/>.</summary>
    public double? MinCoreGap { get; init; }
    public string? Name { get; init; }
    /// <summary>Only these cards, e.g. members of the team colours rankers use; null = any.</summary>
    public IReadOnlySet<long>? Members { get; init; }
    /// <summary>Cards almost nobody rated are rarely traded; their low price says little.</summary>
    public int MinRatings { get; init; } = 10;
    /// <summary>Only cards of these seasons (e.g. "26TOTS"); null = any.</summary>
    public IReadOnlySet<string>? OnlySeasons { get; init; }
    /// <summary>Seasons left out, e.g. ones that hardly trade.</summary>
    public IReadOnlySet<string> ExcludedSeasons { get; init; } = new HashSet<string>();

    public bool Matches(MarketCard c, int grade, string? position = null)
    {
        var price = c.PriceAt(grade);
        if (!c.IsTraded || price <= Grades.FloorPrice || price < MinPrice || price > MaxPrice) return false;
        if (c.RatingCount < MinRatings || c.WeakFoot < MinWeakFoot) return false;
        var ovr = position is null ? c.OvrAt(grade) : c.OvrAt(position, grade) ?? c.OvrAt(grade);
        if (ovr < MinOvr || ovr > MaxOvr) return false;
        if (Traits.Any(t => !c.Tags.Contains($"trait:{t}"))) return false;
        if (SkillMove > 0 && !MarketGroups.SkillTags.Any(s => s >= SkillMove && c.Tags.Contains($"skill:{s}"))) return false;
        if (Body is { } body && BodyOf(c) != body) return false;
        if (MinHeight is not null || MaxHeight is not null)
        {
            if (!c.Stats.TryGetValue("height", out var h) || h < MinHeight || h > MaxHeight) return false;
        }
        if (c.Pay > MaxPay) return false;
        foreach (var (stat, min) in MinStats)
            if (!c.Stats.TryGetValue(stat, out var v) || v < min) return false;
        if (MinCoreGap is { } gap && (MarketGroups.Get(c.Group).CoreGap(c) is not { } g || g < gap)) return false;
        if (Name is { Length: > 0 } name && !c.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) return false;
        if (Members is not null && !Members.Contains(c.SpId)) return false;
        if (OnlySeasons is not null && !OnlySeasons.Contains(c.Season) || ExcludedSeasons.Contains(c.Season)) return false;
        return true;
    }

    public static string BodyOf(MarketCard c) => c.Tags.Contains("body:thin") ? "thin" : c.Tags.Contains("body:heavy") ? "heavy" : "normal";
}

public sealed record ValueQuery
{
    public required string Group { get; init; }
    public int Grade { get; init; } = 8;
    public CardFilter Filter { get; init; } = new();
}

public sealed record ValuePick(MarketCard Card, int Grade, long Price, long Expected)
{
    public double ModelError { get; init; } = double.NaN;
    public double LogErrorScale { get; init; } = double.NaN;
    public double EvidenceScore => PriceEvidence.Score(Price, Expected, LogErrorScale);
    public bool OutsideModelError => EvidenceScore < -1.96;
    /// <summary>Price relative to the model's expectation: −0.4 = 40% cheaper than similar cards.</summary>
    public double Discount => Price / (double)Expected - 1;
}

/// <summary>A holdout-error comparison, not a confidence interval or proof of mispricing.</summary>
public static class PriceEvidence
{
    public static double Score(long price, long expected, double scale) => price > 0 && expected > 0 && double.IsFinite(scale) && scale > 0
        ? Math.Log(price / (double)expected) / scale : double.NaN;

    // Rating count is a demand proxy, not measured trading volume. Unvalidated models sort last.
    public static double Rank(double score, int ratings) => double.IsFinite(score)
        ? score * Math.Clamp(Math.Log(1 + Math.Max(ratings, 0)) / Math.Log(101), 0.5, 1) : double.PositiveInfinity;

    public static string Label(double score) => !double.IsFinite(score) ? "오차 검증 부족"
        : score < -1.96 ? "저평가 후보 [추정]" : "모델 오차 범위 내";
    public static string ErrorLabel(double error) => double.IsFinite(error) ? $"{error:P0}" : "표본 부족";
}

public static class ValueFinder
{
    /// <remarks>A card whose price is its enhancement floor is left out: the floor rule set that price, so it cannot be a bargain.</remarks>
    public static IReadOnlyList<ValuePick> Find(PriceModel model, IEnumerable<MarketCard> cards, ValueQuery q) =>
        cards.Where(c => c.Group == q.Group && q.Filter.Matches(c, q.Grade) && !PriceModel.AtFloor(c, q.Grade))
            .Select(c => new ValuePick(c, q.Grade, c.PriceAt(q.Grade), model.ExpectedAt(c, q.Grade))
                { ModelError = model.ValidationError, LogErrorScale = model.ValidationLogScale })
            .OrderBy(p => PriceEvidence.Rank(p.EvidenceScore, p.Card.RatingCount))
            .ToList();
}
