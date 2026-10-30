namespace FcHelper.Market;

public sealed record PriceEffect(string Name, double Percent, double Low, double High, double T, int? Cards);

/// <summary>One held-out card, priced without training on it. Prices include the enhancement floor.</summary>
public sealed record PriceValidationRow(MarketCard Card, int Grade, int Fold, long Expected)
{
    public double LogResidual => Math.Log(Card.PriceAt(Grade) / (double)Math.Max(Expected, 1));
    public double Error => Math.Abs(Math.Exp(-LogResidual) - 1);
    public bool AtFloor => PriceModel.AtFloor(Card, Grade);
}

public enum FactorKind { Stat, Height, Weight, Trait, Skill, Body, Foot, Salary, TeamColor, FeatureTeamColor }

/// <summary>One part of a card's expected price, as a log change against the average card of the market. [추정]</summary>
public sealed record PriceReason(string Label, double LogEffect, string? Detail = null)
{
    public double Percent => (Math.Exp(LogEffect) - 1) * 100;
}

/// <summary>A card's expected price taken apart: Base (its OVR, season and position) × each reason = the prediction.</summary>
public sealed record PriceExplanation(double Base, IReadOnlyList<PriceReason> Reasons, double Predicted, long Expected);

/// <summary>
/// Where the market price of a card like this one falls: the middle 80% of the held-out errors of the cards the model is
/// about as sure of. Tier 0 is the quarter it knows best, 3 the quarter it knows least. [추정]
/// </summary>
public sealed record PriceBand(long Low, long High, int Tier, double Error)
{
    public string Trust => Tier switch { 0 => "높음", 1 => "보통", 2 => "낮음", _ => "매우 낮음" };
}

/// <summary>A separately priced team-colour membership. Affiliation and feature colours must not be merged.</summary>
public sealed record MarketMembership(string Key, string Name, FactorKind Kind, IReadOnlySet<long> Members)
{
    /// <summary>Share of rankers fielding this colour (0.07 = 7%); 0 when unknown.</summary>
    public double Share { get; init; }
}

/// <summary>
/// What one thing is worth on the market at a position and grade, everything else equal: in % of the price, in OVR
/// points (the price of that many extra OVR) and in BP on a typical card of the group. [추정: 시장 회귀]
/// </summary>
public sealed record PriceFactor(string Key, string Name, FactorKind Kind, double Percent, double Low, double High, double OvrEquivalent,
    long BpAtMedian, int? Cards, bool IsCore, bool IsInflating)
{
    public int? Players { get; init; }
    /// <summary>Traits: footballers with a card with it and one without, the comparisons the premium is measured on (MV-09).</summary>
    public int? Comparisons { get; init; }
    /// <summary>Traits: how much the premium grows per OVR above <see cref="PriceModel.TraitHinge"/>, in log points (0.09 ≈ +9%p).</summary>
    public double PerOvr { get; init; } = double.NaN;
    /// <summary>
    /// Traits: the premium on a card at or under <see cref="PriceModel.TraitHinge"/>. <see cref="Percent"/>, the interval and
    /// the verdict of a trait are for a top card (<see cref="PriceModel.TraitTopOvr"/>), where traits are bought.
    /// </summary>
    public double PercentBelowHinge { get; init; } = double.NaN;
    /// <summary>The t value behind <see cref="Low"/> and <see cref="High"/>.</summary>
    public double Critical { get; init; } = 1.96;
    /// <summary>Why the factor is left out of the price model; null when priced.</summary>
    public string? Excluded { get; init; }
    public bool Estimated => double.IsFinite(Percent);
    /// <summary>At least ten footballers (traits: eight compared within the same footballer) and an interval excluding zero.</summary>
    public bool Clear => Estimated && (Players is null || Players >= 10) && (Comparisons is null || Comparisons >= 8) && (Low > 0 || High < 0);
}

/// <summary>
/// Hedonic price model for one position group at one enhancement grade:
///   ln(price) ~ OVR + OVR² + salary + season (not shrunk) + weak foot + (stat − OVR) per stat + tags (ridge) + player (ridge)
/// A coefficient reads as "everything else equal, this changes the price by X%" in today's market. It is an
/// association: a tag can also stand in for things the model does not see (feel, team colour, uncollected stats).
/// MV-08: prices sitting on their enhancement floor (<see cref="Floors"/>) are fitted as "worth at most this" (Tobit EM).
/// MV-09: every footballer with several cards gets his own effect, so a trait is priced by comparing the same
/// footballer's cards (CH 앙리 with 라인 브레이커 against WG 앙리 without), not different footballers of a season; the
/// expected price of a card uses what the same footballer's other cards say about him.
/// MV-12 (<see cref="FitPool"/>): a card has one price whatever groups list it, so the field positions share one fit:
/// a footballer's winger card informs his striker card, a season's premium is not re-estimated from each group's few
/// cards. Salary, OVR, stats and traits still differ by position through ridged position columns, and each position
/// reads its own values through <see cref="View"/>. On unseen +11 cards this lowered the error on the footballers it
/// was not chosen on (tools/market/mv12_pool.py).
/// MV-14: starts in stored official matches, and a small tree model blended in (<see cref="TreeWeight"/>); each card's trees
/// did not see that card. <see cref="BandOf"/> says how far to trust a card's estimate (tools/market/mv14_blend.py, mv14_mix.py).
/// </summary>
public sealed class PriceModel
{
    private const int MinPlayers = 8, MinSeasonCards = 5;
    private sealed record UnpricedFactor(string Key, string Name, FactorKind Kind, int Cards, int Players, bool IsCore, string? Excluded = null, string? Family = null);
    /// <summary>A column beyond the fixed ones. Premium: counts as playing value in <see cref="PremiumInOvr"/>.</summary>
    private sealed record ExtraColumn(string Name, Func<MarketCard, double> Value, double ZRidge = double.NaN, double RawRidge = double.NaN, bool Premium = false, string? Reason = null);

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

    /// <summary>
    /// Ridges in the columns' own units (MV-09). In the fit a footballer's effect is hardly shrunk (<see cref="FitPlayerRidge"/>):
    /// a shrunk one leaves part of the favourites' name value for the traits they hold more often (a true +50% came out
    /// +98% at ridge 2). The price shown for a card trusts what his n other cards say by n/(n + <see cref="PlayerShrink"/>).
    /// A trait is shrunk as little as a 0/1 factor in the old least squares (<see cref="Ols"/>, ridge 2).
    /// </summary>
    public const double FitPlayerRidge = 0.05, TraitRidge = 2;
    /// <summary>MV-12: ridge on a stat's common value in the pooled fit; its per-position part keeps <see cref="Ridge"/>.</summary>
    public const double PoolStatRidge = 30;
    /// <summary>MV-12: a footballer's other card counts e^(−ΔOVR/3); chosen on half the footballers, confirmed on the other half.</summary>
    public const double NearCardOvr = 3;
    private const int MinViewCards = 30, MaxCardsForAllFolds = 4000;
    /// <summary>MV-14: the tree part of the price, chosen on half the footballers, confirmed on the other half (25~35% are alike).</summary>
    public const double TreeWeight = 0.3;
    private const int MinCardsForTrees = 300, MinCardsForBands = 200;
    /// <summary>Roughly what one OVR is worth in log price: takes OVR out of a footballer's or a season's average for the trees.</summary>
    private const double TreeOvrSlope = 0.45;
    /// <summary>
    /// MV-10: the +11 five-card-fold audit moves the hinge from 118 to 115. At a +1 OVR 115 training cut, top
    /// strikers without a trait move from −0.12 to +0.02 log residual, CF −0.22 to −0.09 and W −0.17 to −0.09,
    /// with little change in overall error. The shape is a market association, not measured playing strength.
    /// </summary>
    public const int TraitHinge = 115;
    /// <summary>The card a trait's premium is shown for: +1 OVR 122 (+11 OVR 143), the playable top of the market.</summary>
    public const int TraitTopOvr = 122;
    public const double TraitSlopeRidge = 5;
    /// <summary>
    /// MV-10: 60 within-season fake-trait draws per +11 position/cut, with the 115 hinge, give pooled |t| 95th
    /// percentiles at most 2.63 (old hinge: 3.11). Keep 3.0. Rare individual traits can still need wider intervals;
    /// this empirical pointwise safeguard is neither simultaneous nor causal inference.
    /// </summary>
    public const double TraitCritical = 3.0;

    /// <summary>
    /// Height and weight buckets are priced only where they matter: 스트라이커 and 골키퍼 (MV-07), 수비형 미드 height, and
    /// 센터백 (the user's call, 2026-10-25; MV-07 found no consistent price effect there, so the ridge keeps it small).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> PhysicalByGroup = new Dictionary<string, string[]>
    {
        ["ST"] = ["height", "weight"], ["CB"] = ["height", "weight"], ["CDM"] = ["height"], ["GK"] = ["weight"],
    };

    private sealed record PhysicalFeature(string Key, string Name, string Stat, FactorKind Kind, int Min, int Max, int Cards, bool IsCore)
    {
        /// <summary>Pooled fit: only cards of this position are in the bucket's column.</summary>
        public string? Family { get; init; }
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

    private const string ReasonColor = "reason:color", ReasonPay = "reason:pay", ReasonStat = "reason:stat:", ReasonUsage = "reason:usage";
    private readonly IReadOnlyDictionary<long, CardUse>? _usage;
    private PriceTrees.Part[]? _treeParts;
    // A training card's tree estimate, from the part that did not see the card.
    private readonly Dictionary<long, double> _treeLog = [];
    private readonly Dictionary<long, double[]> _treeRows = [];
    private string[] _treeFamilies = [];
    private string[] _treeTraits = [];
    private MarketMembership[] _treeColours = [];
    private Reliability? _reliability;
    private readonly double[] _meanFeature;
    private readonly bool _pooled;
    private readonly IReadOnlyList<MarketCard> _data;
    private readonly Dictionary<long, string> _familyOf = [];
    private readonly Dictionary<long, HashSet<string>> _listedIn = [];
    private readonly List<ExtraColumn> _extras = [];
    private readonly Dictionary<string, int> _extraAt = [];
    private readonly Dictionary<string, (double Error, double Scale)> _validationOf = [];
    private readonly string[] _stats;
    private readonly PhysicalFeature[] _physical;
    private readonly string[] _tags;
    private readonly string[] _traitSlopes;
    private readonly int? _traitHinge;
    private readonly bool _linearTraits;
    private long _medianPrice;
    private MarketGroup _group;
    private readonly IReadOnlyDictionary<string, MarketMembership> _memberships;

