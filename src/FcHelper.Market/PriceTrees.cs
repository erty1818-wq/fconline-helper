using FcHelper.Core.Models;

namespace FcHelper.Market;

/// <summary>
/// A small gradient-boosted tree ensemble (MV-14), about one second on a laptop. Alone it is worse than the linear fit,
/// but it errs differently: blended, the error falls on the footballers it was not chosen on (tools/market/mv14_blend.py).
/// Deterministic: a fixed seed, so the same market always gives the same prices.
/// </summary>
internal sealed class PriceTrees
{
    private const int Bins = 32, Rounds = 200, Leaves = 15, MinLeaf = 10, Seed = 7;
    private const double Rate = 0.05, LeafRidge = 5, HuberDelta = 0.7, RowShare = 0.8, ColumnShare = 0.5;

    /// <summary>A forest and the averages its cards gave, so that a card it did not see is described the way its training cards were.</summary>
    internal sealed record Part(Dictionary<int, (double Sum, int Count)> Players, Dictionary<string, (double Sum, int Count)> Seasons, double Mean)
    {
        public PriceTrees? Forest { get; set; }
    }

    private readonly record struct Node(int Feature, double Threshold, int Left, int Right, double Value);
    private sealed record Leaf(int[] Rows, double Sum, int Node)
    {
        public double Gain { get; init; }
        public int Feature { get; init; } = -1;
        public int Bin { get; init; }
    }

    private readonly double _start;
    private readonly List<Node[]> _trees;

    private PriceTrees(double start, List<Node[]> trees) { _start = start; _trees = trees; }

    public double Predict(double[] row)
    {
        var sum = _start;
        foreach (var tree in _trees) sum += Walk(tree, row);
        return sum;
    }

    private static double Walk(Node[] tree, double[] row)
    {
        var i = 0;
        while (tree[i].Feature >= 0)
        {
            var v = row[tree[i].Feature];
            i = double.IsNaN(v) || v <= tree[i].Threshold ? tree[i].Left : tree[i].Right;
        }
        return tree[i].Value;
    }

    /// <param name="x">Rows of plain columns; NaN means unknown and goes left.</param>
    public static PriceTrees Fit(IReadOnlyList<double[]> x, IReadOnlyList<double> y)
    {
        var (n, m) = (x.Count, x[0].Length);
        var uppers = new double[m][];
        var bins = new byte[m][];
        for (var f = 0; f < m; f++) (uppers[f], bins[f]) = Bin(x, f);
        var target = y.ToArray();
        var ordered = target.Order().ToArray();
        var start = ordered[n / 2];
        var predicted = Enumerable.Repeat(start, n).ToArray();
        var random = new Random(Seed);
        var trees = new List<Node[]>(Rounds);
        var columns = Enumerable.Range(0, m).ToArray();
        var keep = Math.Min(m, Math.Max(2, (int)(m * ColumnShare)));
        var residual = new double[n];
        var histSum = new double[Bins + 1];
        var histCount = new int[Bins + 1];
        for (var round = 0; round < Rounds; round++)
        {
            for (var i = 0; i < n; i++) residual[i] = Math.Clamp(target[i] - predicted[i], -HuberDelta, HuberDelta);
            for (var j = 0; j < keep; j++)
            {
                var k = random.Next(j, m);
                (columns[j], columns[k]) = (columns[k], columns[j]);
            }
            var rows = Enumerable.Range(0, n).Where(_ => random.NextDouble() < RowShare).ToArray();
            var nodes = new List<Node> { new(-1, 0, -1, -1, 0) };
            var leaves = new List<Leaf> { BestSplit(new Leaf(rows, rows.Sum(i => residual[i]), 0), columns, keep, bins, residual, histSum, histCount) };
            while (leaves.Count < Leaves)
            {
                var best = leaves.MaxBy(l => l.Gain)!;
                if (best.Feature < 0 || best.Gain <= 0) break;
                leaves.Remove(best);
                var column = bins[best.Feature];
                var left = best.Rows.Where(i => column[i] <= best.Bin).ToArray();
                var right = best.Rows.Where(i => column[i] > best.Bin).ToArray();
                var (leftAt, rightAt) = (nodes.Count, nodes.Count + 1);
                nodes.Add(new(-1, 0, -1, -1, 0));
                nodes.Add(new(-1, 0, -1, -1, 0));
                // Bin 0 holds the unknowns only, so splitting there sends exactly them left.
                var threshold = best.Bin == 0 ? double.NegativeInfinity : uppers[best.Feature][best.Bin - 1];
                nodes[best.Node] = new(best.Feature, threshold, leftAt, rightAt, 0);
                leaves.Add(BestSplit(new Leaf(left, left.Sum(i => residual[i]), leftAt), columns, keep, bins, residual, histSum, histCount));
                leaves.Add(BestSplit(new Leaf(right, right.Sum(i => residual[i]), rightAt), columns, keep, bins, residual, histSum, histCount));
            }
            foreach (var leaf in leaves) nodes[leaf.Node] = new(-1, 0, -1, -1, Rate * leaf.Sum / (leaf.Rows.Length + LeafRidge));
            var tree = nodes.ToArray();
            trees.Add(tree);
            for (var i = 0; i < n; i++) predicted[i] += Walk(tree, x[i]);
        }
        return new PriceTrees(start, trees);
    }

