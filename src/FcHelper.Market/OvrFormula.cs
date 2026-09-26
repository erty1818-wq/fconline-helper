namespace FcHelper.Market;

/// <summary>
/// How the game turns stats into a position's OVR: OVR = ⌊Σ (stat × weight) ÷ 100⌋ with integer weights that add up to
/// 100 per position. The weights were fitted to the data center's own numbers (stats and position OVRs of 244 cards
/// across every position group, docs/OVR_FORMULA.md) and reproduce all of them [계산]; the stats each position uses are
/// the ones the data center marks for it (data-positon).
/// </summary>
public static class OvrFormula
{
    private static readonly Dictionary<string, Dictionary<string, int>> Weights = new()
    {
        ["ST"] = W(("골 결정력", 18), ("위치 선정", 13), ("볼 컨트롤", 10), ("슛 파워", 10), ("헤더", 10), ("반응 속도", 8), ("드리블", 7), ("몸싸움", 5), ("속력", 5), ("짧은 패스", 5), ("가속력", 4), ("중거리 슛", 3), ("발리슛", 2)),
        ["CF"] = W(("볼 컨트롤", 15), ("드리블", 14), ("위치 선정", 13), ("골 결정력", 11), ("반응 속도", 9), ("짧은 패스", 9), ("시야", 8), ("가속력", 5), ("속력", 5), ("슛 파워", 5), ("중거리 슛", 4), ("헤더", 2)),
        ["LW"] = W(("드리블", 16), ("볼 컨트롤", 14), ("골 결정력", 10), ("위치 선정", 9), ("짧은 패스", 9), ("크로스", 9), ("가속력", 7), ("반응 속도", 7), ("속력", 6), ("시야", 6), ("중거리 슛", 4), ("민첩성", 3)),
        ["RW"] = W(("드리블", 16), ("볼 컨트롤", 14), ("골 결정력", 10), ("위치 선정", 9), ("짧은 패스", 9), ("크로스", 9), ("가속력", 7), ("반응 속도", 7), ("속력", 6), ("시야", 6), ("중거리 슛", 4), ("민첩성", 3)),
        ["CAM"] = W(("짧은 패스", 16), ("볼 컨트롤", 15), ("시야", 14), ("드리블", 13), ("위치 선정", 9), ("골 결정력", 7), ("반응 속도", 7), ("중거리 슛", 5), ("가속력", 4), ("긴 패스", 4), ("민첩성", 3), ("속력", 3)),
        ["LM"] = W(("드리블", 15), ("볼 컨트롤", 13), ("짧은 패스", 11), ("크로스", 10), ("위치 선정", 8), ("가속력", 7), ("반응 속도", 7), ("시야", 7), ("골 결정력", 6), ("속력", 6), ("긴 패스", 5), ("스태미너", 5)),
        ["RM"] = W(("드리블", 15), ("볼 컨트롤", 13), ("짧은 패스", 11), ("크로스", 10), ("위치 선정", 8), ("가속력", 7), ("반응 속도", 7), ("시야", 7), ("골 결정력", 6), ("속력", 6), ("긴 패스", 5), ("스태미너", 5)),
        ["CM"] = W(("짧은 패스", 17), ("볼 컨트롤", 14), ("긴 패스", 13), ("시야", 13), ("반응 속도", 8), ("드리블", 7), ("스태미너", 6), ("위치 선정", 6), ("가로채기", 5), ("태클", 5), ("중거리 슛", 4), ("골 결정력", 2)),
        ["CDM"] = W(("가로채기", 14), ("짧은 패스", 14), ("태클", 12), ("긴 패스", 10), ("볼 컨트롤", 10), ("대인 수비", 9), ("반응 속도", 7), ("스태미너", 6), ("슬라이딩 태클", 5), ("적극성", 5), ("몸싸움", 4), ("시야", 4)),
        ["LWB"] = W(("가로채기", 12), ("크로스", 12), ("슬라이딩 태클", 11), ("스태미너", 10), ("짧은 패스", 10), ("반응 속도", 8), ("볼 컨트롤", 8), ("태클", 8), ("대인 수비", 7), ("속력", 6), ("가속력", 4), ("드리블", 4)),
        ["RWB"] = W(("가로채기", 12), ("크로스", 12), ("슬라이딩 태클", 11), ("스태미너", 10), ("짧은 패스", 10), ("반응 속도", 8), ("볼 컨트롤", 8), ("태클", 8), ("대인 수비", 7), ("속력", 6), ("가속력", 4), ("드리블", 4)),
        ["LB"] = W(("슬라이딩 태클", 14), ("가로채기", 12), ("태클", 11), ("크로스", 9), ("대인 수비", 8), ("반응 속도", 8), ("스태미너", 8), ("볼 컨트롤", 7), ("속력", 7), ("짧은 패스", 7), ("가속력", 5), ("헤더", 4)),
        ["RB"] = W(("슬라이딩 태클", 14), ("가로채기", 12), ("태클", 11), ("크로스", 9), ("대인 수비", 8), ("반응 속도", 8), ("스태미너", 8), ("볼 컨트롤", 7), ("속력", 7), ("짧은 패스", 7), ("가속력", 5), ("헤더", 4)),
        ["CB"] = W(("태클", 17), ("대인 수비", 14), ("가로채기", 13), ("몸싸움", 10), ("슬라이딩 태클", 10), ("헤더", 10), ("적극성", 7), ("반응 속도", 5), ("짧은 패스", 5), ("볼 컨트롤", 4), ("점프", 3), ("속력", 2)),
        ["SW"] = W(("대인 수비", 15), ("슬라이딩 태클", 15), ("태클", 15), ("몸싸움", 10), ("헤더", 10), ("가로채기", 8), ("적극성", 8), ("반응 속도", 5), ("볼 컨트롤", 5), ("짧은 패스", 5), ("점프", 4)),
        ["GK"] = W(("GK 다이빙", 21), ("GK 반응속도", 21), ("GK 위치 선정", 21), ("GK 핸들링", 21), ("반응 속도", 11), ("GK 킥", 5)),
    };