    /// <summary>Pseudo-tag: the card counts for one of the team colours rankers use most (priced like a tag).</summary>
    public const string RankerColorTag = "tc:ranker";
    private readonly string[] _seasons;
    private readonly string _baseSeason;
    private readonly double _ovrMean;
    private readonly double[] _beta;
    private readonly double[] _se;
    // Per footballer, his cards' pull on the coefficients (clustered): any combination's variance is Σ (a·row)².
    private readonly double[][] _influence;
    private long _topMedianPrice;
    private readonly string[] _names;
    private readonly Dictionary<string, int> _counts;
    private readonly Dictionary<string, int> _playerCounts;
    private readonly List<UnpricedFactor> _unpriced = [];
    private readonly Dictionary<string, int> _comparisons = [];
    // What the same footballer's other cards say about him (log scale): per training card leaving it out, per footballer.
    private readonly Dictionary<int, List<(long SpId, int Ovr, double Residual)>> _footballer = [];
    private readonly double _critical;
    /// <summary>Spread of log prices around the model (Tobit sigma).</summary>
    public double Sigma { get; }
    /// <summary>
    /// How many cards' worth of doubt a footballer's effect starts with (empirical Bayes: one card's noise σ² over the
    /// spread τ² of footballers' effects, 0.1–10): with n other cards the price trusts them by n/(n + this).
    /// </summary>
    public double PlayerShrink { get; }
    /// <summary>Training cards whose price sat on its enhancement floor.</summary>
    public int FloorCards { get; }

    public string Group { get; private set; }
    public int Grade { get; }
    public int Cards { get; private set; }
    public int Players { get; private set; }
    public double R2 { get; }
    /// <summary>Median absolute percentage error on a deterministic 20% holdout.</summary>
    public double ValidationError { get; private set; }
    /// <summary>Robust spread of holdout log-price errors; used to compare price gaps across position models.</summary>
    public double ValidationLogScale { get; private set; }

    private PriceModel(string group, int grade, IReadOnlyList<MarketCard> data, IReadOnlyList<MarketMembership>? memberships, bool validate = true,
        int? traitHinge = TraitHinge, bool linearTraits = false, bool pooled = false, IReadOnlyDictionary<long, CardUse>? usage = null)
    {
        _pooled = pooled;
        _usage = usage;
        _data = data;
        foreach (var c in data) _familyOf[c.SpId] = pooled ? FamilyFrom(c) : group;
        _traitHinge = traitHinge;
        _linearTraits = linearTraits;
        _memberships = (memberships ?? []).ToDictionary(m => m.Key);
        Group = group;
        Grade = grade;
        Cards = data.Count;
        Players = data.Select(c => c.PlayerId).Distinct().Count();
        _critical = Critical95(Players - 1);
        var g = _group = MarketGroups.Get(group);
        var statPool = pooled ? MarketGroups.All.Where(x => x.Key != "GK").SelectMany(x => x.AllStats).Distinct() : g.AllStats;
        _stats = statPool.Where(s => s is not ("height" or "weight") && data.Count(c => c.Stats.ContainsKey(s)) >= 0.9 * data.Count).ToArray();
        _physical = pooled
            ? [.. PhysicalByGroup.Keys.Where(f => f != "GK").SelectMany(f =>
                data.Where(c => _familyOf[c.SpId] == f).ToList() is { Count: >= 30 } subset ? PhysicalFeatures(subset, MarketGroups.Get(f), f) : [])]
            : PhysicalFeatures(data, g);
        var prices = data.Select(c => c.PriceAt(grade)).Order().ToList();
        _medianPrice = prices[prices.Count / 2];
        var topPrices = data.Where(c => c.Ovr1 >= TraitTopOvr).Select(c => c.PriceAt(grade)).Order().ToList();
        _topMedianPrice = topPrices.Count >= 5 ? topPrices[topPrices.Count / 2] : _medianPrice;
        var seasonCounts = data.GroupBy(c => c.Season).ToDictionary(x => x.Key, x => x.Count());
        _baseSeason = seasonCounts.MaxBy(kv => kv.Value).Key;
        _seasons = seasonCounts.Where(kv => kv.Value >= MinSeasonCards && kv.Key != _baseSeason).Select(kv => kv.Key).Order().ToArray();
        var tagCounts = data.SelectMany(c => c.Tags).GroupBy(t => t).ToDictionary(x => x.Key, x => x.Count());
        foreach (var membership in _memberships.Values)
            tagCounts[membership.Key] = data.Count(c => membership.Members.Contains(c.SpId));
        // MV-07: body types, skill moves and feature team colours are not consistent price factors (the user's call).
        foreach (var tag in tagCounts.Keys.Where(t => ExclusionOf(t) is not null).ToList())
        {
            _unpriced.Add(new(tag, Label(tag), KindOf(tag), tagCounts[tag], data.Where(c => Has(c, tag)).Select(c => c.PlayerId).Distinct().Count(),
                KindOf(tag) == FactorKind.Trait && g.KeyTraits.Contains(tag[6..]), ExclusionOf(tag)));
            tagCounts.Remove(tag);
        }
        var tagPlayers = tagCounts.Keys.ToDictionary(t => t, t => data.Where(c => Has(c, t)).Select(c => c.PlayerId).Distinct().Count());
        var tagControls = tagCounts.Keys.ToDictionary(t => t, t => data.Where(c => !Has(c, t)).Select(c => c.PlayerId).Distinct().Count());
        bool Supported(string t) => tagPlayers[t] >= MinPlayers && tagControls[t] >= MinPlayers;
        foreach (var t in tagCounts.Keys.Where(t => t.StartsWith("trait:")))
            _comparisons[Label(t)] = data.GroupBy(c => c.PlayerId).Count(p => p.Any(c => Has(c, t)) && p.Any(c => !Has(c, t)));
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
        _traitSlopes = traitHinge is null && !linearTraits ? [] : _tags.Where(t => t.StartsWith("trait:")).ToArray();
        _ovrMean = data.Average(c => c.Ovr1);

        if (pooled || (memberships ?? []).Any(m => m.Share > 0)) BuildExtras(data, memberships ?? [], pooled);
        _names = ["const", "OVR +1", "OVR²", "양발 (약발 5)", "약발 4", "급여 +1",
            .. _stats.Select(s => $"{MarketGroups.StatNames.GetValueOrDefault(s, s)} +1 (같은 OVR)"),
            .. _physical.Select(p => p.Name),
            .. _tags.Select(Label), .. _traitSlopes.Select(t => $"{Label(t)} × OVR {TraitHinge} 위 +1"),
            .. _extras.Select(e => "extra:" + e.Name), .. _seasons.Select(s => $"season:{s}"), "season:기타"];
        var extraStart = 6 + _stats.Length + _physical.Length + _tags.Length + _traitSlopes.Length;
        for (var i = 0; i < _extras.Count; i++) _extraAt[_extras[i].Name] = extraStart + i;
        _counts = tagCounts.ToDictionary(kv => Label(kv.Key), kv => kv.Value);
        _playerCounts = tagPlayers.ToDictionary(kv => Label(kv.Key), kv => kv.Value);
        foreach (var p in _physical) _counts[p.Name] = p.Cards;
        foreach (var p in _physical) _playerCounts[p.Name] = data.Where(c => (p.Family is null || p.Family == _familyOf[c.SpId]) && p.Has(c)).Select(c => c.PlayerId).Distinct().Count();
        _counts["양발 (약발 5)"] = data.Count(c => c.WeakFoot >= 5);
        _counts["약발 4"] = data.Count(c => c.WeakFoot == 4);
        _playerCounts["양발 (약발 5)"] = data.Where(c => c.WeakFoot >= 5).Select(c => c.PlayerId).Distinct().Count();
        _playerCounts["약발 4"] = data.Where(c => c.WeakFoot == 4).Select(c => c.PlayerId).Distinct().Count();

        var x = data.Select(Features).ToList();
        _meanFeature = Enumerable.Range(0, _names.Length).Select(j => x.Average(row => row[j])).ToArray();
        var y = data.Select(c => Math.Log(c.PriceAt(grade))).ToList();
        var censored = data.Select(c => AtFloor(c, grade)).ToList();
        FloorCards = censored.Count(b => b);
        // Not shrunk: OVR, OVR², salary and the seasons (shrinking the seasons lets other factors take their premium).
        var seasonStart = _names.Length - _seasons.Length - 1;
        var controls = Enumerable.Range(0, _names.Length).Select(i => i <= 2 || i == 5 || i >= seasonStart).ToList();
        var tagStart = 6 + _stats.Length + _physical.Length;
        var slopeStart = tagStart + _tags.Length;
        var rawPenalty = Enumerable.Range(0, _names.Length)
            .Select(i => i >= tagStart && i < slopeStart && _tags[i - tagStart].StartsWith("trait:") ? TraitRidge
                : i >= slopeStart && i < slopeStart + _traitSlopes.Length ? TraitSlopeRidge
                : i >= extraStart && i < extraStart + _extras.Count ? _extras[i - extraStart].RawRidge : double.NaN).ToList();
        // Pooled: a stat's common value is held less tightly than its per-position part (chosen on half the footballers).
        var zPenalty = Enumerable.Range(0, _names.Length)
            .Select(i => i >= extraStart && i < extraStart + _extras.Count ? _extras[i - extraStart].ZRidge
                : pooled && (i is 3 or 4 || i >= 6 && i < 6 + _stats.Length) ? PoolStatRidge : double.NaN).ToList();
        // A footballer with several cards gets his own effect; one card alone has nothing to compare with.
        var several = data.GroupBy(c => c.PlayerId).Where(p => p.Count() >= 2).Select((p, i) => (p.Key, i)).ToDictionary(p => p.Key, p => p.i);
        var effects = data.Select(c => several.TryGetValue(c.PlayerId, out var e) ? e : -1).ToList();
        double[] completed;
        (_beta, _se, R2, Sigma, completed, _influence) = RidgeTobit(x, y, data.Select(c => c.PlayerId).ToList(), controls, censored, rawPenalty: rawPenalty, effects: effects, effectRidge: FitPlayerRidge, zPenalty: zPenalty);
        // Leave-one-out: a card's own price must not vouch for itself, or a cheap card would look less cheap.
        var residual = data.Select((c, i) => completed[i] - Dot(_beta, x[i])).ToList();
        var footballers = data.Select((c, i) => (c, i)).GroupBy(t => t.c.PlayerId).ToList();
        var repeated = footballers.Where(p => p.Count() >= 2).ToList();
        var tau2 = repeated.Count >= 10 ? repeated.Average(p => Math.Pow(p.Average(t => residual[t.i]), 2) - Sigma * Sigma / p.Count()) : 0;
        PlayerShrink = tau2 > 1e-4 ? Math.Clamp(Sigma * Sigma / tau2, 0.1, 10) : 10;
        foreach (var p in footballers)
            _footballer[p.Key] = p.Select(t => (t.c.SpId, t.c.Ovr1, residual[t.i])).ToList();
        if (pooled)
        {
            if (validate) ValidatePool(grade, data, memberships, traitHinge, linearTraits, usage);
        }
        else (ValidationError, ValidationLogScale) = validate ? Validate(group, grade, data, memberships, traitHinge, linearTraits) : (double.NaN, double.NaN);
    }

