namespace FcHelper.Market;

public sealed record PriceEffect(string Name, double Percent, double Low, double High, double T, int? Cards);

/// <summary>
/// Hedonic price model for one position group at one enhancement grade:
///   ln(price) ~ OVR + OVR² + weak foot + salary + (stat − OVR) per stat + tags + season
/// A coefficient reads as "everything else equal, this changes the price by X%" in today's market. It is an
/// association: a tag can also stand in for things the model does not see (feel, team colour, uncollected stats).
/// </summary>
public sealed class PriceModel
{
    private const int MinTagCards = 8, MinSeasonCards = 5;
    private const string AbsoluteStat = "height";

    private readonly string[] _stats;
    private readonly string[] _tags;
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

    private PriceModel(string group, int grade, IReadOnlyList<MarketCard> data)
    {
        Group = group;
        Grade = grade;
        Cards = data.Count;
        var g = MarketGroups.Get(group);
        _stats = g.AllStats.Where(s => data.Count(c => c.Stats.ContainsKey(s)) >= 0.9 * data.Count).ToArray();
        var seasonCounts = data.GroupBy(c => c.Season).ToDictionary(x => x.Key, x => x.Count());
        _baseSeason = seasonCounts.MaxBy(kv => kv.Value).Key;
        _seasons = seasonCounts.Where(kv => kv.Value >= MinSeasonCards && kv.Key != _baseSeason).Select(kv => kv.Key).Order().ToArray();
        var tagCounts = data.SelectMany(c => c.Tags).GroupBy(t => t).ToDictionary(x => x.Key, x => x.Count());
        _tags = tagCounts.Where(kv => kv.Value >= MinTagCards).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToArray();
        _ovrMean = data.Average(c => c.Ovr1);
        _statMeans = _stats.ToDictionary(s => s, s => data.Where(c => c.Stats.ContainsKey(s)).Average(c => (double)c.Stats[s]));

        _names = ["const", "OVR +1", "OVR²", "양발 (약발 5)", "약발 4", "급여 +1",
            .. _stats.Select(s => $"{MarketGroups.StatNames.GetValueOrDefault(s, s)} +1" + (s == AbsoluteStat ? "" : " (같은 OVR)")),
            .. _tags.Select(MarketGroups.TagLabel), .. _seasons.Select(s => $"season:{s}"), "season:기타"];
        _counts = tagCounts.ToDictionary(kv => MarketGroups.TagLabel(kv.Key), kv => kv.Value);
        _counts["양발 (약발 5)"] = data.Count(c => c.WeakFoot >= 5);
        _counts["약발 4"] = data.Count(c => c.WeakFoot == 4);

        (_beta, _se, R2) = Ols(data.Select(c => (Features(c), Math.Log(c.PriceAt(grade)))).ToList());
    }

    /// <returns>Null when there are too few traded cards to fit.</returns>
    public static PriceModel? Fit(string group, int grade, IEnumerable<MarketCard> cards)
    {
        var data = cards.Where(c => c.Group == group && c.IsTraded && c.PriceAt(grade) > Grades.FloorPrice).ToList();
        return data.Count < 60 ? null : new PriceModel(group, grade, data);
    }

    public double Predict(MarketCard c) => Math.Exp(Dot(_beta, Features(c)));

    public IReadOnlyList<PriceEffect> Effects() =>
        _names.Select((n, i) => (n, i))
            .Where(t => t.i > 0 && t.n != "OVR²" && !t.n.StartsWith("season:"))
            .Select(t => new PriceEffect(t.n, Pct(_beta[t.i]), Pct(_beta[t.i] - 1.96 * _se[t.i]), Pct(_beta[t.i] + 1.96 * _se[t.i]),
                _se[t.i] > 0 ? _beta[t.i] / _se[t.i] : 0, _counts.TryGetValue(t.n, out var n) ? n : null))
            .ToList();

    private double[] Features(MarketCard c)
    {
        var d = c.Ovr1 - _ovrMean;
        var x = new List<double>(_names.Length) { 1, d, d * d, c.WeakFoot >= 5 ? 1 : 0, c.WeakFoot == 4 ? 1 : 0, c.Pay };
        foreach (var s in _stats)
            x.Add(c.Stats.TryGetValue(s, out var v) ? v - (s == AbsoluteStat ? _statMeans[s] : c.Ovr1) : 0);
        foreach (var t in _tags) x.Add(c.Tags.Contains(t) ? 1 : 0);
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

public sealed record ValueQuery
{
    public required string Group { get; init; }
    public int Grade { get; init; } = 8;
    public long MinPrice { get; init; }
    public long MaxPrice { get; init; } = long.MaxValue;
    public int MinWeakFoot { get; init; }
    public string? Trait { get; init; }
    public int SkillMove { get; init; }
    /// <summary>Cards almost nobody rated are rarely traded; their low price says little.</summary>
    public int MinRatings { get; init; } = 10;
}

public sealed record ValuePick(MarketCard Card, int Grade, long Price, long Expected)
{
    /// <summary>Price relative to the model's expectation: −0.4 = 40% cheaper than similar cards.</summary>
    public double Discount => Price / (double)Expected - 1;
}

public static class ValueFinder
{
    public static IReadOnlyList<ValuePick> Find(PriceModel model, IEnumerable<MarketCard> cards, ValueQuery q) =>
        cards.Where(c => c.Group == q.Group && c.IsTraded && c.RatingCount >= q.MinRatings && c.WeakFoot >= q.MinWeakFoot)
            .Where(c => c.PriceAt(q.Grade) is var p && p > Grades.FloorPrice && p >= q.MinPrice && p <= q.MaxPrice)
            .Where(c => q.Trait is null || c.Tags.Contains($"trait:{q.Trait}"))
            .Where(c => q.SkillMove == 0 || MarketGroups.SkillTags.Any(s => s >= q.SkillMove && c.Tags.Contains($"skill:{s}")))
            .Select(c => new ValuePick(c, q.Grade, c.PriceAt(q.Grade), (long)model.Predict(c)))
            .OrderBy(p => p.Discount)
            .ToList();
}
