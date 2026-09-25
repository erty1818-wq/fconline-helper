using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace FcHelper.Market;

/// <summary>One row of the official ranking: who, 구단가치, the main team colour of their last squad and its formation.</summary>
public sealed record RankRow(int Rank, string Nickname, long TeamValue, string? TeamColor, int TeamColorMembers, string? Formation);

/// <summary>How many top 감독모드 rankers run a team colour, and what their clubs are worth.</summary>
public sealed record ManagerPickRate(string TeamColor, int Rankers, double Share, long AverageValue, RankRow Richest, RankRow Poorest);

/// <summary>A card rankers field in a role within one team colour: how many of that colour's sampled rankers use it.</summary>
public sealed record ManagerPick(string Role, long SpId, string Name, string Season, int Grade, int Users, double Share);

/// <summary>A team colour's usual eleven in 감독모드: the most used cards per role, from rankers' latest manager matches.</summary>
public sealed record ManagerTeamPlayers(string TeamColor, int Squads, IReadOnlyDictionary<string, IReadOnlyList<ManagerPick>> ByRole);

/// <summary>
/// A card's ranker-average play in 감독모드 against its price: <see cref="Score"/> is per game, <see cref="Expected"/> the
/// score cards of that price reach on average, <see cref="Ratio"/> how far above that it is (1.3 = 30% better).
/// </summary>
public sealed record ManagerHoney(MarketCard Card, string Position, int Grade, int Ovr, long Price, FcHelper.Core.Models.RankerStatus Stats,
    double Score, double Expected)
{
    public double Ratio => Score / Expected;
    public bool IsHoney => Ratio >= 1.2;

    /// <summary>
    /// 활약 점수 [계산] per game from ranker-stats: attack = goals ×10 + assists ×7 + shots on target ×2 + other shots ×0.5 +
    /// dribbles won ×0.5 + passes completed ×0.05; defence = tackles ×2 + blocks ×3. Forwards count attack, full-backs and
    /// centre-backs defence plus a little attack, midfielders both.
    /// </summary>
    public static double PlayScore(string role, FcHelper.Core.Models.RankerStatus s)
    {
        var attack = s.Goal * 10 + s.Assist * 7 + s.EffectiveShoot * 2 + (s.Shoot - s.EffectiveShoot) * 0.5 + s.DribbleSuccess * 0.5 + s.PassSuccess * 0.05;
        var defence = s.Tackle * 2 + s.Block * 3;
        return role switch
        {
            "ST" or "CF" or "W" => attack,
            "CAM" or "SM" => attack + defence * 0.5,
            "CM" or "CDM" => attack * 0.6 + defence,
            "CB" or "FB" => defence + attack * 0.3,
            _ => attack + defence,
        };
    }
}

/// <summary>꿀선수: priced well below what the card delivers (market or play). One rule for every screen.</summary>
public static class Honey
{
    /// <summary>Trading at least 30% under the price of similar cards.</summary>
    public const double Discount = -0.3;

    public static bool IsHoney(double discount, CardLiquidity? liquidity = null) => discount <= Discount && liquidity is not { Tradable: false };

    public const string Mark = "🐝";
}

public static partial class RankingParser
{
    /// <summary>Every row of a ranking page (1vs1 or manager): the first (largest) team colour and the formation.</summary>
    public static IReadOnlyList<RankRow> Rows(string html)
    {
        var rows = new List<RankRow>();
        foreach (var chunk in html.Split("<div class=\"tr\">").Skip(1))
        {
            var rank = RankRegex().Match(chunk);
            var name = NameRegex().Match(chunk);
            if (!rank.Success || !name.Success) continue;
            var price = PriceRegex().Match(chunk);
            var color = ColorRegex().Match(chunk);
            var formation = FormationRegex().Match(chunk);
            rows.Add(new RankRow(
                int.Parse(rank.Groups[1].Value, CultureInfo.InvariantCulture),
                WebUtility.HtmlDecode(name.Groups[1].Value.Trim()),
                price.Success && long.TryParse(price.Groups[1].Value.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0,
                color.Success ? WebUtility.HtmlDecode(color.Groups[1].Value.Trim()) : null,
                color.Success ? int.Parse(color.Groups[2].Value, CultureInfo.InvariantCulture) : 0,
                formation.Success ? formation.Groups[1].Value.Trim() : null));
        }
        return rows;
    }

    /// <summary>Team colour pick rates over ranking rows (fc-info's "팀컬러 픽률", from the official ranking).</summary>
    public static IReadOnlyList<ManagerPickRate> PickRates(IReadOnlyList<RankRow> rows) =>
        rows.Where(r => r.TeamColor is not null).GroupBy(r => r.TeamColor!)
            .Select(g => new ManagerPickRate(g.Key, g.Count(), g.Count() / (double)rows.Count, (long)g.Average(r => (double)r.TeamValue),
                g.MaxBy(r => r.TeamValue)!, g.MinBy(r => r.TeamValue)!))
            .OrderByDescending(p => p.Rankers).ToList();

    [GeneratedRegex("""<span class="td rank_no">(\d+)</span>""")] private static partial Regex RankRegex();
    [GeneratedRegex("""<span class="name profile_pointer"[^>]*>([^<]+)</span>""")] private static partial Regex NameRegex();
    [GeneratedRegex(""""<span class="price"[^>]*title="([\d,]+)"""")] private static partial Regex PriceRegex();
    [GeneratedRegex("""<span class="inner">\s*([^<]+?)\s*<small>\((\d+)명\)</small>""")] private static partial Regex ColorRegex();
    [GeneratedRegex("""<span class="td formation">([^<]+)</span>""")] private static partial Regex FormationRegex();
}

public static class ManagerAnalysisMath
{
    /// <summary>
    /// The most used cards per role within each team colour, from sampled rankers' squads (the colour each ranker ran,
    /// as the ranking shows it). Share = rankers of that colour who field the card in that role.
    /// </summary>
    public static ManagerTeamPlayers TeamPlayers(string teamColor, IEnumerable<RankerSquad> squads, IReadOnlyDictionary<string, string> colorOf,
        Func<long, (string Name, string Season)?> cardOf, int perRole = 7)
    {
        var mine = squads.Where(s => colorOf.GetValueOrDefault(s.Nickname) == teamColor).ToList();
        var byRole = mine.SelectMany(s => s.Starters.Select(p => (Squad: s.Nickname, Role: RankerAllocation.RoleOf(p.Position), p.SpId, p.Grade)))
            .GroupBy(x => x.Role)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ManagerPick>)g.GroupBy(x => x.SpId)
                .Select(c =>
                {
                    var card = cardOf(c.Key);
                    var users = c.Select(x => x.Squad).Distinct().Count();
                    var grade = c.GroupBy(x => x.Grade).MaxBy(x => x.Count())!.Key;
                    return new ManagerPick(g.Key, c.Key, card?.Name ?? c.Key.ToString(CultureInfo.InvariantCulture), card?.Season ?? "", grade, users,
                        mine.Count == 0 ? 0 : users / (double)mine.Count);
                })
                .OrderByDescending(p => p.Users).Take(perRole).ToList());
        return new ManagerTeamPlayers(teamColor, mine.Count, byRole);
    }
}
