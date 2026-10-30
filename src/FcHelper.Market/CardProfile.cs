namespace FcHelper.Market;

/// <summary>How the official ranker chart fields a card: at this position and grade, this many users.</summary>
public sealed record CardRankerUse(string Position, int Grade, int Ovr, int Users, double Share);

/// <summary>A card fielded next to the looked-at one in the sampled rankers' elevens.</summary>
public sealed record CardTeammate(MarketCard Card, int Grade, int Together);

/// <summary>What the sampled rankers' elevens (about 100 teams, latest official match) do with a card.</summary>
public sealed record CardSquadUse(int Squads, int Sample, IReadOnlyList<(string Position, int Grade, int Count)> Slots, IReadOnlyList<CardTeammate> Teammates);

/// <summary>A team-colour partner: a fellow member worth fielding with the card, by role.</summary>
public sealed record ColorPartner(MarketCard Card, string Role, int Grade, int Ovr, long Price, long Expected, int RankerUsers, int SquadUses)
{
    /// <summary>Price against the model's expectation: −0.4 = 40% under. [추정]</summary>
    public double Discount => Expected > 0 ? Price / (double)Expected - 1 : 0;
}

/// <summary>A team colour a card counts for, with how much of the ranker chart fields it (0 when unknown).</summary>
public sealed record CardColor(TeamColor Color, double RankerShare);

/// <summary>Everything the card detail window shows beyond the card itself. Each part is official data or derived from it.</summary>
public sealed record CardProfile(MarketCard Card, IReadOnlyList<CardColor> Colors, IReadOnlyList<CardRankerUse> RankerUses, CardSquadUse Squads,
    IReadOnlyList<MarketCard> OtherCards, IReadOnlyDictionary<long, int> RankerUsers, string? ChartDate);

/// <summary>A card like the looked-at one for less: same role, about the same OVR.</summary>
public sealed record SimilarCard(MarketCard Card, int Ovr, long Price, long Expected, bool SameTrait);

/// <summary>A card the user watches at one grade; TargetPrice 0 means no alert.</summary>
public sealed record FavoriteCard(long SpId, int Grade, long TargetPrice);

public static class CardProfiles
{
    private static readonly string[] RoleOrder = ["ST", "CF", "W", "SM", "CAM", "CM", "CDM", "FB", "CB", "GK"];

    public static IReadOnlyList<CardRankerUse> RankerUses(long spId, IEnumerable<RankerPick> picks) =>
        picks.Where(p => p.SpId == spId).OrderByDescending(p => p.Users).Select(p => new CardRankerUse(p.Position, p.Grade, p.Ovr, p.Users, p.Share)).ToList();

    /// <summary>The footballer's other cards in the market, best first; a card listed by several positions counts once.</summary>
    public static IReadOnlyList<MarketCard> OtherCards(MarketCard card, IEnumerable<MarketCard> pool) =>
        pool.Where(c => c.PlayerId == card.PlayerId && c.SpId != card.SpId).DistinctBy(c => c.SpId).OrderByDescending(c => c.Ovr1).ThenBy(c => c.Season).ToList();

    public static CardSquadUse SquadUse(long spId, IReadOnlyList<RankerSquad> squads, IReadOnlyDictionary<long, MarketCard> cards, int top = 10)
    {
        var mine = squads.Where(s => s.Starters.Any(p => p.SpId == spId)).ToList();
        var slots = mine.SelectMany(s => s.Starters.Where(p => p.SpId == spId))
            .GroupBy(p => (p.Position, p.Grade)).OrderByDescending(g => g.Count()).Select(g => (g.Key.Position, g.Key.Grade, g.Count())).ToList();
        var teammates = mine.SelectMany(s => s.Starters.Where(p => p.SpId != spId))
            .GroupBy(p => p.SpId).Where(g => cards.ContainsKey(g.Key)).OrderByDescending(g => g.Count()).Take(top)
            .Select(g => new CardTeammate(cards[g.Key], g.GroupBy(p => p.Grade).MaxBy(x => x.Count())!.Key, g.Count())).ToList();
        return new CardSquadUse(mine.Count, squads.Count, slots, teammates);
    }