    public static bool Has(string position) => Weights.ContainsKey(Formations.Normalize(position));

    /// <summary>Weights of a position (stat → weight, heaviest first), empty for an unknown position.</summary>
    public static IReadOnlyDictionary<string, int> Of(string position) =>
        Weights.TryGetValue(Formations.Normalize(position), out var w) ? w : new Dictionary<string, int>();

    public static int Weight(string position, string stat) => Of(position).GetValueOrDefault(stat);

    /// <summary>Σ stat × weight: the OVR is this ÷ 100, rounded down.</summary>
    public static int Points(string position, IReadOnlyDictionary<string, int> stats) =>
        Of(position).Sum(kv => kv.Value * stats.GetValueOrDefault(kv.Key));

    public static int Ovr(string position, IReadOnlyDictionary<string, int> stats) => Points(position, stats) / 100;

    private static Dictionary<string, int> W(params (string Stat, int Weight)[] w) =>
        w.OrderByDescending(x => x.Weight).ToDictionary(x => x.Stat, x => x.Weight);
}

/// <summary>
/// A card's OVR as the game shows it in a squad, step by step: the data center's +1 figure, then grade, 적응도, the team
/// colours' 전체 능력치 and single-stat bonuses, and 집중훈련. <see cref="Exact"/> when the card's stats are known;
/// otherwise the single-stat parts are rounded down from the weights and the true figure can be one higher [추정].
/// </summary>
public sealed record FinalOvr(int Listed, int Grade, int Adaptability, int AllStats, int Detail, int Training, int Value, bool Exact,
    int? PointsToNext, IReadOnlyDictionary<string, int>? Stats)
{
    /// <summary>
    /// A correction for others' squads, fitted on one data-center squad [추정]: what the game adds beyond the steps above.
    /// The cause is not confirmed (more 적응도 than assumed, 훈련 코치, 클럽 하우스 …).
    /// </summary>
    public int Account { get; init; }

    public string Breakdown =>
        $"+1 기준 {Listed} · 강화 +{Grade} · 적응도 +{Adaptability}" + (AllStats > 0 ? $" · 팀컬러 전체 +{AllStats}" : "")
        + (Detail > 0 ? $" · 팀컬러 세부 +{Detail}" : "") + (Training > 0 ? $" · 집중훈련 +{Training}" : "")
        + (Account > 0 ? $" · 보정 +{Account} [추정, 원인 미확인]" : "") + $" = {Value}"
        + (Exact ? "" : " [추정: 세부 스탯 보너스는 내림, 실제는 1 높을 수 있음]");
}

/// <summary>The stats to raise in 집중훈련 for one position: the heaviest ones, +2 each.</summary>
public sealed record TrainingPlan(IReadOnlyList<(string Stat, int Weight, int Plus)> Stats, int Points, int Gain, int? PointsToNext,
    IReadOnlyList<(string Stat, int Plus)> Cheapest)
{
    public IReadOnlyDictionary<string, int> AsBonus => Stats.ToDictionary(s => s.Stat, s => s.Plus);
}

public static class FinalOvrMath
{
    public const int MaxAdaptability = 5;
    /// <summary>집중훈련 raises one stat by at most +2.</summary>
    public const int TrainingMaxPlus = 2;

    /// <summary>집중훈련 opens 5 stats up to +10 and 6 from +11.</summary>
    public static int TrainingSlots(int grade) => grade >= 11 ? 6 : 5;

