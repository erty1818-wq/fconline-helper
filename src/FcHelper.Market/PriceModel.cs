namespace FcHelper.Market;

public sealed record PriceEffect(string Name, double Percent, double Low, double High, double T, int? Cards);

public enum FactorKind { Stat, Height, Trait, Skill, Body, Foot, Salary, TeamColor }

/// <summary>
/// What one thing is worth on the market at a position and grade, everything else equal: in % of the price, in OVR
/// points (the price of that many extra OVR) and in BP on a typical card of the group. [추정: 시장 회귀]
/// </summary>
public sealed record PriceFactor(string Key, string Name, FactorKind Kind, double Percent, double Low, double High, double OvrEquivalent,
    long BpAtMedian, int? Cards, bool IsCore, bool IsInflating)
{
    /// <summary>The 95% range excludes zero: the market clearly prices it.</summary>
    public bool Clear => Low > 0 || High < 0;
}

/// <summary>
/// Hedonic price model for one position group at one enhancement grade:
///   ln(price) ~ OVR + OVR² + weak foot + salary + (stat − OVR) per stat + tags + season
/// A coefficient reads as "everything else equal, this changes the price by X%" in today's market. It is an
/// association: a tag can also stand in for things the model does not see (feel, team colour, uncollected stats).
/// </summary>
public sealed class PriceModel
{
    private const int MinTagCards = 8, MinSeasonCards = 5;
    /// <summary>Stats measured on their own scale, not against the OVR.</summary>
    private static readonly string[] AbsoluteStats = ["height", "weight"];

    /// <summary>
    /// Height windows from the video: centre-backs are best at 183-192 cm (taller or shorter feels slower), keepers at
    /// 187-189 cm (worse the further away). Used only where height was collected.
    /// </summary>
    private static readonly Dictionary<string, (string Name, Func<int, double> Value)[]> HeightFeatures = new()
    {
        ["CB"] = [("키 183~192cm", h => h is >= 183 and <= 192 ? 1 : 0)],
        ["GK"] = [("키 187~189cm에서 1cm 멀어짐", h => Math.Max(0, Math.Abs(h - 188) - 1))],
    };

    private readonly string[] _stats;
    private readonly (string Name, Func<int, double> Value)[] _height;
    private readonly string[] _tags;
    private readonly long _medianPrice;
    private readonly MarketGroup _group;
    private readonly IReadOnlySet<long>? _rankerMembers;

    /// <summary>Pseudo-tag: the card counts for one of the team colours rankers use most (priced like a tag).</summary>
    public const string RankerColorTag = "tc:ranker";
    private readonly string[] _seasons;
    private readonly string _baseSeason;
    private readonly double _ovrMean;
    private readonly Dictionary<string, double> _statMeans;
    private readonly double[] _beta;
    private readonly double[] _se;
    private readonly string[] _names;
    private readonly Dictionary<string, int> _counts;

    public string Group { get; }
    public int Grade { get; }
    public int Cards { get; }
    public double R2 { get; }

