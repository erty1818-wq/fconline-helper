namespace FcHelper.Market;

public enum SquadMode
{
    /// <summary>The highest effective OVR the budget allows.</summary>
    Strongest,
    /// <summary>Spends only where an OVR point is worth its price at this budget.</summary>
    Balanced,
    /// <summary>Nearly as strong for clearly less money: every 억 has to earn its OVR.</summary>
    Value,
    /// <summary>Leans towards what top rankers field (daily chart usage).</summary>
    RankerPicks,
}

/// <summary>What the user asks for. Prices are BP; grades 1-13.</summary>
public sealed record SquadRequest
{
    public required Formation Formation { get; init; }
    public long Budget { get; init; } = long.MaxValue;
    /// <summary>Squad salary cap (sum of the eleven starters' 급여).</summary>
    public int SalaryCap { get; init; } = int.MaxValue;
    /// <summary>Grades a card may be bought at; several = the optimiser also picks the grade.</summary>
    public IReadOnlyList<int> Grades { get; init; } = [8];
    public SquadMode Mode { get; init; } = SquadMode.Strongest;
    /// <summary>Slots filled already: slot index → card and grade. Owned cards cost nothing.</summary>
    public IReadOnlyDictionary<int, LockedCard> Locked { get; init; } = new Dictionary<int, LockedCard>();
    /// <summary>Footballers not to use (player id = spid % 1,000,000).</summary>
    public IReadOnlySet<int> ExcludedPlayers { get; init; } = new HashSet<int>();
    /// <summary>
    /// Team colours to build around (at most one 소속 and one 특성, as in the game). A 소속 bonus goes to all eleven
    /// once its level is met, a 특성 bonus only to its member cards.
    /// </summary>
    public IReadOnlyList<TeamColorTarget> TeamColors { get; init; } = [];
    /// <summary>
    /// With a 소속 colour, every card must count for it (a Barcelona squad is eleven Barcelona-colour cards). Locked
    /// slots are kept as the user set them.
    /// </summary>
    public bool OnlyAffiliationMembers { get; init; } = true;
    /// <summary>Only cards top rankers field at that position.</summary>
    public bool RankerPicksOnly { get; init; }
    public int Plans { get; init; } = 3;
}

public sealed record LockedCard(long SpId, int Grade, bool Owned);

/// <param name="TeamColorBonus">OVR the active team colours add at this slot (whole for "전체 능력치", fractional for single stats [추정]).</param>
public sealed record SquadSlot(int Index, string Position, MarketCard Card, int Grade, int Ovr, double Premium, double TeamColorBonus,
    long Price, long Expected, int Pay, int RankerUsers, double RankerShare, bool Locked, bool Owned)
{
    /// <summary>OVR + team colour bonus + what the market pays for the rest of the card, in OVR points [추정].</summary>
    public double EffectiveOvr => Ovr + TeamColorBonus + Premium;
    public double Discount => Expected > 0 ? Price / (double)Expected - 1 : 0;
}

public sealed record SquadPlan(string Label, SquadMode Mode, Formation Formation, IReadOnlyList<SquadSlot> Slots, IReadOnlyList<AppliedTeamColor> TeamColors)
{
    public long TotalPrice => Slots.Where(s => !s.Owned).Sum(s => s.Price);
    public long MarketValue => Slots.Sum(s => s.Price);
    public int TotalPay => Slots.Sum(s => s.Pay);
    public double AverageOvr => Slots.Average(s => s.Ovr + s.TeamColorBonus);
    public double AverageEffectiveOvr => Slots.Average(s => s.EffectiveOvr);
}

/// <summary>A card as one candidate for one slot at one grade.</summary>
/// <param name="Members">Bit i set = the card counts for request team colour i.</param>
internal sealed record Candidate(MarketCard Card, int Grade, int Ovr, double Premium, long Price, long Expected, int RankerUsers, double RankerShare,
    int Members, bool Locked, bool Owned)
{
    public long Cost => Owned ? 0 : Price;
}