    private static (double[] Uppers, byte[] Bins) Bin(IReadOnlyList<double[]> x, int f)
    {
        var known = x.Select(r => r[f]).Where(v => !double.IsNaN(v)).Order().ToArray();
        var uppers = known.Length == 0 ? [] : Enumerable.Range(1, Bins - 1).Select(k => known[k * known.Length / Bins]).Where(v => v < known[^1]).Distinct().ToArray();
        var bins = new byte[x.Count];
        for (var i = 0; i < bins.Length; i++)
        {
            var v = x[i][f];
            if (double.IsNaN(v)) continue;
            var at = Array.BinarySearch(uppers, v);
            bins[i] = (byte)(1 + (at >= 0 ? at : ~at));
        }
        return (uppers, bins);
    }

    private static Leaf BestSplit(Leaf leaf, int[] columns, int keep, byte[][] bins, double[] residual, double[] histSum, int[] histCount)
    {
        var rows = leaf.Rows;
        if (rows.Length < 2 * MinLeaf) return leaf;
        var parent = leaf.Sum * leaf.Sum / (rows.Length + LeafRidge);
        var (bestGain, bestFeature, bestBin) = (0.0, -1, 0);
        for (var j = 0; j < keep; j++)
        {
            var column = bins[columns[j]];
            Array.Clear(histSum);
            Array.Clear(histCount);
            foreach (var i in rows)
            {
                histSum[column[i]] += residual[i];
                histCount[column[i]]++;
            }
            var (sum, count) = (0.0, 0);
            for (var b = 0; b < Bins; b++)
            {
                sum += histSum[b];
                count += histCount[b];
                var other = rows.Length - count;
                if (count < MinLeaf) continue;
                if (other < MinLeaf) break;
                var rest = leaf.Sum - sum;
                var gain = sum * sum / (count + LeafRidge) + rest * rest / (other + LeafRidge) - parent;
                if (gain > bestGain) (bestGain, bestFeature, bestBin) = (gain, columns[j], b);
            }
        }
        return leaf with { Gain = bestGain, Feature = bestFeature, Bin = bestBin };
    }
}

/// <summary>How a card is used in the stored official matches: its starts, and how many of them at +11.</summary>
public sealed record CardUse(int Starts, int AtEleven = 0);

/// <summary>How often each card is fielded and at what grade (MV-14, MV-15): the demand signals the specs do not carry.</summary>
public static class CardUsage
{
    /// <summary>Per card; the substitutes' bench does not count.</summary>
    public static IReadOnlyDictionary<long, CardUse> Count(IEnumerable<MatchDetail> matches)
    {
        var counts = new Dictionary<long, CardUse>();
        foreach (var match in matches)
            foreach (var side in match.MatchInfo)
                foreach (var player in side.Player)
                {
                    if (player.IsSubstitute) continue;
                    var known = counts.GetValueOrDefault(player.SpId) ?? new CardUse(0);
                    counts[player.SpId] = known with { Starts = known.Starts + 1, AtEleven = known.AtEleven + (player.SpGrade >= 11 ? 1 : 0) };
                }
        return counts;
    }
}