    private PriceModel(string group, int grade, IReadOnlyList<MarketCard> data, IReadOnlySet<long>? rankerMembers)
    {
        _rankerMembers = rankerMembers;
        Group = group;
        Grade = grade;
        Cards = data.Count;
        var g = _group = MarketGroups.Get(group);
        _stats = g.AllStats.Where(s => data.Count(c => c.Stats.ContainsKey(s)) >= 0.9 * data.Count).ToArray();
        _height = _stats.Contains("height") && HeightFeatures.TryGetValue(group, out var hf) ? hf : [];
        var prices = data.Select(c => c.PriceAt(grade)).Order().ToList();
        _medianPrice = prices[prices.Count / 2];
        var seasonCounts = data.GroupBy(c => c.Season).ToDictionary(x => x.Key, x => x.Count());
        _baseSeason = seasonCounts.MaxBy(kv => kv.Value).Key;
        _seasons = seasonCounts.Where(kv => kv.Value >= MinSeasonCards && kv.Key != _baseSeason).Select(kv => kv.Key).Order().ToArray();
        var tagCounts = data.SelectMany(c => c.Tags).GroupBy(t => t).ToDictionary(x => x.Key, x => x.Count());
        if (rankerMembers is not null) tagCounts[RankerColorTag] = data.Count(c => rankerMembers.Contains(c.SpId));
        _tags = tagCounts.Where(kv => kv.Value >= MinTagCards).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToArray();
        _ovrMean = data.Average(c => c.Ovr1);
        _statMeans = _stats.ToDictionary(s => s, s => data.Where(c => c.Stats.ContainsKey(s)).Average(c => (double)c.Stats[s]));

        _names = ["const", "OVR +1", "OVR²", "양발 (약발 5)", "약발 4", "급여 +1",
            .. _stats.Select(s => $"{MarketGroups.StatNames.GetValueOrDefault(s, s)} +1" + (AbsoluteStats.Contains(s) ? "" : " (같은 OVR)")),
            .. _height.Select(h => h.Name),
            .. _tags.Select(MarketGroups.TagLabel), .. _seasons.Select(s => $"season:{s}"), "season:기타"];
        _counts = tagCounts.ToDictionary(kv => MarketGroups.TagLabel(kv.Key), kv => kv.Value);
        _counts["양발 (약발 5)"] = data.Count(c => c.WeakFoot >= 5);
        _counts["약발 4"] = data.Count(c => c.WeakFoot == 4);

        (_beta, _se, R2) = Ols(data.Select(c => (Features(c), Math.Log(c.PriceAt(grade)))).ToList());
    }

    /// <returns>Null when there are too few traded cards to fit.</returns>
    /// <remarks>Name-priced cards (<see cref="MarketGroups.PriceOutliers"/>) are left out of the fit; they are still priced by it.</remarks>
    /// <param name="rankerMembers">Cards of the team colours rankers use most: their premium is priced, so a card outside
    /// them is not taken for a bargain just because nobody's squad needs it.</param>
    public static PriceModel? Fit(string group, int grade, IEnumerable<MarketCard> cards, IReadOnlySet<long>? rankerMembers = null)
    {
        var data = cards.Where(c => c.Group == group && c.IsTraded && c.PriceAt(grade) > Grades.FloorPrice && !MarketGroups.IsPriceOutlier(c)).ToList();
        return data.Count < 60 ? null : new PriceModel(group, grade, data, rankerMembers);
    }

    private bool Has(MarketCard c, string tag) => tag == RankerColorTag ? _rankerMembers?.Contains(c.SpId) == true : c.Tags.Contains(tag);

    public double Predict(MarketCard c) => Math.Exp(Dot(_beta, Features(c)));

    /// <summary>
    /// What the market pays for this card beyond its OVR, season and salary (weak foot, stats relative to OVR, traits,
    /// skill moves, body), converted to OVR points at the card's own OVR: e.g. +1.4 means "priced like 1.4 OVR more".
    /// Used as the playing-value premium of a card; a market reading, not a measurement [추정].
    /// </summary>
    public double PremiumInOvr(MarketCard c)
    {
        var x = Features(c);
        const int firstPremium = 3; // after const, OVR, OVR²; salary (index 5) is a cost, not a quality
        var end = 6 + _stats.Length + _height.Length + _tags.Length;
        double premium = 0;
        for (var i = firstPremium; i < end; i++)
            if (i != 5) premium += _beta[i] * x[i];
        var slope = _beta[1] + 2 * _beta[2] * (c.Ovr1 - _ovrMean);
        return slope > 0.01 ? Math.Clamp(premium / slope, -6, 10) : 0;
    }

