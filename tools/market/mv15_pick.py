"""MV-15: the app run showed the top cards with traits getting worse where the first test did not, so medians alone cannot choose.
Pairs every card's error under each set of usage columns against the app now, on each part of the market, with footballer-resampled
intervals and more than one tree seed. Read-only.
    python tools/market/mv15_pick.py --db <copy of fchelper.db> --out artifacts/mv15_pick.txt
"""
import argparse, math, os, sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import numpy as np
import lightgbm as lgb
from collections import Counter
from mv12 import Block, design, fold_of, floor_of, load
from mv12_pool import unique_cards, pooled_block, colour_block, compat_block, stats_line, paired
from mv12_search import rare_block
from mv14 import usage_block, match_usage
from mv14_blend import plain_columns
from mv14_fit import fit
from mv15 import TREE_WEIGHT
from mv15_grade import read_matches, count, columns


def forest(F, y, players, ovr, seasons, tr, te, lf, seed):
    spec = y - 0.45 * ovr
    sums, counts, ssum, scount = Counter(), Counter(), Counter(), Counter()
    for i in tr:
        sums[players[i]] += spec[i]; counts[players[i]] += 1
        ssum[seasons[i]] += spec[i]; scount[seasons[i]] += 1
    mean = spec[tr].mean()
    def extra(ids, loo):
        rows = []
        for i in ids:
            n, s, sn, ss = counts[players[i]], sums[players[i]], scount[seasons[i]], ssum[seasons[i]]
            if loo: n -= 1; s -= spec[i]; sn -= 1; ss -= spec[i]
            rows.append((s / n if n > 0 else np.nan, n, (ss + 5 * mean) / (sn + 5)))
        return np.array(rows)
    params = dict(objective="huber", alpha=0.7, learning_rate=.05, num_leaves=15, min_data_in_leaf=10, bagging_fraction=0.8, bagging_freq=1,
                  feature_fraction=0.5, lambda_l2=5, max_bin=32, verbosity=-1, seed=seed, num_threads=4)
    model = lgb.train(params, lgb.Dataset(np.column_stack([F[tr], extra(tr, True)]), y[tr]), num_boost_round=200)
    return np.maximum(model.predict(np.column_stack([F[te], extra(te, False)])), lf[te])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=os.path.expandvars(r"%LOCALAPPDATA%\FcHelper\fchelper.db"))
    ap.add_argument("--out", required=True)
    ap.add_argument("--min-playable", type=int, default=113)
    a = ap.parse_args()
    g = 11
    m = load(a.db)
    rng = np.random.default_rng(15)
    data = unique_cards(m, g, 115)
    y = np.log([c["prices"][g] for c in data])
    floors = np.array([floor_of(c["prices"], g) for c in data]); lf = np.log(np.maximum(floors, 1))
    cens = np.array([c["prices"][g] <= floor_of(c["prices"], g) * 1.007 for c in data])
    players = np.array([c["player"] for c in data]); ovr = np.array([c["ovr1"] for c in data], float)
    seasons = np.array([c["season"] for c in data])
    folds = np.array([fold_of(c["sp_id"]) for c in data])
    hold = np.array([(c["player"] * 2654435761) % 2**32 % 2 == 0 for c in data])
    usage, _ = match_usage(a.db)
    h = columns(data, count(read_matches(a.db)))
    has = (h["n"] > 0).astype(float)
    ranker = np.array([m["use_card"][c["sp_id"]] for c in data], float)
    logr = np.log1p(ranker)
    use = h["n"] + ranker
    no_trait = np.array([not c["traits"] for c in data]); free = ~cens
    rank = np.argsort(-use)
    def top_mask(n):
        mk = np.zeros(len(y), bool); mk[rank[:n]] = True
        return mk
    parts = {"전체": np.ones(len(y), bool), "실사용": ovr >= a.min_playable, "120+ 특성있음": (ovr >= 120) & free & ~no_trait, "120+ 특성없음": (ovr >= 120) & free & no_trait,
             "많이쓰임300": top_mask(300), "많이쓰임1000": top_mask(1000)}
    out = [f"카드 {len(y):,}장 · 실사용 기준 +1 OVR {a.min_playable}"]
    out += [f"  {k}: {v.sum():,}장" for k, v in parts.items()]
    def save():
        Path(a.out).parent.mkdir(parents=True, exist_ok=True)
        Path(a.out).write_text("\n".join(out), encoding="utf-8")
    base = [pooled_block(data, dict(statz=300)), colour_block(data, m), rare_block(data), compat_block(data), usage_block(data, usage, False)]
    F0 = plain_columns(data, m, usage, None)
    def block(pairs, z=30):
        b = Block()
        for k, v in pairs: b.add(k, v, z=z)
        return b
    n11 = [("n11", h["log11"]), ("n11_any", h["n11"] > 0)]
    share = [("share11", h["share11"] * has)]
    rk = [("r", logr), ("r_any", ranker > 0)]
    variants = {"+11 사용": (n11, [h["log11"]]), "+11 몫": (share, [share[0][1]]), "랭커": (rk, [logr]), "+11 사용·몫": (n11 + share, [h["log11"], share[0][1]]),
                "+11 사용·랭커": (n11 + rk, [h["log11"], logr]), "셋 다": (n11 + share + rk, [h["log11"], share[0][1], logr])}
    def linear(pairs):
        _, X, pen = design(base + ([block(pairs)] if pairs else []))
        p = np.full(len(y), np.nan)
        for f in range(5):
            tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
            p[te] = fit(X, pen, y, cens, players, tr, te, floors, ovr, data)
        return p
    def trees(cols, seed):
        F1 = np.column_stack([F0] + cols) if cols else F0
        p = np.full(len(y), np.nan)
        for f in range(5):
            tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
            p[te] = forest(F1, y, players, ovr, seasons, tr, te, lf, seed)
        return p
    seeds = (7, 11, 23)
    l0 = linear([])
    ref = {s: np.maximum((1 - TREE_WEIGHT) * l0 + TREE_WEIGHT * trees([], s), lf) for s in seeds}
    out.append("\n## 앱 지금 (씨앗 3개 평균의 오차 중앙값)")
    out.append("  " + " · ".join(f"{k} {np.mean([stats_line(ref[s], y, v)[0] for s in seeds]):.1%}" for k, v in parts.items()))
    save()
    for name, (pairs, cols) in variants.items():
        l1 = linear(pairs)
        ps = {s: np.maximum((1 - TREE_WEIGHT) * l1 + TREE_WEIGHT * trees(cols, s), lf) for s in seeds}
        out.append(f"\n## {name}")
        out.append("  오차 " + " · ".join(f"{k} {np.mean([stats_line(ps[s], y, v)[0] for s in seeds]):.1%}" for k, v in parts.items()))
        pbar = np.mean([ps[s] for s in seeds], axis=0); rbar = np.mean([ref[s] for s in seeds], axis=0)
        for k, v in parts.items():
            d0, lo0, hi0 = paired(pbar, rbar, y, players, v & ~hold, rng)
            d1, lo1, hi1 = paired(pbar, rbar, y, players, v & hold, rng)
            out.append(f"  {k:<10} 짝 비교 선택 {d0:+.3f} ({lo0:+.3f}~{hi0:+.3f}) · 확인 {d1:+.3f} ({lo1:+.3f}~{hi1:+.3f})")
        save()


if __name__ == "__main__":
    sys.exit(main())
