"""MV-14: the blend as the app would run it. A small boosted tree (a few dozen plain columns, few rounds, so a laptop fits it in
seconds) with the linear fit, with and without match usage, trading activity and a free OVR curve. Each card is priced by models
that did not see it. Same half split as the rest of MV-14.
    python tools/market/mv14_blend.py --out artifacts/mv14_blend.txt
"""
import argparse, math, os, sys
from collections import Counter
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import numpy as np
import lightgbm as lgb
from mv12 import Block, FIELD_STATS, design, fold_of, floor_of, load
from mv12_pool import unique_cards, pooled_block, colour_block, compat_block, stats_line, paired
from mv12_search import rare_block
from mv14 import match_usage, usage_block
from mv14_activity import daily, activity
from mv14_fit import curve_block, fit


def plain_columns(data, m, usage, acts):
    """No interactions: the tree finds those. Trait flags only for traits on 20+ cards."""
    share = m["colour_share"]
    traits = [t for t, n in Counter(t for c in data for t in c["traits"]).items() if n >= 20]
    fams = sorted({c["family"] for c in data})
    cols = [[c["ovr1"], c["pay"], c["weak_foot"], c["stats"].get("height", 180), c["stats"].get("weight", 75), len(c["positions"]), len(c["traits"]), len(c["colours"]),
             max((share.get(t, 0) for t in c["colours"]), default=0), math.log1p(usage[c["sp_id"]]["n"]) if c["sp_id"] in usage else 0] for c in data]
    for row, c in zip(cols, data):
        row += [c["stats"].get(s, c["ovr1"]) - c["ovr1"] for s in FIELD_STATS]
        row += [c["family"] == f for f in fams]
        row += [t in c["traits"] for t in traits]
    return np.array(cols, float)