/// <summary>
/// Picks eleven cards for a formation under a budget and a salary cap. Beam search over the slots (most constrained
/// first) keeping the best partial squads, with lower bounds on what the remaining slots must still cost so dead ends
/// are cut early; one footballer can appear once (any season). Different modes trade OVR against money or ranker use.
/// </summary>
public sealed class SquadBuilder(IReadOnlyList<MarketCard> cards, Func<MarketCard, PriceModel?> modelOf, RankerUsage? rankers = null)
{
    public int BeamWidth { get; init; } = 2000;
    public int CandidatesPerSlot { get; init; } = 70;

    public IReadOnlyList<SquadPlan> Build(SquadRequest request)
    {
        var slots = request.Formation.Slots;
        var candidates = slots.Select((pos, i) => CandidatesFor(i, pos, request)).ToList();
        var empty = candidates.Select((c, i) => (c, i)).Where(t => t.c.Count == 0).Select(t => slots[t.i]).ToList();
        if (empty.Count > 0)
        {
            var where = string.Join(", ", empty.Distinct());
            throw new InvalidOperationException(request.Budget < long.MaxValue
                ? $"예산 {Bp.Format(request.Budget)} 안에서 넣을 카드가 없는 자리: {where}. 예산·강화 단계·급여 한도를 확인하세요."
                : $"조건에 맞는 카드가 없는 자리: {where}");
        }

        var lambda = LambdaFor(request.Mode, request.Budget, candidates);
        var rankerWeight = request.Mode == SquadMode.RankerPicks ? 1.5 : 0;
        var plans = Search(request, candidates, lambda, rankerWeight);
        var label = request.Mode switch
        {
            SquadMode.Strongest => "최강",
            SquadMode.Balanced => "균형",
            SquadMode.Value => "가성비",
            _ => "랭커픽",
        };
        return plans.Select((p, i) => p with { Label = i == 0 ? label : $"{label} 대안 {i}" }).ToList();
    }

    /// <summary>Cost of money in OVR points per BP: zero for Strongest; for the others one OVR per slot budget share.</summary>
    private static double LambdaFor(SquadMode mode, long budget, List<List<Candidate>> candidates)
    {
        if (mode is SquadMode.Strongest or SquadMode.RankerPicks) return 0;
        var perSlot = budget == long.MaxValue ? candidates.Average(c => c.Max(x => (double)x.Cost)) : budget / 11.0;
        // An OVR point costs roughly a third of a card's price at squad level (OVR +1 ≈ +25-45% price).
        var ovrWorth = perSlot * 0.33;
        return mode == SquadMode.Balanced ? 1 / (ovrWorth * 2) : 1 / ovrWorth;
    }

    private List<Candidate> CandidatesFor(int index, string position, SquadRequest r)
    {
        if (r.Locked.TryGetValue(index, out var locked))
        {
            var card = cards.FirstOrDefault(c => c.SpId == locked.SpId && c.OvrAt(position, locked.Grade) is not null)
                ?? cards.FirstOrDefault(c => c.SpId == locked.SpId);
            if (card is null) return [];
            return [Make(card, position, locked.Grade, r, locked: true, owned: locked.Owned)];
        }
        var list = new List<Candidate>();
        foreach (var card in cards)
        {
            if (!card.IsTraded || r.ExcludedPlayers.Contains(card.PlayerId) || card.Pay > r.SalaryCap) continue;
            if (r.OnlyAffiliationMembers && r.TeamColors.Any(t => t.Color.Category == TeamColorCategory.Affiliation && !t.Members.Contains(card.SpId))) continue;
            if (card.OvrAt(position, 1) is null) continue;
            if (r.RankerPicksOnly && rankers?.Users(position, card.SpId) is not > 0) continue;
            foreach (var g in r.Grades)
            {
                var price = card.PriceAt(g);
                if (price <= Grades.FloorPrice || price > r.Budget) continue;
                list.Add(Make(card, position, g, r, locked: false, owned: false));
            }
        }
        // The strongest, plus the cheapest per strength band so low budgets still find squads.
        var strongest = list.OrderByDescending(c => c.Ovr + c.Premium + (c.Members != 0 ? 2 : 0)).Take(CandidatesPerSlot);
        var cheap = list.GroupBy(c => c.Ovr / 2).SelectMany(g => g.OrderBy(c => c.Price).Take(2));
        return strongest.Concat(cheap).Distinct().ToList();
    }

