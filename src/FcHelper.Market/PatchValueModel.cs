namespace FcHelper.Market;

public sealed record PatchTraitValue(string Trait, string Scope, double Percent, double Low, double High, int Cards, int Players,
    int MatchedCards, int MatchedPlayers, int Strata)
{
    public bool Estimated => double.IsFinite(Percent);
    public bool Clear => Estimated && Players >= 10 && (Low > 0 || High < 0);
}

/// <summary>
/// Matched-card log-price changes, net of season × position × five-point OVR market trends. A before/after association,
/// not a causal patch estimate: one pre-period cannot establish parallel trends or exclude concurrent changes.
/// </summary>
public static class PatchValueModel
{
    public static readonly string[] Traits = ["크로스 포쳐", "스피드스터", "아크로바틱 피니셔", "2개의 심장"];
    private sealed record Pair(MarketCard Card, double Change, string Stratum);

    public static IReadOnlyList<PatchTraitValue> Compare(IEnumerable<MarketCard> before, IEnumerable<MarketCard> after, int grade = 8)
    {
        var old = before.ToDictionary(c => (c.Group, c.SpId));
        var pairs = after.Where(c => old.ContainsKey((c.Group, c.SpId))).Select(c => (Now: c, Old: old[(c.Group, c.SpId)]))
            .Where(p => p.Now.IsTraded && p.Old.IsTraded && !MarketGroups.IsPriceOutlier(p.Now)
                && p.Now.PriceAt(grade) > Grades.FloorPrice && p.Old.PriceAt(grade) > Grades.FloorPrice
                && p.Now.Ovr1 == p.Old.Ovr1 && p.Now.Season == p.Old.Season
                && Traits.All(t => p.Now.Tags.Contains($"trait:{t}") == p.Old.Tags.Contains($"trait:{t}")))
            .Select(p => new Pair(p.Now, Math.Log(p.Now.PriceAt(grade) / (double)p.Old.PriceAt(grade)),
                $"{p.Now.Group}:{p.Now.Season}:{p.Now.Ovr1 / 5}")).ToList();
        var result = new List<PatchTraitValue>();
        foreach (var (scope, groups) in MarketService.TraitScopes)
        {
            // A multi-position card is counted once within the family; the first listed role supplies its trend stratum.
            var sample = groups.SelectMany(g => pairs.Where(p => p.Card.Group == g)).DistinctBy(p => p.Card.SpId)
                .GroupBy(p => p.Stratum).Where(g => g.Count() >= 3).SelectMany(g => g).ToList();
            result.AddRange(Fit(scope, sample));
        }
        return result;
    }

    private static IReadOnlyList<PatchTraitValue> Fit(string scope, List<Pair> sample)
    {
        var players = sample.Select(p => p.Card.PlayerId).Distinct().Count();
        var strata = sample.GroupBy(p => p.Stratum).ToList();
        var counts = Traits.ToDictionary(t => t, t => sample.Count(p => Has(p, t)));
        var members = Traits.ToDictionary(t => t, t => sample.Where(p => Has(p, t)).Select(p => p.Card.PlayerId).Distinct().Count());
        var eligible = Traits.Where(t => members[t] >= 8 && sample.Where(p => !Has(p, t)).Select(p => p.Card.PlayerId).Distinct().Count() >= 8
            && strata.Sum(g => { var mean = g.Average(p => Has(p, t) ? 1.0 : 0); return g.Sum(p => Math.Pow((Has(p, t) ? 1 : 0) - mean, 2)); }) > 1e-6).ToArray();
        var result = new List<PatchTraitValue>();
        var canFit = sample.Count >= 60 && players >= 10 && eligible.Length > 0 && sample.Count > strata.Count + eligible.Length;
        double[] beta = [], se = [];
        if (canFit)
        {
            var rows = new List<(double[] X, double Y)>();
            var clusters = new List<int>();
            foreach (var stratum in strata)
            {
                var mean = stratum.Average(p => p.Change);
                var means = eligible.Select(t => stratum.Average(p => Has(p, t) ? 1.0 : 0)).ToArray();
                foreach (var pair in stratum)
                {
                    rows.Add(([1, .. eligible.Select((t, i) => (Has(pair, t) ? 1 : 0) - means[i])], pair.Change - mean));
                    clusters.Add(pair.Card.PlayerId);
                }
            }
            // Absorbed stratum intercepts count towards the CR1 degrees-of-freedom correction.
            (beta, se, _) = PriceModel.Ols(rows, clusters, ridge: 1e-6, absorbedParameters: strata.Count - 1);
        }
        foreach (var trait in Traits.Where(t => counts[t] > 0))
        {
            var i = Array.IndexOf(eligible, trait) + 1;
            var estimated = canFit && i > 0;
            var critical = PriceModel.Critical95(players - 1);
            result.Add(new(trait, scope, estimated ? Pct(beta[i]) : double.NaN,
                estimated ? Pct(beta[i] - critical * se[i]) : double.NaN, estimated ? Pct(beta[i] + critical * se[i]) : double.NaN,
                counts[trait], members[trait], sample.Count, players, strata.Count));
        }
        return result;
    }

    private static bool Has(Pair pair, string trait) => pair.Card.Tags.Contains($"trait:{trait}");
    private static double Pct(double log) => (Math.Exp(log) - 1) * 100;
}