    /// <summary>The position's own reading of a pooled fit; a single-position model is its own view.</summary>
    public PriceModel? View(string group)
    {
        if (!_pooled) return group == Group ? this : null;
        var rows = _data.Where(c => _listedIn.GetValueOrDefault(c.SpId)?.Contains(group) == true).ToList();
        if (rows.Count < MinViewCards) return null;
        var view = (PriceModel)MemberwiseClone();
        view.Group = group;
        view._group = MarketGroups.Get(group);
        view.Cards = rows.Count;
        view.Players = rows.Select(c => c.PlayerId).Distinct().Count();
        var prices = rows.Select(c => c.PriceAt(Grade)).Order().ToList();
        view._medianPrice = prices[prices.Count / 2];
        var top = rows.Where(c => c.Ovr1 >= TraitTopOvr).Select(c => c.PriceAt(Grade)).Order().ToList();
        view._topMedianPrice = top.Count >= 5 ? top[top.Count / 2] : view._medianPrice;
        if (_validationOf.TryGetValue(group, out var v)) (view.ValidationError, view.ValidationLogScale) = v;
        return view;
    }

    /// <summary>
    /// One fit over the field cards of every position (the goalkeepers have other stats and stay on their own). A card
    /// listed by several groups is one row. Use <see cref="View"/> for a position's values and validation.
    /// </summary>
    /// <param name="usage">How each card is used in the stored official matches (<see cref="CardUsage"/>).</param>
    public static PriceModel? FitPool(int grade, IEnumerable<MarketCard> cards, IReadOnlyList<MarketMembership>? memberships = null, bool validate = true,
        int? traitHinge = TraitHinge, bool linearTraits = false, IReadOnlyDictionary<long, CardUse>? usage = null)
    {
        var rows = cards.Where(c => c.Group != "GK" && c.IsTraded && c.PriceAt(grade) > Grades.FloorPrice && !MarketGroups.IsPriceOutlier(c)).ToList();
        var data = Unique(rows);
        if (data.Count < 60 || data.Select(c => c.PlayerId).Distinct().Count() < 10) return null;
        var model = new PriceModel(MarketGroups.All[0].Key, grade, data, memberships, false, traitHinge, linearTraits, pooled: true, usage: usage);
        foreach (var c in rows)
        {
            if (!model._listedIn.TryGetValue(c.SpId, out var set)) model._listedIn[c.SpId] = set = [];
            set.Add(c.Group);
        }
        if (validate) model.ValidatePool(grade, data, memberships, traitHinge, linearTraits, usage);
        return model;
    }

    private static List<MarketCard> Unique(IEnumerable<MarketCard> rows) =>
        rows.GroupBy(c => c.SpId).Select(g => g.Count() == 1 ? g.First() : g.First() with { Tags = g.SelectMany(c => c.Tags).ToHashSet() }).ToList();

    private static readonly IReadOnlyDictionary<string, string> PositionFamily = new Dictionary<string, string>
    {
        ["ST"] = "ST", ["LS"] = "ST", ["RS"] = "ST", ["CF"] = "CF", ["LF"] = "CF", ["RF"] = "CF", ["LW"] = "W", ["RW"] = "W",
        ["LM"] = "SM", ["RM"] = "SM", ["CAM"] = "CAM", ["LAM"] = "CAM", ["RAM"] = "CAM", ["CM"] = "CM", ["LCM"] = "CM", ["RCM"] = "CM",
        ["CDM"] = "CDM", ["LDM"] = "CDM", ["RDM"] = "CDM", ["LB"] = "FB", ["RB"] = "FB", ["LWB"] = "FB", ["RWB"] = "FB",
        ["CB"] = "CB", ["LCB"] = "CB", ["RCB"] = "CB", ["SW"] = "CB", ["GK"] = "GK",
    };
    private static readonly IReadOnlyDictionary<string, string> LineOf = new Dictionary<string, string>
    {
        ["ST"] = "ATT", ["CF"] = "ATT", ["W"] = "ATT", ["SM"] = "ATT", ["CAM"] = "MID", ["CM"] = "MID", ["CDM"] = "MID", ["FB"] = "DEF", ["CB"] = "DEF",
    };

    /// <summary>The position the card plays best; the market prices it there. The group that listed it when positions are unknown.</summary>
    private static string FamilyFrom(MarketCard c)
    {
        var best = c.Positions.Count == 0 ? null : c.Positions.Aggregate((a, b) => b.Value > a.Value ? b : a).Key;
        return best is not null && PositionFamily.TryGetValue(best, out var family) ? family : c.Group;
    }

    private string FamilyOf(MarketCard c) => _familyOf.TryGetValue(c.SpId, out var family) ? family : _pooled ? FamilyFrom(c) : Group;
    private string LineOfCard(MarketCard c) => LineOf.GetValueOrDefault(FamilyOf(c), "MID");

    /// <summary>What a trait's name suggests it works with. Each pair is priced (ridged), none assumed; tools/market/mv12_report.py shows the values.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> TraitCompatibility = new Dictionary<string, string[]>
    {
        ["라인 브레이커"] = ["sprintspeed", "acceleration", "agility", "positioning", "finishing", "dribbling"],
        ["크로스 포쳐"] = ["headingaccuracy", "jumping", "strength", "height", "positioning", "finishing"],
        ["스피드스터"] = ["sprintspeed", "acceleration", "agility", "balance", "dribbling"],
        ["트릭스터"] = ["dribbling", "ballcontrol", "agility", "balance", "finishing", "curve"],
        ["아크로바틱 피니셔"] = ["volleys", "agility", "jumping", "finishing", "balance", "composure"],
        ["2개의 심장"] = ["stamina", "shortpassing", "vision", "interceptions", "standingtackle", "aggression"],
        ["체이서"] = ["sprintspeed", "acceleration", "interceptions", "standingtackle", "marking", "stamina"],
        ["파이터"] = ["strength", "aggression", "standingtackle", "marking", "jumping"],
        ["타이탄"] = ["strength", "height", "jumping", "headingaccuracy", "marking"],
        ["커맨더"] = ["vision", "shortpassing", "longpassing", "composure", "interceptions"],
        ["와일드 태클러"] = ["slidingtackle", "standingtackle", "aggression", "sprintspeed", "strength"],
        ["블로커"] = ["marking", "strength", "jumping", "interceptions", "height"],
    };