    private Candidate Make(MarketCard card, string position, int grade, SquadRequest r, bool locked, bool owned)
    {
        var model = modelOf(card);
        var price = card.PriceAt(grade);
        return new Candidate(card, grade, card.OvrAt(position, grade) ?? card.OvrAt(grade), model?.PremiumInOvr(card) ?? 0, price,
            ExpectedAt(model, card, grade), rankers?.Users(position, card.SpId) ?? 0, rankers?.Share(position, card.SpId) ?? 0,
            MembersOf(r, card.SpId), locked, owned);
    }

    private static int MembersOf(SquadRequest r, long spId)
    {
        var mask = 0;
        for (var i = 0; i < r.TeamColors.Count; i++)
            if (r.TeamColors[i].Members.Contains(spId)) mask |= 1 << i;
        return mask;
    }

    /// <summary>The model's expected price at its own grade, carried to another grade along the card's own grade curve.</summary>
    internal static long ExpectedAt(PriceModel? model, MarketCard card, int grade)
    {
        var price = card.PriceAt(grade);
        if (model is null) return price;
        var atModelGrade = card.PriceAt(model.Grade);
        return atModelGrade > 0 ? (long)(model.Predict(card) * price / atModelGrade) : price;
    }

    private sealed class State
    {
        public required Candidate[] Picks;
        public required HashSet<int> Players;
        public long Cost;
        public int Pay;
        public double Score;
        /// <summary>Members of each request team colour among the picks.</summary>
        public required int[] Members;
    }

    private List<SquadPlan> Search(SquadRequest r, List<List<Candidate>> candidates, double lambda, double rankerWeight)
    {
        var n = candidates.Count;
        var order = Enumerable.Range(0, n).OrderBy(i => candidates[i].Count).ToArray();
        // Cheapest completion of the slots after step k, so a partial squad that cannot finish is dropped at once.
        var minCostAfter = new long[n + 1];
        var minPayAfter = new int[n + 1];
        for (var k = n - 1; k >= 0; k--)
        {
            minCostAfter[k] = minCostAfter[k + 1] + candidates[order[k]].Min(c => c.Cost);
            minPayAfter[k] = minPayAfter[k + 1] + candidates[order[k]].Min(c => c.Card.Pay);
        }
        if (minCostAfter[0] > r.Budget) throw new InvalidOperationException($"예산이 부족합니다. 가장 싼 조합도 {Bp.Format(minCostAfter[0])}입니다.");
        if (minPayAfter[0] > r.SalaryCap) throw new InvalidOperationException($"급여 한도가 부족합니다. 가장 낮은 조합도 급여 {minPayAfter[0]}입니다.");

        double Value(Candidate c) => c.Ovr + c.Premium + rankerWeight * Math.Log2(1 + c.RankerUsers) - lambda * c.Cost;
        var bonus = new TeamColorBonus(r);
        var beam = new List<State> { new() { Picks = new Candidate[n], Players = [], Score = 0, Members = new int[r.TeamColors.Count] } };
        for (var k = 0; k < n; k++)
        {
            var slot = order[k];
            var next = new List<State>(beam.Count * 8);
            foreach (var s in beam)
            {
                foreach (var c in candidates[slot])
                {
                    if (s.Players.Contains(c.Card.PlayerId)) continue;
                    var cost = s.Cost + c.Cost;
                    var pay = s.Pay + c.Card.Pay;
                    if (cost + minCostAfter[k + 1] > r.Budget || pay + minPayAfter[k + 1] > r.SalaryCap) continue;
                    var picks = (Candidate[])s.Picks.Clone();
                    picks[slot] = c;
                    next.Add(new State
                    {
                        Picks = picks, Players = [.. s.Players, c.Card.PlayerId], Cost = cost, Pay = pay,
                        Score = s.Score + Value(c), Members = Add(s.Members, c.Members),
                    });
                }
            }
            if (next.Count == 0) throw new InvalidOperationException("예산과 급여 한도 안에서 스쿼드를 완성하지 못했습니다.");
            beam = next.OrderByDescending(s => s.Score + bonus.Total(s.Picks, s.Members)).ThenBy(s => s.Cost).Take(BeamWidth).ToList();
        }

        var plans = new List<SquadPlan>();
        foreach (var s in beam.OrderByDescending(s => s.Score + bonus.Total(s.Picks, s.Members)))
        {
            if (plans.Any(p => Differs(p, s.Picks) < 2)) continue;
            plans.Add(ToPlan(r, s, bonus));
            if (plans.Count >= r.Plans) break;
        }
        return plans;
    }