    /// <summary>
    /// Cheaper cards for the same role within one OVR of this card (or better), other footballers only. Same trait first,
    /// then the highest OVR, then the cheapest.
    /// </summary>
    public static IReadOnlyList<SimilarCard> Similar(MarketCard card, IEnumerable<MarketCard> pool, int grade, Func<MarketCard, PriceModel?> modelOf, int top = 15)
    {
        var role = RankerAllocation.RoleOf(card.MainPosition);
        var ovr = card.OvrAt(grade);
        var price = card.PriceAt(grade);
        var traits = card.Tags.Where(t => t.StartsWith("trait:")).ToHashSet();
        return pool.Where(c => c.PlayerId != card.PlayerId && c.IsTraded && c.PriceAt(grade) > Grades.FloorPrice && c.PriceAt(grade) < price
                && c.OvrAt(grade) >= ovr - 1 && RankerAllocation.RoleOf(c.MainPosition) == role)
            .DistinctBy(c => c.SpId)
            .Select(c => new SimilarCard(c, c.OvrAt(grade), c.PriceAt(grade), modelOf(c)?.ExpectedAt(c, grade) ?? c.PriceAt(grade), c.Tags.Any(traits.Contains)))
            .OrderByDescending(s => s.SameTrait).ThenByDescending(s => s.Ovr).ThenBy(s => s.Price).Take(top).ToList();
    }

    /// <summary>Watched cards at or under their target price.</summary>
    public static IReadOnlyList<(FavoriteCard Favorite, MarketCard Card, long Price)> TargetsReached(IEnumerable<FavoriteCard> favorites, Func<long, MarketCard?> cardOf) =>
        favorites.Where(f => f.TargetPrice > 0)
            .Select(f => (Favorite: f, Card: cardOf(f.SpId)))
            .Where(x => x.Card is not null && x.Card.PriceAt(x.Favorite.Grade) is > 0 and var p && p <= x.Favorite.TargetPrice)
            .Select(x => (x.Favorite, x.Card!, x.Card!.PriceAt(x.Favorite.Grade))).ToList();

    /// <summary>
    /// Fellow members of a team colour worth fielding with the card, per role: what rankers actually field first, then the
    /// cheapest against the model. Cards far under the card's own level are left out; the footballer himself too.
    /// </summary>
    public static IReadOnlyList<ColorPartner> Partners(MarketCard card, IReadOnlySet<long> members, IEnumerable<MarketCard> pool, int grade,
        Func<MarketCard, PriceModel?> modelOf, IEnumerable<RankerPick> picks, IReadOnlyList<RankerSquad> squads, int perRole = 5)
    {
        var users = picks.GroupBy(p => p.SpId).ToDictionary(g => g.Key, g => g.Sum(p => p.Users));
        var fielded = squads.SelectMany(s => s.Starters).GroupBy(p => p.SpId).ToDictionary(g => g.Key, g => g.Count());
        var floor = Math.Min(card.OvrAt(grade) - 6, MarketDefaults.MinOvr(grade));
        return pool.Where(c => members.Contains(c.SpId) && c.PlayerId != card.PlayerId && c.IsTraded && c.PriceAt(grade) > Grades.FloorPrice && c.OvrAt(grade) >= floor)
            .DistinctBy(c => c.SpId)
            .Select(c =>
            {
                var price = c.PriceAt(grade);
                var expected = modelOf(c)?.ExpectedAt(c, grade) ?? price;
                return new ColorPartner(c, RankerAllocation.RoleOf(c.MainPosition), grade, c.OvrAt(grade), price, expected, users.GetValueOrDefault(c.SpId), fielded.GetValueOrDefault(c.SpId));
            })
            .GroupBy(p => p.Role)
            .OrderBy(g => Array.IndexOf(RoleOrder, g.Key))
            .SelectMany(g => g.OrderByDescending(p => p.RankerUsers + p.SquadUses).ThenBy(p => p.Discount).Take(perRole))
            .ToList();
    }
}
