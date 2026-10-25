"""Run with: python -m unittest discover -s tools/market -p test_mv10.py"""
import math
import unittest
import numpy as np
import mv10


class TobitAnalysisTests(unittest.TestCase):
    def test_single_card_footballers_cannot_identify_a_within_player_trait_effect(self):
        cards = []
        for player in range(100):
            for season in range(3):
                ovr = 111 + (player + season*3) % 14
                has = (player + season) % 3 == 0
                price = round(math.exp(18 + .3*(ovr-115) + .2*season + player%2 + .55*has))
                cards.append(dict(sp_id=(300+season)*1_000_000+player, player=player, season=f"S{season}",
                    ovr1=ovr,pay=25,weak_foot=3,traits={"trait"} if has else set(),prices={10:1000,11:price}))
        before = mv10.effects(mv10.fit(cards,11,"constant"),cards)[0]["log_premium"]
        # Expensive favourites with only one card all hold the trait. Their price differences have to be absorbed.
        singletons = [dict(cards[i], sp_id=400_000_000+1000+i, player=1000+i, traits={"trait"},
                           prices={10:1000,11:cards[i]["prices"][11]*100}) for i in range(80)]
        after = mv10.effects(mv10.fit(cards+singletons,11,"constant"),cards+singletons)[0]["log_premium"]
        self.assertAlmostEqual(before,after,delta=.002)

    def test_same_player_tobit_recovers_a_known_premium_under_censoring(self):
        rng = np.random.default_rng(41)
        cards = []
        for player in range(250):
            popular = player % 3 == 0
            for season in range(4):
                ovr = int(rng.integers(110, 126))
                has = rng.random() < (.7 if popular else .3)
                log = 18 + .3*(ovr-115) + .2*season + (1 if popular else 0) + .6*has + rng.normal(0, .25)
                lower = round(math.exp(18 + .3*(ovr-115) + rng.normal(0, .8))/1.55)
                cards.append(dict(sp_id=(300+season)*1_000_000+player,player=player,season=f"S{season}",ovr1=ovr,
                    pay=25,weak_foot=3,traits={"trait"} if has else set(),
                    prices={10:lower,11:max(round(math.exp(log)),round(lower*1.55))}))
        model = mv10.fit(cards,11,shape="constant")
        premium = mv10.effects(model,cards)[0]
        self.assertGreater(sum(mv10.at_floor(c["prices"],11) for c in cards),100)
        self.assertAlmostEqual(premium["log_premium"],.6,delta=.10)
        self.assertGreater(premium["comparisons"],100)

    def test_grade11_floor_and_top_prediction_are_censored_in_the_correct_direction(self):
        self.assertTrue(mv10.at_floor({10:20_000_000,11:31_200_000},11))
        self.assertFalse(mv10.at_floor({10:20_000_000,11:31_300_000},11))
        self.assertFalse(mv10.at_floor({11:31_000_000},11))
        self.assertAlmostEqual(float(mv10.mills(np.array([0]))[0]),math.sqrt(2/math.pi),places=6)
        self.assertGreater(float(mv10.mills(np.array([-40]))[0]),40)


if __name__ == "__main__":
    unittest.main()