    private static int[] Add(int[] counts, int mask)
    {
        if (mask == 0) return counts;
        var next = (int[])counts.Clone();
        for (var i = 0; i < next.Length; i++)
            if ((mask & (1 << i)) != 0) next[i]++;
        return next;
    }

    /// <summary>
    /// OVR the request's team colours add per slot at each level, worked out once: a 소속 level adds to every slot of
    /// the formation, a 특성 level only to the slots its members fill.
    /// </summary>
    private sealed class TeamColorBonus
    {
        private readonly SquadRequest _r;
        private readonly double[][][] _gain; // [colour][level index][slot]
        private readonly double[][] _squadTotal; // [colour][level index]

        public TeamColorBonus(SquadRequest r)
        {
            _r = r;
            _gain = r.TeamColors.Select(t => t.Color.Levels.Select(l => r.Formation.Slots.Select(l.OvrGain).ToArray()).ToArray()).ToArray();
            _squadTotal = _gain.Select(levels => levels.Select(g => g.Sum()).ToArray()).ToArray();
        }

        public double Total(Candidate?[] picks, int[] members)
        {
            var total = 0.0;
            for (var t = 0; t < members.Length; t++)
            {
                var level = _r.TeamColors[t].Color.LevelIndexFor(members[t]);
                if (level < 0) continue;
                if (_r.TeamColors[t].Color.AppliesToSquad) { total += _squadTotal[t][level]; continue; }
                for (var i = 0; i < picks.Length; i++)
                    if (picks[i] is { } c && (c.Members & (1 << t)) != 0) total += _gain[t][level][i];
            }
            return total;
        }

        public double At(int slot, Candidate c, int[] members)
        {
            var total = 0.0;
            for (var t = 0; t < members.Length; t++)
            {
                var level = _r.TeamColors[t].Color.LevelIndexFor(members[t]);
                if (level >= 0 && (_r.TeamColors[t].Color.AppliesToSquad || (c.Members & (1 << t)) != 0)) total += _gain[t][level][slot];
            }
            return total;
        }
    }

    private static int Differs(SquadPlan plan, Candidate[] picks) =>
        picks.Count(p => plan.Slots.All(s => s.Card.PlayerId != p.Card.PlayerId));

    private static SquadPlan ToPlan(SquadRequest r, State s, TeamColorBonus bonus)
    {
        var slots = s.Picks.Select((c, i) => new SquadSlot(i, r.Formation.Slots[i], c.Card, c.Grade, c.Ovr, c.Premium, bonus.At(i, c, s.Members),
            c.Price, c.Expected, c.Card.Pay, c.RankerUsers, c.RankerShare, c.Locked, c.Owned)).ToList();
        var colors = r.TeamColors.Select((t, i) => new AppliedTeamColor(t.Color, s.Members[i], t.Color.LevelFor(s.Members[i]))).ToList();
        return new SquadPlan("", r.Mode, r.Formation, slots, colors);
    }
}

/// <summary>How many top rankers field a card at a position (daily chart), across the grades they use it at.</summary>
public sealed class RankerUsage
{
    private readonly Dictionary<(string, long), (int Users, double Share)> _byPosition = [];

    public RankerUsage(IEnumerable<RankerPick> picks)
    {
        foreach (var p in picks)
        {
            var key = (Formations.Normalize(p.Position), p.SpId);
            var (users, share) = _byPosition.GetValueOrDefault(key);
            _byPosition[key] = (users + p.Users, share + p.Share);
        }
    }

    public int Users(string position, long spId) => _byPosition.GetValueOrDefault((Formations.Normalize(position), spId)).Users;
    public double Share(string position, long spId) => _byPosition.GetValueOrDefault((Formations.Normalize(position), spId)).Share;
    public IEnumerable<(string Position, long SpId, int Users, double Share)> All() => _byPosition.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value.Users, kv.Value.Share));
}