    private void BuildExtras(IReadOnlyList<MarketCard> data, IReadOnlyList<MarketMembership> memberships, bool pooled)
    {
        void Z(string name, Func<MarketCard, double> f, double z, bool premium = false, string? reason = null) => _extras.Add(new(name, f, ZRidge: z, Premium: premium, Reason: reason));
        void Raw(string name, Func<MarketCard, double> f, double raw, bool premium = false, string? reason = null) => _extras.Add(new(name, f, RawRidge: raw, Premium: premium, Reason: reason));
        // How popular the card's team colour is among rankers, and how deep the card sits in it at its position.
        var colours = memberships.Where(m => m.Kind == FactorKind.TeamColor && m.Share > 0).ToList();
        if (colours.Count > 0)
        {
            double Best(MarketCard c) => colours.Where(m => m.Members.Contains(c.SpId)).Select(m => m.Share).DefaultIfEmpty(0).Max();
            var depth = new Dictionary<(string, string), int[]>();
            foreach (var m in colours)
                foreach (var part in data.Where(c => m.Members.Contains(c.SpId)).GroupBy(c => _familyOf[c.SpId]))
                    depth[(m.Key, part.Key)] = part.Select(c => c.Ovr1).ToArray();
            double Rank(MarketCard c)
            {
                var family = FamilyOf(c);
                var best = 50;
                foreach (var m in colours)
                    if (m.Share > 0.005 && m.Members.Contains(c.SpId) && depth.TryGetValue((m.Key, family), out var ovrs))
                        best = Math.Min(best, ovrs.Count(o => o > c.Ovr1));
                return best;
            }
            Z("colour_share", Best, 30, reason: ReasonColor);
            Z("colour_share2", c => Math.Pow(Best(c), 2), 30, reason: ReasonColor);
            Z("colour_count", c => colours.Count(m => m.Members.Contains(c.SpId)), 30, reason: ReasonColor);
            Z("colour_rank1", c => Rank(c) == 0 ? 1 : 0, 30, reason: ReasonColor);
            Z("colour_rank3", c => Rank(c) < 3 ? 1 : 0, 30, reason: ReasonColor);
            Z("colour_rank_log", c => Math.Log(1 + Rank(c)), 30, reason: ReasonColor);
        }
        if (!pooled) return;
        var mean = data.Average(c => (double)c.Ovr1);
        var pay = data.Average(c => (double)c.Pay);
        if (_usage is { Count: > 0 } usage)
        {
            // How often it is fielded: demand the specs do not show. Same footballer's other cards count too (MV-14).
            var perPlayer = new Dictionary<int, int>();
            foreach (var (spId, use) in usage)
            {
                var player = (int)(spId % 1_000_000);
                perPlayer[player] = perPlayer.GetValueOrDefault(player) + use.Starts;
            }
            Z("use_card", c => LogStarts(c), 30, reason: ReasonUsage);
            Z("use_any", c => usage.ContainsKey(c.SpId) ? 1 : 0, 30, reason: ReasonUsage);
            Z("use_player", c => Math.Log(1 + perPlayer.GetValueOrDefault(c.PlayerId)), 30, reason: ReasonUsage);
            // At what grade: whether managers actually field it at +11 says what +11 is worth to them (MV-15).
            Z("use_11", c => LogAtEleven(c), 30, reason: ReasonUsage);
            Z("use_11_any", c => LogAtEleven(c) > 0 ? 1 : 0, 30, reason: ReasonUsage);
        }
        var families = data.Select(c => _familyOf[c.SpId]).Distinct().Order().ToArray();
        foreach (var f in families.Skip(1)) Z($"family:{f}", c => FamilyOf(c) == f ? 1 : 0, 1e-3);
        foreach (var f in families)
        {
            Z($"ovr×{f}", c => FamilyOf(c) == f ? c.Ovr1 - mean : 0, 3);
            Z($"pay×{f}", c => FamilyOf(c) == f ? c.Pay - pay : 0, 3, reason: ReasonPay);
            foreach (var s in _stats)
                Z($"stat:{s}×{f}", c => FamilyOf(c) == f && c.Stats.TryGetValue(s, out var v) ? v - c.Ovr1 : 0, 300, premium: true, reason: ReasonStat + s);
        }
        foreach (var t in _traitSlopes)
        {
            var carriers = data.Where(c => c.Tags.Contains(t)).ToList();
            foreach (var line in new[] { "ATT", "MID", "DEF" })
                if (carriers.Count(c => LineOf.GetValueOrDefault(_familyOf[c.SpId], "MID") == line) >= 8)
                {
                    Raw($"{t}×{line}", c => c.Tags.Contains(t) && LineOfCard(c) == line ? 1 : 0, 2, premium: true, reason: t);
                    Raw($"hinge:{t}×{line}", c => c.Tags.Contains(t) && LineOfCard(c) == line ? TraitMultiplier(c.Ovr1) : 0, 5, premium: true, reason: t);
                }
            foreach (var f in families)
                if (carriers.Count(c => _familyOf[c.SpId] == f) >= 12)
                {
                    Raw($"{t}×{f}", c => c.Tags.Contains(t) && FamilyOf(c) == f ? 1 : 0, 4, premium: true, reason: t);
                    Raw($"hinge:{t}×{f}", c => c.Tags.Contains(t) && FamilyOf(c) == f ? TraitMultiplier(c.Ovr1) : 0, 10, premium: true, reason: t);
                }
            // A trait's compatibility with the card's stats, around the trait cards' usual level.
            if (carriers.Count >= 15 && TraitCompatibility.TryGetValue(t[6..], out var partners))
                foreach (var s in partners)
                {
                    double Value(MarketCard c) => c.Stats.TryGetValue(s, out var v) ? v - (s == "height" ? 0 : c.Ovr1) : 0;
                    var center = carriers.Average(Value);
                    Z($"{t}×{s}", c => c.Tags.Contains(t) ? Value(c) - center : 0, 100, premium: true, reason: t);
                }
        }
        foreach (var season in data.GroupBy(c => c.Season).Where(x => x.Count() >= 25).Select(x => x.Key))
        {
            Z($"season×ovr:{season}", c => c.Season == season ? c.Ovr1 - mean : 0, 30);
            foreach (var line in new[] { "ATT", "MID", "DEF" })
                Z($"season:{season}×{line}", c => c.Season == season && LineOfCard(c) == line ? 1 : 0, 30);
        }
    }

    /// <returns>Null when there are too few traded cards to fit.</returns>
    /// <remarks>Name-priced cards (<see cref="MarketGroups.PriceOutliers"/>) are left out of the fit; they are still priced by it.</remarks>
    /// <param name="memberships">Separate affiliation and feature team-colour memberships to price.</param>
    public static PriceModel? Fit(string group, int grade, IEnumerable<MarketCard> cards, IReadOnlyList<MarketMembership>? memberships = null, bool validate = true,
        int? traitHinge = TraitHinge, bool linearTraits = false)
    {
        var data = cards.Where(c => c.Group == group && c.IsTraded && c.PriceAt(grade) > Grades.FloorPrice && !MarketGroups.IsPriceOutlier(c)).ToList();
        return data.Count < 60 || data.Select(c => c.PlayerId).Distinct().Count() < 10 ? null : new PriceModel(group, grade, data, memberships, validate, traitHinge, linearTraits);
    }

    private string Label(string tag) => _memberships.TryGetValue(tag, out var membership) ? membership.Name : MarketGroups.TagLabel(tag);

    /// <summary>Why a tag is not priced (MV-07), or null when it is.</summary>
    private string? ExclusionOf(string tag) => tag switch
    {
        _ when tag.StartsWith("body:") => "가격 모델에서 뺌 (MV-07: 체형은 어느 포지션에서도 가격 차이 없음)",
        _ when tag.StartsWith("skill:") => "가격 모델에서 뺌 (MV-07: 개인기는 카드마다 들쭉날쭉)",
        _ when _memberships.TryGetValue(tag, out var m) && m.Kind == FactorKind.FeatureTeamColor => "가격 모델에서 뺌 (MV-07: 특성 팀컬러는 일관된 가격 차이 없음)",
        _ => null,
    };

    private bool Has(MarketCard c, string tag) => _memberships.TryGetValue(tag, out var membership)
        ? membership.Members.Contains(c.SpId) : c.Tags.Contains(tag);

    private FactorKind KindOf(string tag) => tag.StartsWith("trait:") ? FactorKind.Trait : tag.StartsWith("skill:") ? FactorKind.Skill
        : _memberships.TryGetValue(tag, out var membership) ? membership.Kind : FactorKind.Body;