    /// <summary>Team colour stat bonuses of the levels that reach a card, summed (전체 능력치, single stats).</summary>
    public static (int AllStats, IReadOnlyDictionary<string, int> Detail) ColorStats(IEnumerable<TeamColorLevel> levels)
    {
        var all = 0;
        var detail = new Dictionary<string, int>();
        foreach (var level in levels)
        {
            all += level.AllStats;
            foreach (var (stat, plus) in TeamColorParser.StatBonuses(level.Effects)) detail[stat] = detail.GetValueOrDefault(stat) + plus;
        }
        return (all, detail);
    }

    /// <param name="adaptability">적응도 1-5 (the data center's +1 figures are at 1).</param>
    /// <param name="levels">The team colour levels that reach this card (of 강화 colours only the best one).</param>
    /// <param name="training">집중훈련 per stat (+1/+2), or null.</param>
    public static FinalOvr Compute(MarketCard card, CardAbility? ability, string position, int grade, int adaptability,
        IEnumerable<TeamColorLevel> levels, IReadOnlyDictionary<string, int>? training = null)
    {
        var pos = Formations.Normalize(position);
        grade = Math.Clamp(grade, 1, 13);
        var listed = card.Positions.TryGetValue(pos, out var l) ? l : ability?.Positions.GetValueOrDefault(pos) is > 0 and var a ? a : card.Ovr1;
        var gradeBonus = Grades.Bonus[grade] - Grades.Bonus[1];
        var adapt = Math.Clamp(adaptability, 1, MaxAdaptability) - 1;
        var (all, detail) = ColorStats(levels);
        training ??= new Dictionary<string, int>();
        var uniform = gradeBonus + adapt + all;
        var weights = OvrFormula.Of(pos);

        if (ability is not null && weights.Count > 0)
        {
            var p0 = OvrFormula.Points(pos, ability.Stats);
            // The fitted formula reproduces the listed figure; if a card ever disagrees, keep the listed one and add the steps.
            var exact = p0 / 100 == listed;
            var baseValue = exact ? p0 / 100 : listed;
            var pUniform = p0 + 100 * uniform;
            var pDetail = pUniform + weights.Sum(kv => kv.Value * detail.GetValueOrDefault(kv.Key));
            var pTrained = pDetail + weights.Sum(kv => kv.Value * training.GetValueOrDefault(kv.Key));
            var shift = baseValue - p0 / 100;
            var stats = ability.Stats.ToDictionary(kv => kv.Key, kv => kv.Value + uniform + detail.GetValueOrDefault(kv.Key) + training.GetValueOrDefault(kv.Key));
            return new FinalOvr(listed, gradeBonus, adapt, all, pDetail / 100 - pUniform / 100, pTrained / 100 - pDetail / 100, pTrained / 100 + shift, exact,
                exact ? 100 - pTrained % 100 : null, stats);
        }

        // Without the card's stats: the single-stat parts rounded down.
        var detailGain = weights.Sum(kv => kv.Value * detail.GetValueOrDefault(kv.Key)) / 100;
        var trainingGain = weights.Sum(kv => kv.Value * training.GetValueOrDefault(kv.Key)) / 100;
        return new FinalOvr(listed, gradeBonus, adapt, all, detailGain, trainingGain, listed + uniform + detailGain + trainingGain, false, null, null);
    }

    /// <summary>
    /// The best 집중훈련 for a position: the <see cref="TrainingSlots"/> heaviest stats +2 each, what that adds, and the
    /// fewest steps (+1 on the heaviest stats first) that reach the next OVR when the points to it are known.
    /// </summary>
    public static TrainingPlan Training(string position, int grade, FinalOvr? before = null)
    {
        var top = OvrFormula.Of(position).OrderByDescending(kv => kv.Value).Take(TrainingSlots(grade)).Select(kv => (kv.Key, kv.Value, TrainingMaxPlus)).ToList();
        var points = top.Sum(t => t.Value * TrainingMaxPlus);
        var need = before?.PointsToNext;
        var gain = need is { } n ? (points >= n ? 1 + (points - n) / 100 : 0) : points / 100;
        var cheapest = new List<(string, int)>();
        if (need is { } left)
        {
            var steps = top.SelectMany(t => Enumerable.Repeat((t.Key, t.Value), TrainingMaxPlus)).ToList();
            foreach (var (stat, weight) in steps)
            {
                if (left <= 0) break;
                left -= weight;
                var i = cheapest.FindIndex(c => c.Item1 == stat);
                if (i >= 0) cheapest[i] = (stat, cheapest[i].Item2 + 1);
                else cheapest.Add((stat, 1));
            }
            if (left > 0) cheapest.Clear();
        }
        return new TrainingPlan(top, points, gain, need, cheapest);
    }
}
