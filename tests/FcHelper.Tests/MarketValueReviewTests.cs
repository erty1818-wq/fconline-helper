using FcHelper.Market;

namespace FcHelper.Tests;

/// <summary>
/// Reproductions of the MV-04 review findings (docs/market-value/REVIEW-CLAUDE.md). They fail on the model as of
/// c8cb18f and are skipped so CI stays green; MV-05 removes the Skip once the fix is in.
/// The market is synthetic with a known truth: every footballer has several season cards and a footballer-level
/// "name premium" the model cannot see, which is what the real market looks like.
/// </summary>
public class MarketValueReviewTests
{
    private const string Pending = "MV-04 finding, fixed in MV-05 (docs/market-value/REVIEW-CLAUDE.md)";

    private static double Normal(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

    /// <summary>
    /// 250 footballers with 2-6 season cards each. ln(price) = 1.4× per OVR + 0.3 for two good feet + name premium
    /// (sd 0.5, shared by a footballer's cards) + season + noise; <paramref name="boosted"/> footballers' cards get
    /// <paramref name="boost"/> on top. spId = season id × 1,000,000 + footballer, as in the game.
    /// </summary>
    internal static List<MarketCard> Market(int seed, IReadOnlySet<int>? boosted = null, double boost = 0)
    {
        var r = new Random(seed);
        var cards = new List<MarketCard>();
        string[] seasons = ["S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8"];
        for (var p = 0; p < 250; p++)
        {
            var skill = 112 + r.Next(0, 12);
            var name = Normal(r) * 0.5;
            foreach (var s in seasons.OrderBy(_ => r.Next()).Take(2 + r.Next(0, 5)))
            {
                var season = Array.IndexOf(seasons, s);
                var ovr = skill + season / 2 + r.Next(-1, 2);
                var wf = 3 + r.Next(0, 3);
                var ln = Math.Log(1e8) + Math.Log(1.4) * (ovr - 110) + (wf == 5 ? 0.3 : 0) + name + 0.05 * season + Normal(r) * 0.25
                    + (boosted?.Contains(p) == true ? boost : 0);
                cards.Add(new MarketCard
                {
                    Group = "W", SpId = (100L + season) * 1_000_000 + p, Name = $"p{p}", Season = s, Pay = 20 + ovr / 10, Ovr1 = ovr,
                    WeakFoot = wf, RatingCount = 50, Prices = new Dictionary<int, long> { [1] = 1000, [8] = (long)Math.Exp(ln) },
                    Stats = new Dictionary<string, int> { ["sprintspeed"] = ovr + r.Next(-6, 7) },
                });
            }
        }
        return cards;
    }

    private static int Footballer(MarketCard c) => (int)(c.SpId % 1_000_000);

    [Fact(Skip = Pending)]
    public void Team_colour_membership_is_not_counted_as_playing_premium()
    {
        // The market pays +50% for the cards of 42 footballers because they unlock a team colour. Squads add the team
        // colour's real bonus separately (SquadSlot.TeamColorBonus), so the price premium must not also be played OVR.
        var colour = Enumerable.Range(0, 250).Where(p => p % 6 == 0).ToHashSet();
        var cards = Market(1, colour, Math.Log(1.5));
        var members = cards.Where(c => colour.Contains(Footballer(c))).Select(c => c.SpId).ToHashSet();
        var model = PriceModel.Fit("W", 8, cards, [new("tc:aff:9", "소속 팀컬러", FactorKind.TeamColor, members)])!;
        var member = cards.First(c => members.Contains(c.SpId));
        var twin = member with { SpId = 999_000_001 }; // the same card without the membership

        Assert.True(model.Factors().Single(f => f.Key == "tc:aff:9").Clear); // the price premium itself is real…
        Assert.Equal(model.PremiumInOvr(twin), model.PremiumInOvr(member), 3); // …but it is not playing value
    }

    [Fact(Skip = Pending)]
    public void A_team_colour_of_few_footballers_is_not_called_clear_by_chance()
    {
        // No true effect. Cards of 6 footballers (~24 cards) share their name premiums, so card-level robust errors call
        // it "clear" about 40% of the time. Footballer-clustered errors bring it to ~10% (few clusters run a little hot,
        // hence the margin); a t(G-1) critical value or a minimum number of footballers gets it closer to 5%.
        var clear = 0;
        const int runs = 60;
        for (var seed = 0; seed < runs; seed++)
        {
            var cards = Market(seed);
            var pick = new Random(1000 + seed);
            var six = Enumerable.Range(0, 250).OrderBy(_ => pick.Next()).Take(6).ToHashSet();
            var members = cards.Where(c => six.Contains(Footballer(c))).Select(c => c.SpId).ToHashSet();
            var model = PriceModel.Fit("W", 8, cards, [new("tc:aff:1", "6명 팀컬러", FactorKind.TeamColor, members)])!;
            if (model.Factors().Single(f => f.Key == "tc:aff:1").Clear) clear++;
        }
        Assert.InRange(clear / (double)runs, 0, 0.15);
    }
}