def boosted(F, y, players, ovr, seasons, folds, lf, rounds, leaves, rate):
    pb = np.full(len(y), np.nan)
    for f in range(5):
        tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
        spec = y - 0.45 * ovr
        sums, counts = Counter(), Counter()
        for i in tr: sums[players[i]] += spec[i]; counts[players[i]] += 1
        ssum, scount = Counter(), Counter()
        for i in tr: ssum[seasons[i]] += spec[i]; scount[seasons[i]] += 1
        mean = spec[tr].mean()
        def extra(ids, loo):
            rows = []
            for i in ids:
                n, s = counts[players[i]], sums[players[i]]
                sn, ss = scount[seasons[i]], ssum[seasons[i]]
                if loo: n -= 1; s -= spec[i]; sn -= 1; ss -= spec[i]
                rows.append((s / n if n > 0 else np.nan, n, (ss + 5 * mean) / (sn + 5)))
            return np.array(rows)
        params = dict(objective="huber", alpha=0.7, learning_rate=rate, num_leaves=leaves, min_data_in_leaf=10, bagging_fraction=0.8, bagging_freq=1,
                      feature_fraction=0.5, lambda_l2=5, max_bin=32, verbosity=-1, seed=7, num_threads=4)
        model = lgb.train(params, lgb.Dataset(np.column_stack([F[tr], extra(tr, True)]), y[tr]), num_boost_round=rounds)
        pb[te] = np.maximum(model.predict(np.column_stack([F[te], extra(te, False)])), lf[te])
    return pb


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=os.path.expandvars(r"%LOCALAPPDATA%\FcHelper\fchelper.db"))
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    g = 11
    m = load(a.db)
    rng = np.random.default_rng(14)
    data = unique_cards(m, g, 115)
    y = np.log([c["prices"][g] for c in data])
    floors = np.array([floor_of(c["prices"], g) for c in data]); lf = np.log(np.maximum(floors, 1))
    cens = np.array([c["prices"][g] <= floor_of(c["prices"], g) * 1.007 for c in data])
    players = np.array([c["player"] for c in data]); ovr = np.array([c["ovr1"] for c in data], float)
    seasons = np.array([c["season"] for c in data])
    folds = np.array([fold_of(c["sp_id"]) for c in data])
    hold = np.array([(c["player"] * 2654435761) % 2**32 % 2 == 0 for c in data])
    top = ovr >= 119; free = ~cens; everyone = np.ones(len(y), bool)
    usage, _ = match_usage(a.db)
    daily_rows, _ = daily(a.db)
    acts = [activity(daily_rows.get(c["sp_id"], {}), (8, 10, 11)) for c in data]
    def rate(k): return np.array([(u[k][1] + .5) / (max(u[k][0], 0) + 2) for u in acts])
    ab = Block()
    for k in (8, 10, 11):
        ab.add(f"a{k}", rate(k), z=3); ab.add(f"a{k}_log", np.log(rate(k)), z=3)
    app = [pooled_block(data, dict(statz=300)), colour_block(data, m), rare_block(data), compat_block(data)]
    out = []
    def row(name, p, ref):
        s = f"  {name:<40} 전체 {stats_line(p, y, everyone)[0]:.1%} · 상위 {stats_line(p, y, top)[0]:.1%} · 상위(하한 제외) {stats_line(p, y, top & free)[0]:.1%}"
        if ref is not None:
            for lab, mk in (("선택", ~hold), ("확인", hold)):
                d, lo, hi = paired(p, ref, y, players, mk, rng)
                s += f" | {lab} {d:+.3f} ({lo:+.3f}~{hi:+.3f})"
        out.append(s)
        Path(a.out).parent.mkdir(parents=True, exist_ok=True)
        Path(a.out).write_text("\n".join(out), encoding="utf-8")
    def linear(blocks):
        _, X, pen = design(blocks)
        p = np.full(len(y), np.nan)
        for f in range(5):
            tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
            p[te] = fit(X, pen, y, cens, players, tr, te, floors, ovr, data)
        return p
    p0 = linear(app)
    row("앱 지금", p0, None)
    use = usage_block(data, usage, False)
    p1 = linear(app + [use]); row("+ 경기 사용량", p1, p0)
    p2 = linear(app + [use, ab]); row("+ 사용량 + 활발도", p2, p0)
    p3 = linear(app + [use, curve_block(data)]); row("+ 사용량 + OVR 곡선", p3, p0)
    F = plain_columns(data, m, usage, acts)
    Fa = np.column_stack([F, rate(8), rate(10), rate(11)])
    out.append(f"\n나무 입력 {F.shape[1] + 3}열")
    for rounds, leaves, lr in ((200, 15, .05), (100, 15, .1), (400, 15, .03)):
        b = boosted(F, y, players, ovr, seasons, folds, lf, rounds, leaves, lr)
        row(f"나무 단독 {rounds}회 잎{leaves}", b, p0)
        for w in (.25, .35, .5):
            row(f"  사용량 선형 + 나무 {w:.0%}", np.maximum((1 - w) * p1 + w * b, lf), p0)
    ba = boosted(Fa, y, players, ovr, seasons, folds, lf, 200, 15, .05)
    row("나무 + 활발도 단독", ba, p0)
    for w in (.25, .35, .5):
        row(f"  사용량·활발도 선형 + 나무(활발도) {w:.0%}", np.maximum((1 - w) * p2 + w * ba, lf), p0)
    # What the numbers look like when the trees run as the app would: seeds should not matter much.
    out.append("\n## 처음 보는 선수의 카드만 (그 선수 카드를 전혀 안 본 모델)")
    groups = np.array([(c["player"] * 2654435761) % 2**32 % 5 for c in data])
    _, X, pen = design(app + [use])
    fresh = np.full(len(y), np.nan)
    for f in range(5):
        tr, te = np.where(groups != f)[0], np.where(groups == f)[0]
        fresh[te] = fit(X, pen, y, cens, players, tr, te, floors, ovr, data)
    out.append(f"  형제 카드 없이 스펙만: 전체 {stats_line(fresh, y, everyone)[0]:.1%} · 상위 {stats_line(fresh, y, top)[0]:.1%} · 상위(하한 제외) {stats_line(fresh, y, top & free)[0]:.1%}")
    Path(a.out).write_text("\n".join(out), encoding="utf-8")


if __name__ == "__main__":
    sys.exit(main())