    private PhysicalFeature[] PhysicalFeatures(IReadOnlyList<MarketCard> data, MarketGroup group, string? family = null)
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
                    $"가격 모델에서 뺌 (MV-07: 이 포지션은 {title}에 따른 일관된 가격 차이 없음)", family));
                return;
            }
            var counts = buckets.Select(b => (Bucket: b, Cards: data.Count(c => c.Stats.TryGetValue(stat, out var v) && v >= b.Min && v <= b.Max))).ToList();
            var reference = counts.MaxBy(x => x.Cards).Bucket;
            var isCore = group.CoreStats.Any(s => s.Stat == stat);
            foreach (var (bucket, cards) in counts.Where(x => x.Cards > 0 && x.Bucket.Key != reference.Key))
            {
                var p = new PhysicalFeature($"{stat}:{bucket.Key}", $"{title} {bucket.Label} (기준 {reference.Label})", stat, kind,
                    bucket.Min, bucket.Max, cards, isCore) { Family = family };
                var players = data.Where(p.Has).Select(c => c.PlayerId).Distinct().Count();
                var controls = data.Where(c => c.Stats.TryGetValue(stat, out var v) && v >= reference.Min && v <= reference.Max)
                    .Select(c => c.PlayerId).Distinct().Count();
                if (players >= MinPlayers && controls >= MinPlayers) result.Add(p);
                else _unpriced.Add(new(p.Key, p.Name, kind, cards, players, isCore, Family: family));
            }
        }
    }

    /// <summary>The model's price for the card, before the enhancement floor.</summary>
    public double Predict(MarketCard c)
    {
        var linear = Dot(_beta, Features(c)) + PlayerEffect(c);
        return Math.Exp(_treeParts is null ? linear : Blend(linear, TreeLog(c), c));
    }

    /// <summary>MV-14: the linear fit and the trees, each no lower than the card's floor, as the blend was tested.</summary>
    private double Blend(double linear, double tree, MarketCard c)
    {
        var floor = Math.Log(Math.Max(FloorAt(c, Grade) ?? 1, 1));
        return (1 - TreeWeight) * Math.Max(linear, floor) + TreeWeight * Math.Max(tree, floor);
    }

    private double LogStarts(MarketCard c) => Math.Log(1 + (_usage?.GetValueOrDefault(c.SpId)?.Starts ?? 0));
    private double LogAtEleven(MarketCard c) => Math.Log(1 + (_usage?.GetValueOrDefault(c.SpId)?.AtEleven ?? 0));

    private double TreeLog(MarketCard c)
    {
        if (_treeLog.TryGetValue(c.SpId, out var known)) return known;
        var parts = _treeParts!;
        return parts.Average(p => p.Forest!.Predict(TreeRow(c, p, leaveOut: false)));
    }

    /// <summary>Plain columns: the trees find the interactions the linear fit has to be given. A footballer's and a season's average come from the part's own cards.</summary>
    private double[] TreeRow(MarketCard c, PriceTrees.Part part, bool leaveOut)
    {
        if (!_treeRows.TryGetValue(c.SpId, out var plain))
        {
            var row = new List<double>
            {
                c.Ovr1, c.Pay, c.WeakFoot, c.Stats.TryGetValue("height", out var h) ? h : double.NaN, c.Stats.TryGetValue("weight", out var w) ? w : double.NaN,
                c.Positions.Count, c.Tags.Count(t => t.StartsWith("trait:")), _treeColours.Count(m => m.Members.Contains(c.SpId)),
                _treeColours.Where(m => m.Members.Contains(c.SpId)).Select(m => m.Share).DefaultIfEmpty(0).Max(),
                LogStarts(c), LogAtEleven(c),
            };
            foreach (var s in _stats) row.Add(c.Stats.TryGetValue(s, out var v) ? v - c.Ovr1 : double.NaN);
            var family = FamilyOf(c);
            foreach (var f in _treeFamilies) row.Add(family == f ? 1 : 0);
            foreach (var t in _treeTraits) row.Add(c.Tags.Contains(t) ? 1 : 0);
            plain = [.. row];
            if (_familyOf.ContainsKey(c.SpId)) _treeRows[c.SpId] = plain;
        }
        var own = Math.Log(Math.Max(c.PriceAt(Grade), 1)) - TreeOvrSlope * c.Ovr1;
        var (playerSum, playerCount) = part.Players.GetValueOrDefault(c.PlayerId);
        var (seasonSum, seasonCount) = part.Seasons.GetValueOrDefault(c.Season);
        if (leaveOut) { playerSum -= own; playerCount--; seasonSum -= own; seasonCount--; }
        return [.. plain, playerCount > 0 ? playerSum / playerCount : double.NaN, playerCount, (seasonSum + 5 * part.Mean) / (seasonCount + 5)];
    }

    private PriceTrees.Part FitTreePart(IReadOnlyList<MarketCard> train)
    {
        double Own(MarketCard c) => Math.Log(c.PriceAt(Grade)) - TreeOvrSlope * c.Ovr1;
        var part = new PriceTrees.Part(
            train.GroupBy(c => c.PlayerId).ToDictionary(g => g.Key, g => (g.Sum(Own), g.Count())),
            train.GroupBy(c => c.Season).ToDictionary(g => g.Key, g => (g.Sum(Own), g.Count())),
            train.Average(Own));
        part.Forest = PriceTrees.Fit(train.Select(c => TreeRow(c, part, leaveOut: true)).ToList(), train.Select(c => Math.Log(c.PriceAt(Grade))).ToList());
        return part;
    }

    private void PrepareTrees()
    {
        _treeFamilies = _data.Select(c => _familyOf[c.SpId]).Distinct().Order().ToArray();
        _treeTraits = _data.SelectMany(c => c.Tags.Where(t => t.StartsWith("trait:"))).GroupBy(t => t).Where(g => g.Count() >= 20).Select(g => g.Key).Order().ToArray();
        _treeColours = _memberships.Values.Where(m => m.Kind == FactorKind.TeamColor && m.Share > 0).ToArray();
    }

    /// <summary>
    /// Trees for the shown prices: five parts, each blind to one fold, and a card takes the part that did not see it. One
    /// forest over everything would know each card's own price, and every card would look about right.
    /// </summary>
    private void FitCrossFittedTrees()
    {
        if (_data.Count < MinCardsForTrees) return;
        PrepareTrees();
        var parts = Enumerable.Range(0, 5).Select(fold => FitTreePart(_data.Where(c => Fold(c.SpId) != fold).ToList())).ToArray();
        foreach (var c in _data)
        {
            var part = parts[Fold(c.SpId)];
            _treeLog[c.SpId] = part.Forest!.Predict(TreeRow(c, part, leaveOut: false));
        }
        _treeParts = parts;
    }

    /// <summary>For audits: every card this model is asked about is unseen, so one forest over the training cards will do.</summary>
    private void FitPlainTrees()
    {
        if (_data.Count < MinCardsForTrees) return;
        PrepareTrees();
        _treeParts = [FitTreePart(_data)];
    }

    /// <summary>
    /// How the market prices this footballer beyond his cards' specs (log scale), from his other cards only; 0 when the
    /// model has seen no other card of him. [추정]
    /// </summary>
    public double PlayerEffect(MarketCard c)
    {
        if (!_footballer.TryGetValue(c.PlayerId, out var cards)) return 0;
        double sum = 0, weight = 0;
        foreach (var (spId, ovr, residual) in cards)
        {
            if (spId == c.SpId) continue;
            // MV-12: a card close in OVR says more about this one than a much older, weaker card of the same footballer.
            var w = Math.Exp(-Math.Abs(ovr - c.Ovr1) / NearCardOvr);
            sum += w * residual;
            weight += w;
        }
        return weight > 0 ? sum / (weight + PlayerShrink) : 0;
    }

    /// <summary>
    /// Takes the prediction apart. OVR, season and position make the base; each other factor is measured against the
    /// average card of the market, so the base times every reason is exactly <see cref="Predict"/>. A market reading [추정].
    /// </summary>
    public PriceExplanation Explain(MarketCard c)
    {
        var x = Features(c);
        var tagStart = 6 + _stats.Length + _physical.Length;
        var slopeStart = tagStart + _tags.Length;
        var extraStart = slopeStart + _traitSlopes.Length;
        var sums = new Dictionary<string, double>();
        var stats = new Dictionary<string, double>();
        double basis = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var effect = _beta[i] * (x[i] - _meanFeature[i]);
            string? key = null;
            if (i is 3 or 4) key = "약발";
            else if (i == 5) key = "급여";
            else if (i >= 6 && i < 6 + _stats.Length) { key = "능력치 배분"; stats[_stats[i - 6]] = stats.GetValueOrDefault(_stats[i - 6]) + effect; }
            else if (i >= 6 + _stats.Length && i < tagStart) key = "키·몸무게";
            else if (i >= tagStart && i < slopeStart)
            {
                var tag = _tags[i - tagStart];
                key = tag.StartsWith("trait:") ? "특성: " + tag[6..] : "팀컬러 인기";
            }
            else if (i >= slopeStart && i < extraStart) key = "특성: " + _traitSlopes[i - slopeStart][6..];
            else if (i >= extraStart && i < extraStart + _extras.Count && _extras[i - extraStart].Reason is { } reason)
            {
                if (reason == ReasonColor) key = "팀컬러 인기";
                else if (reason == ReasonPay) key = "급여";
                else if (reason == ReasonUsage) key = "공식 경기 사용량";
                else if (reason.StartsWith(ReasonStat)) { key = "능력치 배분"; var s = reason[ReasonStat.Length..]; stats[s] = stats.GetValueOrDefault(s) + effect; }
                else key = "특성: " + reason[6..];
            }
            if (key is null) basis += _beta[i] * x[i];
            else
            {
                basis += _beta[i] * _meanFeature[i];
                sums[key] = sums.GetValueOrDefault(key) + effect;
            }
        }
        // Traits a card does not have only show up as what the average card carries: not a reason for this card.
        var own = c.Tags.Where(t => t.StartsWith("trait:")).Select(t => "특성: " + t[6..]).ToHashSet();
        var absent = sums.Where(kv => kv.Key.StartsWith("특성: ") && !own.Contains(kv.Key)).Sum(kv => kv.Value);
        foreach (var key in sums.Keys.Where(k => k.StartsWith("특성: ") && !own.Contains(k)).ToList()) sums.Remove(key);
        if (Math.Abs(absent) > 1e-12) sums["다른 특성이 없는 몫"] = absent;
        string? statDetail = stats.Count == 0 ? null : string.Join(", ", stats.OrderByDescending(kv => Math.Abs(kv.Value)).Take(3)
            .Select(kv => $"{MarketGroups.StatNames.GetValueOrDefault(kv.Key, kv.Key)} {Pct(kv.Value):+0;-0}%"));
        var reasons = sums.Select(kv => new PriceReason(kv.Key, kv.Value, kv.Key == "능력치 배분" ? statDetail : null)).ToList();
        var player = PlayerEffect(c);
        if (Math.Abs(player) > 1e-9) reasons.Add(new PriceReason("이 선수의 다른 카드 시세", player, "같은 선수 카드들이 스펙보다 비싸거나 싸게 팔리는 정도"));
        var blended = Math.Log(Predict(c)) - (Dot(_beta, x) + player);
        if (Math.Abs(blended) > 1e-9) reasons.Add(new PriceReason("능력치·특성 조합 보정", blended, "위 항목들을 더하는 것으로는 못 보는 조합을 별도 모델로 본 몫"));
        return new PriceExplanation(Math.Exp(basis), reasons.OrderByDescending(r => Math.Abs(r.LogEffect)).ToList(), Predict(c), Expected(c));
    }

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
        var end = tagStart + _tags.Length + _traitSlopes.Length;
        double premium = 0;
        for (var i = firstPremium; i < end; i++)
        {
            if (i == 5) continue;
            if (i >= tagStart && i < tagStart + _tags.Length && KindOf(_tags[i - tagStart]) is FactorKind.TeamColor or FactorKind.FeatureTeamColor) continue;
            premium += _beta[i] * x[i];
        }
        var extraStart = end;
        for (var i = 0; i < _extras.Count; i++)
            if (_extras[i].Premium) premium += _beta[extraStart + i] * x[extraStart + i];
        var slope = _beta[1] + 2 * _beta[2] * (c.Ovr1 - _ovrMean)
            + (_extraAt.TryGetValue($"ovr×{FamilyOf(c)}", out var at) ? _beta[at] : 0);
        return slope > 0.01 ? Math.Clamp(premium / slope, -6, 10) : 0;
    }

    public IReadOnlyList<PriceEffect> Effects() =>
        _names.Select((n, i) => (n, i))
            .Where(t => t.i > 0 && t.n != "OVR²" && !t.n.StartsWith("season:"))
            .Select(t => new PriceEffect(t.n, Pct(_beta[t.i]), Pct(_beta[t.i] - CriticalOf(t.i) * _se[t.i]), Pct(_beta[t.i] + CriticalOf(t.i) * _se[t.i]),
                _se[t.i] > 0 ? _beta[t.i] / _se[t.i] : 0, _counts.TryGetValue(t.n, out var n) ? n : null))
            .ToList();

    /// <summary>
    /// Every factor the model prices, in %, OVR points and BP on the group's median card. Stats are "+1 with the same
    /// OVR" (so a stat that only inflates the OVR shows as negative), traits and body types against cards without them.
    /// </summary>
    public IReadOnlyList<PriceFactor> Factors()
    {
        if (_pooled) return PoolFactors();
        var slope = _beta[1];
        var core = _group.CoreStats.Select(s => s.Stat).ToHashSet();
        var result = new List<PriceFactor>();
        void Add(int i, string key, FactorKind kind, bool isCore, bool inflating = false)
        {
            var name = _names[i];
            var critical = CriticalOf(i);
            var slopeAt = Array.IndexOf(_traitSlopes, key);
            // A trait: its premium on a top card, the base plus the hinge's growth, with the pair's joint error.
            var weights = new Dictionary<int, double> { [i] = 1 };
            if (slopeAt >= 0) weights[6 + _stats.Length + _physical.Length + _tags.Length + slopeAt] = TraitMultiplier(TraitTopOvr);
            var b = weights.Sum(w => w.Value * _beta[w.Key]);
            var se = slopeAt >= 0 ? StandardError(weights) : _se[i];
            result.Add(new PriceFactor(key, name, kind, Pct(b), Pct(b - critical * se), Pct(b + critical * se),
                slope > 0.01 ? b / slope : 0, (long)((slopeAt >= 0 ? _topMedianPrice : _medianPrice) * (Math.Exp(b) - 1)),
                _counts.TryGetValue(name, out var n) ? n : null, isCore, inflating)
                { Players = _playerCounts.GetValueOrDefault(name, Players), Critical = critical,
                  Comparisons = _comparisons.TryGetValue(name, out var compared) ? compared : null,
                  PerOvr = slopeAt >= 0 ? _beta[6 + _stats.Length + _physical.Length + _tags.Length + slopeAt] : double.NaN,
                  PercentBelowHinge = slopeAt >= 0 ? Pct(_beta[i]) : double.NaN });
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

    /// <summary>The position's values: the shared coefficient plus that position's own part, with their joint error.</summary>
    private IReadOnlyList<PriceFactor> PoolFactors()
    {
        var family = Group;
        var rows = _data.Where(c => _listedIn.GetValueOrDefault(c.SpId)?.Contains(family) == true).ToList();
        var slope = _beta[1] + Part($"ovr×{family}");
        double Part(string name) => _extraAt.TryGetValue(name, out var at) ? _beta[at] : 0;
        var core = _group.CoreStats.Select(s => s.Stat).ToHashSet();
        var result = new List<PriceFactor>();
        void Add(string key, string name, FactorKind kind, bool isCore, bool inflating, Dictionary<int, double> w, int? cards, int? players, double critical,
            bool top = false, int? comparisons = null, double perOvr = double.NaN, double below = double.NaN)
        {
            var b = w.Sum(x => x.Value * _beta[x.Key]);
            var se = StandardError(w);
            result.Add(new PriceFactor(key, name, kind, Pct(b), Pct(b - critical * se), Pct(b + critical * se), slope > 0.01 ? b / slope : 0,
                (long)((top ? _topMedianPrice : _medianPrice) * (Math.Exp(b) - 1)), cards, isCore, inflating)
                { Players = players, Critical = critical, Comparisons = comparisons, PerOvr = perOvr, PercentBelowHinge = below });
        }
        Dictionary<int, double> Sum(int at, params string[] parts)
        {
            var w = new Dictionary<int, double> { [at] = 1 };
            foreach (var p in parts) if (_extraAt.TryGetValue(p, out var i)) w[i] = 1;
            return w;
        }
        Add("foot:5", "양발 (약발 5)", FactorKind.Foot, true, false, Sum(3), rows.Count(c => c.WeakFoot >= 5), rows.Where(c => c.WeakFoot >= 5).Select(c => c.PlayerId).Distinct().Count(), _critical);
        Add("foot:4", "약발 4", FactorKind.Foot, false, false, Sum(4), rows.Count(c => c.WeakFoot == 4), rows.Where(c => c.WeakFoot == 4).Select(c => c.PlayerId).Distinct().Count(), _critical);
        Add("pay", "급여 +1", FactorKind.Salary, false, false, Sum(5, $"pay×{family}"), null, Players, _critical);
        for (var i = 0; i < _stats.Length; i++)
            if (_group.AllStats.Contains(_stats[i]))
                Add(_stats[i], _names[6 + i], FactorKind.Stat, core.Contains(_stats[i]), _group.InflatingStats.Contains(_stats[i]), Sum(6 + i, $"stat:{_stats[i]}×{family}"), null, Players, _critical);
        for (var i = 0; i < _physical.Length; i++)
            if (_physical[i].Family == family)
            {
                var p = _physical[i];
                var own = rows.Where(p.Has).ToList();
                Add(p.Key, p.Name, p.Kind, p.IsCore, false, Sum(6 + _stats.Length + i), own.Count, own.Select(c => c.PlayerId).Distinct().Count(), _critical);
            }
        var tagStart = 6 + _stats.Length + _physical.Length;
        var line = LineOf.GetValueOrDefault(family, "MID");
        var slopeStart = tagStart + _tags.Length;
        for (var i = 0; i < _tags.Length; i++)
        {
            var tag = _tags[i];
            var kind = KindOf(tag);
            var holders = rows.Where(c => Has(c, tag)).ToList();
            var players = holders.Select(c => c.PlayerId).Distinct().Count();
            if (kind != FactorKind.Trait)
            {
                Add(tag, _names[tagStart + i], kind, false, false, Sum(tagStart + i), holders.Count, players, _critical);
                continue;
            }
            var isCore = _group.KeyTraits.Contains(tag[6..]);
            if (players < MinPlayers)
            {
                result.Add(new PriceFactor(tag, Label(tag), kind, double.NaN, double.NaN, double.NaN, 0, 0, holders.Count, isCore, false) { Players = players });
                continue;
            }
            // The premium on a top card: common + the line's + the position's parts, base and hinge together.
            var growth = TraitMultiplier(TraitTopOvr);
            var w = Sum(tagStart + i, $"{tag}×{line}", $"{tag}×{family}");
            var below = w.Sum(x => x.Value * _beta[x.Key]);
            var slopeAt = Array.IndexOf(_traitSlopes, tag);
            double per = 0;
            if (slopeAt >= 0)
            {
                var h = Sum(slopeStart + slopeAt, $"hinge:{tag}×{line}", $"hinge:{tag}×{family}");
                per = h.Sum(x => x.Value * _beta[x.Key]);
                foreach (var (at, _) in h) w[at] = growth;
            }
            var comparisons = rows.GroupBy(c => c.PlayerId).Count(p => p.Any(c => Has(c, tag)) && p.Any(c => !Has(c, tag)));
            Add(tag, Label(tag), kind, isCore, false, w, holders.Count, players, Math.Max(TraitCritical, _critical), top: slopeAt >= 0, comparisons: comparisons,
                perOvr: slopeAt >= 0 ? per : double.NaN, below: slopeAt >= 0 ? Pct(below) : double.NaN);
        }
        foreach (var f in _unpriced.Where(f => f.Family is null || f.Family == family))
        {
            var holders = rows.Where(c => Has(c, f.Key)).ToList();
            var cards = f.Key.EndsWith(":excluded") ? rows.Count : holders.Count;
            result.Add(new PriceFactor(f.Key, f.Name, f.Kind, double.NaN, double.NaN, double.NaN, 0, 0, cards, f.Kind == FactorKind.Trait && _group.KeyTraits.Contains(f.Key.Replace("trait:", "")), false)
                { Players = f.Key.EndsWith(":excluded") ? Players : holders.Select(c => c.PlayerId).Distinct().Count(), Excluded = f.Excluded });
        }
        return result;
    }

    private double StandardError(IReadOnlyDictionary<int, double> weights)
    {
        double variance = 0;
        foreach (var row in _influence)
        {
            var s = weights.Sum(w => w.Value * row[w.Key]);
            variance += s * s;
        }
        return Math.Sqrt(variance);
    }

    private double CriticalOf(int i)
    {
        var tagStart = 6 + _stats.Length + _physical.Length;
        var isTrait = i >= tagStart && i < tagStart + _tags.Length && _tags[i - tagStart].StartsWith("trait:")
            || i >= tagStart + _tags.Length && i < tagStart + _tags.Length + _traitSlopes.Length;
        return isTrait ? Math.Max(TraitCritical, _critical) : _critical;
    }

    /// <summary>Typical (median) price of a traded card of the group at the model's grade.</summary>
    public long MedianPrice => _medianPrice;

    private double[] Features(MarketCard c)
    {
        var d = c.Ovr1 - _ovrMean;
        var x = new List<double>(_names.Length) { 1, d, d * d, c.WeakFoot >= 5 ? 1 : 0, c.WeakFoot == 4 ? 1 : 0, c.Pay };
        foreach (var s in _stats)
            x.Add(c.Stats.TryGetValue(s, out var v) ? v - c.Ovr1 : 0);
        foreach (var physical in _physical) x.Add((physical.Family is null || physical.Family == FamilyOf(c)) && physical.Has(c) ? 1 : 0);
        foreach (var t in _tags) x.Add(Has(c, t) ? 1 : 0);
        foreach (var t in _traitSlopes) x.Add(Has(c, t) ? TraitMultiplier(c.Ovr1) : 0);
        foreach (var e in _extras) x.Add(e.Value(c));
        foreach (var s in _seasons) x.Add(c.Season == s ? 1 : 0);
        x.Add(c.Season != _baseSeason && !_seasons.Contains(c.Season) ? 1 : 0);
        return [.. x];
    }

    private static double Pct(double b) => (Math.Exp(b) - 1) * 100;
    private double TraitMultiplier(int ovr) => _linearTraits ? ovr - 118 : Math.Max(0, ovr - (_traitHinge ?? TraitHinge));
    private static double Dot(double[] a, double[] b) { double s = 0; for (var i = 0; i < a.Length; i++) s += a[i] * b[i]; return s; }

    /// <summary>
    /// Reproducible five-fold card audit. Each fold selects its own features and player effects from the other four;
    /// neither the held-out price nor its censored latent value enters training. The UI's cheaper single holdout is
    /// unchanged. Use the returned rows to compare the same OVR population, traits and non-floor cards (MV-10).
    /// </summary>
    public static IReadOnlyList<PriceValidationRow> CrossValidate(string group, int grade, IEnumerable<MarketCard> cards,
        IReadOnlyList<MarketMembership>? memberships = null, int? traitHinge = TraitHinge, bool linearTraits = false)
    {
        var data = cards.Where(c => c.Group == group && c.IsTraded && c.PriceAt(grade) > Grades.FloorPrice && !MarketGroups.IsPriceOutlier(c)).ToList();
        var result = new List<PriceValidationRow>();
        for (var fold = 0; fold < 5; fold++)
        {
            var train = data.Where(c => Fold(c.SpId) != fold).ToList();
            if (train.Count < 50 || train.Select(c => c.PlayerId).Distinct().Count() < 10) continue;
            var model = new PriceModel(group, grade, train, memberships, validate: false, traitHinge: traitHinge, linearTraits: linearTraits);
            result.AddRange(data.Where(c => Fold(c.SpId) == fold).Select(c => new PriceValidationRow(c, grade, fold, model.Expected(c))));
        }
        return result;
    }

    /// <summary>
    /// <see cref="CrossValidate"/> for the pooled fit: one row per listed card (per group), one fit per fold over all positions.
    /// </summary>
    public static IReadOnlyList<PriceValidationRow> CrossValidatePool(int grade, IEnumerable<MarketCard> cards,
        IReadOnlyList<MarketMembership>? memberships = null, int? traitHinge = TraitHinge, bool linearTraits = false,
        IReadOnlyDictionary<long, CardUse>? usage = null, bool trees = true)
    {
        var rows = cards.Where(c => c.Group != "GK" && c.IsTraded && c.PriceAt(grade) > Grades.FloorPrice && !MarketGroups.IsPriceOutlier(c)).ToList();
        var result = new List<PriceValidationRow>();
        for (var fold = 0; fold < 5; fold++)
        {
            var train = rows.Where(c => Fold(c.SpId) != fold).ToList();
            if (FitPool(grade, train, memberships, validate: false, traitHinge, linearTraits, usage) is not { } model) continue;
            if (trees) model.FitPlainTrees();
            result.AddRange(rows.Where(c => Fold(c.SpId) == fold).Select(c => new PriceValidationRow(c, grade, fold, model.Expected(c))));
        }
        return result;
    }

    /// <summary>
    /// Held-out error per position. All five folds on the playable market: one fold swung by ±10%p for a position (MV-13);
    /// one fold on the whole market, where five would take half a minute.
    /// </summary>
    private void ValidatePool(int grade, IReadOnlyList<MarketCard> data, IReadOnlyList<MarketMembership>? memberships, int? traitHinge, bool linearTraits, IReadOnlyDictionary<long, CardUse>? usage)
    {
        FitCrossFittedTrees();
        var folds = data.Count <= MaxCardsForAllFolds ? 5 : 1;
        var holdout = data.Where(c => Fold(c.SpId) < folds).ToList();
        if (holdout.Count < 10) return;
        var residual = new Dictionary<long, double>();
        var linearOf = new Dictionary<long, double>();
        for (var fold = 0; fold < folds; fold++)
        {
            if (FitPool(grade, data.Where(c => Fold(c.SpId) != fold), memberships, validate: false, traitHinge, linearTraits, usage) is not { } model) continue;
            foreach (var c in data.Where(c => Fold(c.SpId) == fold))
            {
                // The fold's model is linear only; the trees were fitted without this fold as well.
                var linear = Math.Log(model.Predict(c));
                linearOf[c.SpId] = linear;
                var expected = _treeParts is null ? model.Expected(c) : Math.Max((long)Math.Exp(Blend(linear, _treeLog[c.SpId], c)), FloorAt(c, grade) ?? 0);
                residual[c.SpId] = Math.Log(c.PriceAt(grade)) - Math.Log(Math.Max(expected, 1));
            }
        }
        if (residual.Count < 10) return;
        holdout = holdout.Where(c => residual.ContainsKey(c.SpId)).ToList();
        (double, double) Measure(IEnumerable<double> values)
        {
            var list = values.ToList();
            var median = Median(list);
            return (Median(list.Select(e => Math.Abs(Math.Exp(-e) - 1))), Math.Max(Math.Max(1.4826 * Median(list.Select(e => Math.Abs(e - median))), Math.Abs(median)), 0.05));
        }
        (ValidationError, ValidationLogScale) = Measure(residual.Values);
        foreach (var group in MarketGroups.All.Where(g => g.Key != "GK").Select(g => g.Key))
        {
            var own = holdout.Where(c => _listedIn.GetValueOrDefault(c.SpId)?.Contains(group) == true).Select(c => residual[c.SpId]).ToList();
            _validationOf[group] = own.Count >= 10 ? Measure(own) : (ValidationError, ValidationLogScale);
        }
        if (_treeParts is not null) FitReliability(data, residual, linearOf);
    }

    /// <summary>How sure the price of a kind of card is: a regression of the size of held-out errors, and what those errors were in each quarter.</summary>
    private sealed record Reliability(double[] Beta, double[] Cuts, (double Low, double High, double Error, double Scale)[] Tiers, Dictionary<string, double> SeasonError, double AnyError);

    private double[] ReliabilityRow(MarketCard c, double blended, double linear, double tree, IReadOnlyDictionary<string, double> seasonError, double anyError)
    {
        var others = _footballer.TryGetValue(c.PlayerId, out var cards) ? cards.Count(t => t.SpId != c.SpId) : 0;
        var floor = Math.Log(Math.Max(FloorAt(c, Grade) ?? 1, 1));
        return [1, Math.Log(1 + others), others == 0 ? 1 : 0, LogStarts(c), c.Ovr1 - _ovrMean, blended - 18,
            Math.Max(0, Math.Log(Bp.Eok) - blended), blended - floor, c.Tags.Count(t => t.StartsWith("trait:")), Math.Abs(linear - tree), seasonError.GetValueOrDefault(c.Season, anyError)];
    }

    private void FitReliability(IReadOnlyList<MarketCard> data, IReadOnlyDictionary<long, double> residual, IReadOnlyDictionary<long, double> linearOf)
    {
        var known = data.Where(c => residual.ContainsKey(c.SpId) && !AtFloor(c, Grade)).ToList();
        if (known.Count < MinCardsForBands) return;
        var anyError = Median(known.Select(c => Math.Abs(residual[c.SpId])));
        var seasonError = known.GroupBy(c => c.Season).ToDictionary(g => g.Key, g => (g.Sum(c => Math.Abs(residual[c.SpId])) + 10 * anyError) / (g.Count() + 10));
        var rows = known.Select(c => ReliabilityRow(c, Blend(linearOf[c.SpId], _treeLog[c.SpId], c), linearOf[c.SpId], _treeLog[c.SpId], seasonError, anyError)).ToList();
        var k = rows[0].Length;
        var xtx = new double[k, k];
        var xty = new double[k];
        for (var i = 0; i < rows.Count; i++)
        {
            var size = Math.Log(Math.Abs(residual[known[i].SpId]) + 0.03);
            for (var a = 0; a < k; a++)
            {
                xty[a] += rows[i][a] * size;
                for (var b = 0; b < k; b++) xtx[a, b] += rows[i][a] * rows[i][b];
            }
        }
        for (var a = 0; a < k; a++) xtx[a, a] += 1e-3;
        var inverse = Invert(xtx);
        var beta = Enumerable.Range(0, k).Select(a => Enumerable.Range(0, k).Sum(b => inverse[a, b] * xty[b])).ToArray();
        var scored = known.Select((c, i) => (Score: Dot(beta, rows[i]), Residual: residual[c.SpId])).OrderBy(t => t.Score).ToList();
        var cuts = new[] { scored[scored.Count / 4].Score, scored[scored.Count / 2].Score, scored[3 * scored.Count / 4].Score };
        var tiers = Enumerable.Range(0, 4).Select(t =>
        {
            var part = scored.Where(s => TierOf(s.Score, cuts) == t).Select(s => s.Residual).Order().ToList();
            var middle = Median(part);
            return (part[part.Count / 10], part[9 * part.Count / 10], Median(part.Select(e => Math.Abs(Math.Exp(-e) - 1))),
                Math.Max(Math.Max(1.4826 * Median(part.Select(e => Math.Abs(e - middle))), Math.Abs(middle)), 0.05));
        }).ToArray();
        _reliability = new Reliability(beta, cuts, tiers, seasonError, anyError);
    }

    private static int TierOf(double score, double[] cuts) => cuts.Count(cut => score >= cut);

    private int? TierOf(MarketCard c)
    {
        if (_reliability is not { } r || _treeParts is null) return null;
        var linear = Dot(_beta, Features(c)) + PlayerEffect(c);
        var tree = TreeLog(c);
        return TierOf(Dot(r.Beta, ReliabilityRow(c, Blend(linear, tree, c), linear, tree, r.SeasonError, r.AnyError)), r.Cuts);
    }

    /// <summary>The card's price range at the model's grade; null where the model has no per-card errors (goalkeepers, small markets).</summary>
    public PriceBand? BandOf(MarketCard c)
    {
        if (TierOf(c) is not { } tier) return null;
        var (low, high, error, _) = _reliability!.Tiers[tier];
        var expected = Expected(c);
        return new PriceBand(Math.Max((long)(expected * Math.Exp(low)), FloorAt(c, Grade) ?? 0), (long)(expected * Math.Exp(high)), tier, error);
    }

    /// <summary>Held-out error of cards like this one; the position's when there is no per-card figure.</summary>
    public double ErrorOf(MarketCard c) => TierOf(c) is { } tier ? _reliability!.Tiers[tier].Error : ValidationError;

    /// <summary>What the held-out errors were in each trust tier, best known first; empty without per-card figures.</summary>
    public IReadOnlyList<(double Low, double High, double Error)> BandTiers =>
        _reliability is { } r ? r.Tiers.Select(t => (t.Low, t.High, t.Error)).ToList() : [];

    /// <summary>Spread of held-out log errors of cards like this one, so a price gap on a card the model hardly knows counts for less.</summary>
    public double LogScaleOf(MarketCard c) => TierOf(c) is { } tier ? _reliability!.Tiers[tier].Scale : ValidationLogScale;

    private static (double Error, double LogScale) Validate(string group, int grade, IReadOnlyList<MarketCard> data, IReadOnlyList<MarketMembership>? memberships,
        int? traitHinge, bool linearTraits)
    {
        // Card folds (MV-09): a held-out card is priced like any card, with what the footballer's other cards say.
        var train = data.Where(c => Fold(c.SpId) != 0).ToList();
        var holdout = data.Where(c => Fold(c.SpId) == 0).ToList();
        if (train.Count < 50 || holdout.Count < 10 || train.Select(c => c.PlayerId).Distinct().Count() < 10) return (double.NaN, double.NaN);
        // Feature selection and reference buckets must also use training data only.
        var model = new PriceModel(group, grade, train, memberships, validate: false, traitHinge: traitHinge, linearTraits: linearTraits);
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
    /// <param name="rawPenalty">Per column, a ridge in the column's own units (an effect seen on n cards keeps about
    /// n/(n+λ) of its size) instead of <paramref name="lambda"/>; NaN keeps the standardised ridge.</param>
    /// <param name="effects">Per row, the absorbed effect it belongs to (a player with several cards) or −1: uncentred
    /// dummies ridged by <paramref name="effectRidge"/>, solved through the Schur complement so many players stay cheap.
    /// The other coefficients are then compared within an effect (the same player's cards).</param>
    internal static (double[] Beta, double[] Se, double R2, double Sigma, double[] Completed, double[][] Influence) RidgeTobit(IReadOnlyList<double[]> x, IReadOnlyList<double> y,
        IReadOnlyList<int> clusters, IReadOnlyList<bool> controls, IReadOnlyList<bool>? censored = null, double lambda = Ridge, int iterations = 60,
        IReadOnlyList<double>? rawPenalty = null, IReadOnlyList<int>? effects = null, double effectRidge = FitPlayerRidge, IReadOnlyList<double>? zPenalty = null)
    {
        var n = x.Count;
        var k = x[0].Length;
        var group = effects?.ToArray() ?? Enumerable.Repeat(-1, n).ToArray();
        var effectCount = group.Length == 0 ? 0 : group.Max() + 1;
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
        var columns = new double[k][];
        for (var j = 0; j < k; j++)
        {
            columns[j] = new double[n];
            for (var i = 0; i < n; i++) columns[j][i] = z[i][j];
        }
        var a = new double[k, k];
        Parallel.For(0, k, p =>
        {
            var left = columns[p];
            for (var q = p; q < k; q++)
            {
                var right = columns[q];
                double sum = 0;
                for (var i = 0; i < n; i++) sum += left[i] * right[i];
                a[p, q] = sum;
                a[q, p] = sum;
            }
        });
        for (var j = 1; j < k; j++)
            a[j, j] += rawPenalty is not null && double.IsFinite(rawPenalty[j]) ? rawPenalty[j] / (sd[j] * sd[j])
                : zPenalty is not null && double.IsFinite(zPenalty[j]) ? zPenalty[j] : controls[j] ? 1e-3 : lambda;
        // Effects: B[p, g] = Σ z_ip over the effect's rows, D_g = rows + ridge; S = A − B D⁻¹ Bᵀ is what the other columns solve.
        var bsum = new double[k, effectCount];
        var d = new double[effectCount];
        for (var i = 0; i < n; i++)
        {
            if (group[i] < 0) continue;
            d[group[i]] += 1;
            for (var p = 0; p < k; p++) bsum[p, group[i]] += z[i][p];
        }
        for (var g = 0; g < effectCount; g++) d[g] += effectRidge;
        if (effectCount > 0)
            Parallel.For(0, k, p =>
            {
                for (var q = 0; q < k; q++)
                {
                    double sum = 0;
                    for (var g = 0; g < effectCount; g++) sum += bsum[p, g] * bsum[q, g] / d[g];
                    a[p, q] -= sum;
                }
            });
        var inv = Invert(a);
        // Each row with its effect's shrunk mean taken out: b = S⁻¹ Σ w_i y_i, so these also carry the clustered errors.
        var w = new double[n][];
        for (var i = 0; i < n; i++)
        {
            w[i] = (double[])z[i].Clone();
            if (group[i] >= 0) for (var p = 0; p < k; p++) w[i][p] -= bsum[p, group[i]] / d[group[i]];
        }

        var u = new double[effectCount];
        double[] Solve(double[] target)
        {
            var wty = new double[k];
            for (var i = 0; i < n; i++)
                for (var j = 0; j < k; j++)
                    wty[j] += w[i][j] * target[i];
            var b = new double[k];
            for (var p = 0; p < k; p++) for (var q = 0; q < k; q++) b[p] += inv[p, q] * wty[q];
            Array.Clear(u);
            for (var i = 0; i < n; i++) if (group[i] >= 0) u[group[i]] += target[i] - Dot(b, z[i]);
            for (var g = 0; g < effectCount; g++) u[g] /= d[g];
            return b;
        }
        double Fitted(double[] b, int i) => Dot(b, z[i]) + (group[i] >= 0 ? u[group[i]] : 0);

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
                var change = next.Zip(bz, (p, q) => Math.Abs(p - q)).Max();
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
            for (var j = 0; j < k; j++) score[j] += e * w[i][j];
        }
        if (double.IsNaN(sigma)) sigma = Math.Max(Math.Sqrt(sseAll / n), 0.05);
        var cr1 = scores.Count / (double)Math.Max(scores.Count - 1, 1) * (n - 1) / Math.Max(n - k - effectCount, 1);
        var beta = new double[k];
        var se = new double[k];
        // Each cluster's pull on the original-scale coefficients, CR1 included: Var(a·β) = Σ_g (a·influence_g)².
        var influence = scores.Values.AsParallel().AsOrdered().Select(score =>
        {
            var row = new double[k];
            for (var j = 0; j < k; j++)
            {
                double s = 0;
                for (var q = 0; q < k; q++) s += inv[j, q] * score[q];
                row[j] = s * Math.Sqrt(Math.Max(cr1, 0)) / sd[j];
            }
            return row;
        }).ToArray();
        for (var j = 0; j < k; j++)
        {
            se[j] = Math.Sqrt(influence.Sum(row => row[j] * row[j]));
            beta[j] = bz[j] / sd[j];
        }
        // Back to the original columns: the constant absorbs the centring (its influence row is not used).
        beta[0] = bz[0] - Enumerable.Range(1, k - 1).Sum(j => mu[j] * bz[j] / sd[j]);
        return (beta, se, sst > 0 ? 1 - sseAll / sst : 0, sigma, yi, influence);
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
            void Eliminate(int r)
            {
                var f = m[r, col];
                if (r == col || f == 0) return;
                for (var j = 0; j < 2 * n; j++) m[r, j] -= f * m[col, j];
            }
            if (n >= 200) Parallel.For(0, n, Eliminate);
            else for (var r = 0; r < n; r++) Eliminate(r);
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
    /// OVR at the chosen enhancement grade, before adaptability, team colours and training.
    /// </summary>
    public int? MinOvr { get; init; }
    public int? MaxOvr { get; init; }
    public int MinWeakFoot { get; init; }
    /// <summary>The card has any of these traits ("라인 브레이커" or "크로스 포쳐"); no card has two (checked 2026-10-30).</summary>
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
        if (Traits.Count > 0 && !Traits.Any(t => c.Tags.Contains($"trait:{t}"))) return false;
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
    public int Grade { get; init; } = MarketDefaults.Grade;
    public CardFilter Filter { get; init; } = new();
}

public sealed record ValuePick(MarketCard Card, int Grade, long Price, long Expected)
{
    public PriceBand? Band { get; init; }
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
        : score < -1.96 ? "비슷한 카드보다 쌈 [추정]" : "모델 오차 범위 내";
    public static string ErrorLabel(double error) => double.IsFinite(error) ? $"{error:P0}" : "표본 부족";
}

public static class ValueFinder
{
    /// <remarks>A card whose price is its enhancement floor is left out: the floor rule set that price, so it cannot be a bargain.</remarks>
    public static IReadOnlyList<ValuePick> Find(PriceModel model, IEnumerable<MarketCard> cards, ValueQuery q) =>
        cards.Where(c => c.Group == q.Group && q.Filter.Matches(c, q.Grade) && !PriceModel.AtFloor(c, q.Grade))
            .Select(c => new ValuePick(c, q.Grade, c.PriceAt(q.Grade), model.ExpectedAt(c, q.Grade))
                { ModelError = model.ErrorOf(c), LogErrorScale = model.LogScaleOf(c), Band = q.Grade == model.Grade ? model.BandOf(c) : null })
            .OrderBy(p => PriceEvidence.Rank(p.EvidenceScore, p.Card.RatingCount))
            .ToList();
}