    public IReadOnlyList<PriceEffect> Effects() =>
        _names.Select((n, i) => (n, i))
            .Where(t => t.i > 0 && t.n != "OVR²" && !t.n.StartsWith("season:"))
            .Select(t => new PriceEffect(t.n, Pct(_beta[t.i]), Pct(_beta[t.i] - 1.96 * _se[t.i]), Pct(_beta[t.i] + 1.96 * _se[t.i]),
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
            result.Add(new PriceFactor(key, name, kind, Pct(_beta[i]), Pct(_beta[i] - 1.96 * _se[i]), Pct(_beta[i] + 1.96 * _se[i]),
                slope > 0.01 ? _beta[i] / slope : 0, (long)(_medianPrice * (Math.Exp(_beta[i]) - 1)),
                _counts.TryGetValue(name, out var n) ? n : null, isCore, inflating));
        }
        Add(3, "foot:5", FactorKind.Foot, true);
        Add(4, "foot:4", FactorKind.Foot, false);
        Add(5, "pay", FactorKind.Salary, false);
        for (var i = 0; i < _stats.Length; i++)
            Add(6 + i, _stats[i], AbsoluteStats.Contains(_stats[i]) ? FactorKind.Height : FactorKind.Stat, core.Contains(_stats[i]), _group.InflatingStats.Contains(_stats[i]));
        for (var i = 0; i < _height.Length; i++) Add(6 + _stats.Length + i, $"height:{i}", FactorKind.Height, true);
        for (var i = 0; i < _tags.Length; i++)
        {
            var tag = _tags[i];
            var kind = tag.StartsWith("trait:") ? FactorKind.Trait : tag.StartsWith("skill:") ? FactorKind.Skill
                : tag == RankerColorTag ? FactorKind.TeamColor : FactorKind.Body;
            Add(6 + _stats.Length + _height.Length + i, tag, kind, kind == FactorKind.Trait && _group.KeyTraits.Contains(tag[6..]));
        }
        return result;
    }

    /// <summary>Typical (median) price of a traded card of the group at the model's grade.</summary>
    public long MedianPrice => _medianPrice;

    private double[] Features(MarketCard c)
    {
        var d = c.Ovr1 - _ovrMean;
        var x = new List<double>(_names.Length) { 1, d, d * d, c.WeakFoot >= 5 ? 1 : 0, c.WeakFoot == 4 ? 1 : 0, c.Pay };
        foreach (var s in _stats)
            x.Add(c.Stats.TryGetValue(s, out var v) ? v - (AbsoluteStats.Contains(s) ? _statMeans[s] : c.Ovr1) : 0);
        foreach (var (_, value) in _height)
            x.Add(c.Stats.TryGetValue("height", out var h) ? value(h) : 0);
        foreach (var t in _tags) x.Add(Has(c, t) ? 1 : 0);
        foreach (var s in _seasons) x.Add(c.Season == s ? 1 : 0);
        x.Add(c.Season != _baseSeason && !_seasons.Contains(c.Season) ? 1 : 0);
        return [.. x];
    }

    private static double Pct(double b) => (Math.Exp(b) - 1) * 100;
    private static double Dot(double[] a, double[] b) { double s = 0; for (var i = 0; i < a.Length; i++) s += a[i] * b[i]; return s; }

    /// <summary>Ordinary least squares through the normal equations; a tiny ridge keeps an empty column solvable.</summary>
    internal static (double[] Beta, double[] Se, double R2) Ols(IReadOnlyList<(double[] X, double Y)> rows)
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
        for (var i = 1; i < k; i++) xtx[i, i] += 1e-6;
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
        var s2 = sse / Math.Max(rows.Count - k, 1);
        var se = new double[k];
        for (var i = 0; i < k; i++) se[i] = Math.Sqrt(Math.Max(inv[i, i] * s2, 0));
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
    /// <summary>Price relative to the model's expectation: −0.4 = 40% cheaper than similar cards.</summary>
    public double Discount => Price / (double)Expected - 1;
}

public static class ValueFinder
{
    public static IReadOnlyList<ValuePick> Find(PriceModel model, IEnumerable<MarketCard> cards, ValueQuery q) =>
        cards.Where(c => c.Group == q.Group && q.Filter.Matches(c, q.Grade))
            .Select(c => new ValuePick(c, q.Grade, c.PriceAt(q.Grade), (long)model.Predict(c)))
            .OrderBy(p => p.Discount)
            .ToList();
}
