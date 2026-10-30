"""MV-15: the player's way of pricing. Take the cards people actually use in a position as the reference, then price a card by how
far its stats sit above them, with the price rising faster the further above it goes. Also prices a card from comparable cards
of other footballers, the way a player compares. Chosen on half the footballers, confirmed on the other half, on top of the
blend the app runs now. Needs brotli and lightgbm. Read-only.
    python tools/market/mv15.py --db <copy of fchelper.db> --out artifacts/mv15.txt
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
from mv14_blend import plain_columns
from mv14_fit import fit

TREE_WEIGHT = 0.3


def weighted_median(values, weights):
    order = np.argsort(values)
    v, w = np.asarray(values, float)[order], np.asarray(weights, float)[order]
    return float(v[np.searchsorted(np.cumsum(w), w.sum() / 2)])


def references(cards, weight):
    """Per position: the median OVR and median of each stat over the cards in use, weighted by use."""
    out = {}
    for f in sorted({c["family"] for c in cards}):
        used = [c for c in cards if c["family"] == f and weight(c) > 0]
        w = [weight(c) for c in used]
        out[f] = dict(ovr=weighted_median([c["ovr1"] for c in used], w), n=len(used), uses=sum(w),
                      stats={s: weighted_median([c["stats"].get(s, c["ovr1"]) for c in used], w) for s in FIELD_STATS})
    return out


def gap_block(data, gap, knots, z):
    b = Block()
    b.add("below", np.maximum(0, -gap), z=z)
    for k in knots:
        b.add(f"above{k}", np.maximum(0, gap - k), z=z)
    b.add("above_sq", np.maximum(0, gap) ** 2, z=z)
    fams = sorted({c["family"] for c in data})
    for f in fams:
        h = np.array([c["family"] == f for c in data], float)
        b.add(f"above×{f}", h * np.maximum(0, gap), z=z * 10)
        b.add(f"above_sq×{f}", h * np.maximum(0, gap) ** 2, z=z * 10)
    return b


def share_block(data, weight, z):
    """How much of the position's use goes to cards as good as this one or better: few alternatives, a scarce card."""
    by = {}
    for c in data:
        by.setdefault(c["family"], []).append((c["ovr1"], weight(c)))
    above, count = [], []
    for c in data:
        rows = by[c["family"]]
        total = sum(w for _, w in rows) or 1
        above.append(sum(w for o, w in rows if o >= c["ovr1"]) / total)
        count.append(sum(1 for o, _ in rows if o > c["ovr1"]))
    b = Block()
    b.add("share_log", np.log(np.maximum(above, 1e-4)), z=z)
    b.add("better_log", np.log1p(count), z=z)
    return b


def stat_gaps(data, refs):
    return np.array([[c["stats"].get(s, c["ovr1"]) - refs[c["family"]]["stats"][s] for s in FIELD_STATS] for c in data], float)


def stat_summary_block(gaps, z):
    b = Block()
    above = np.maximum(0, gaps)
    b.add("stats_above_sum", above.sum(1), z=z)
    b.add("stats_above_sq", (above ** 2).sum(1), z=z)
    b.add("stats_below_sum", np.maximum(0, -gaps).sum(1), z=z)
    b.add("stats_above_n", (gaps > 0).sum(1), z=z)
    return b


def stat_full_block(data, gaps, z):
    b = Block()
    fams = sorted({c["family"] for c in data})
    for j, s in enumerate(FIELD_STATS):
        for f in fams:
            h = np.array([c["family"] == f for c in data], float)
            b.add(f"above:{s}×{f}", h * np.maximum(0, gaps[:, j]), z=z)
    return b


def comparable(data, y, cens, players, train, sv, use, tau, slope, weights=None):
    """Price each card from the training cards of other footballers in its position with the most alike stats, moved along the
    OVR slope. Training rows never use themselves, so the column means the same on both sides."""
    fam = np.array([c["family"] for c in data])
    ovr = np.array([c["ovr1"] for c in data], float)
    pool = train[~cens[train]]
    out = np.full(len(y), np.nan)
    for i in range(len(y)):
        p = pool[(fam[pool] == fam[i]) & (players[pool] != players[i])]
        if len(p) == 0: continue
        d = np.sqrt(((sv[p] - sv[i]) ** 2).mean(1))
        w = np.exp(-d / tau) * (1 + use[p])
        if w.sum() < 1e-9: continue
        out[i] = (w * (y[p] + slope * (ovr[i] - ovr[p]))).sum() / w.sum()
    return out


def boosted_fold(F, y, players, ovr, seasons, tr, te, lf):
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
    params = dict(objective="huber", alpha=0.7, learning_rate=.05, num_leaves=15, min_data_in_leaf=10, bagging_fraction=0.8, bagging_freq=1,
                  feature_fraction=0.5, lambda_l2=5, max_bin=32, verbosity=-1, seed=7, num_threads=4)
    model = lgb.train(params, lgb.Dataset(np.column_stack([F[tr], extra(tr, True)]), y[tr]), num_boost_round=200)
    return np.maximum(model.predict(np.column_stack([F[te], extra(te, False)])), lf[te])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=os.path.expandvars(r"%LOCALAPPDATA%\FcHelper\fchelper.db"))
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    g = 11
    m = load(a.db)
    rng = np.random.default_rng(15)
    data = unique_cards(m, g, 115)
    wide = unique_cards(m, g, 100)
    y = np.log([c["prices"][g] for c in data])
    floors = np.array([floor_of(c["prices"], g) for c in data]); lf = np.log(np.maximum(floors, 1))
    cens = np.array([c["prices"][g] <= floor_of(c["prices"], g) * 1.007 for c in data])
    players = np.array([c["player"] for c in data]); ovr = np.array([c["ovr1"] for c in data], float)
    seasons = np.array([c["season"] for c in data])
    folds = np.array([fold_of(c["sp_id"]) for c in data])
    hold = np.array([(c["player"] * 2654435761) % 2**32 % 2 == 0 for c in data])
    usage, matches = match_usage(a.db)
    def use_of(c):
        u = usage.get(c["sp_id"])
        return (u["n"] if u else 0) + m["use_card"][c["sp_id"]]
    def use11(c):
        u = usage.get(c["sp_id"])
        return (u["n11"] if u else 0) + m["use_card"][c["sp_id"]]
    refs = references(wide, use_of)
    gap = ovr - np.array([refs[c["family"]]["ovr"] for c in data])
    use = np.array([use_of(c) for c in data], float)
    out = []
    def save():
        Path(a.out).parent.mkdir(parents=True, exist_ok=True)
        Path(a.out).write_text("\n".join(out), encoding="utf-8")
    out.append(f"카드 {len(y):,}장 · 경기 {matches:,} · 하한 {cens.mean():.0%}\n\n## 포지션별 기준 카드 (많이 쓰인 만큼 가중한 중위, +1 OVR)")
    for f, r in refs.items():
        at = np.array([c["family"] == f for c in data]) & (np.abs(gap) <= 1) & ~cens
        price = np.median(y[at]) if at.any() else float("nan")
        out.append(f"  {f}: 기준 OVR {r['ovr']:.0f} · 사용 카드 {r['n']:,}장 · 사용 {r['uses']:,}회 · 기준±1 카드 {at.sum()}장, 중앙 시세 {math.exp(price) / 1e8:,.0f}억")
    out.append("\n## 기준에서 멀어질수록 값이 가팔라지나 (같은 선수의 카드끼리, OVR 1당 로그 시세, 하한 제외)")
    pairs = []
    for p in np.unique(players):
        ids = np.where((players == p) & ~cens)[0]
        for i in ids:
            for j in ids:
                if ovr[j] - ovr[i] >= 1 and ovr[j] - ovr[i] <= 4 and data[i]["family"] == data[j]["family"]:
                    pairs.append((gap[i], (gap[i] + gap[j]) / 2, (y[j] - y[i]) / (ovr[j] - ovr[i])))
    pairs = np.array(pairs)
    for lo, hi in ((-20, -3), (-3, 0), (0, 2), (2, 4), (4, 6), (6, 9), (9, 40)):
        s = pairs[(pairs[:, 1] >= lo) & (pairs[:, 1] < hi)]
        if len(s) >= 10: out.append(f"  기준 {lo:+d}~{hi:+d}: 짝 {len(s):,} · OVR 1당 중앙 {math.exp(np.median(s[:, 2])) - 1:+.0%} (사분위 {math.exp(np.percentile(s[:, 2], 25)) - 1:+.0%}~{math.exp(np.percentile(s[:, 2], 75)) - 1:+.0%})")
    save()

    app = [pooled_block(data, dict(statz=300)), colour_block(data, m), rare_block(data), compat_block(data), usage_block(data, usage, False)]
    F = plain_columns(data, m, usage, None)
    sv = np.array([[c["stats"].get(s, c["ovr1"]) for s in FIELD_STATS] for c in data], float)
    logu = np.log1p(use)
    def run(blocks, tree_cols=None, comps=None, weight=TREE_WEIGHT):
        """The app's blend. comps(fold) gives fold-dependent columns for both parts."""
        _, X, pen = design(app + blocks)
        F1 = F if tree_cols is None else np.column_stack([F, tree_cols])
        pl = np.full(len(y), np.nan); pt = np.full(len(y), np.nan)
        for f in range(5):
            tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
            if comps is None:
                Xf, pf, Ff = X, pen, F1
            else:
                c = comps(tr)
                Xf = np.column_stack([X, c]); pf = pen + [("z", 3)] * c.shape[1]; Ff = np.column_stack([F1, c])
            pl[te] = fit(Xf, pf, y, cens, players, tr, te, floors, ovr, data)
            pt[te] = boosted_fold(Ff, y, players, ovr, seasons, tr, te, lf)
        return np.maximum((1 - weight) * pl + weight * pt, lf)
    no_trait = np.array([not c["traits"] for c in data]); top = ovr >= 119; free = ~cens
    everyone = np.ones(len(y), bool)
    def errs(p):
        return (f"전체 {stats_line(p, y, everyone)[0]:.1%} · 상위(하한 제외) {stats_line(p, y, top & free)[0]:.1%} · 특성 없음 {stats_line(p, y, top & free & no_trait)[0]:.1%} · 특성 있음 {stats_line(p, y, top & free & ~no_trait)[0]:.1%}")
    def row(name, p, ref):
        s = f"  {name:<28} {errs(p)}"
        if ref is not None:
            for lab, mk in (("선택", ~hold), ("확인", hold)):
                d, lo, hi = paired(p, ref, y, players, mk, rng)
                s += f" | {lab} {d:+.3f} ({lo:+.3f}~{hi:+.3f})"
        out.append(s); save()
    p0 = run([])
    out.append("\n## 기준과의 거리별 지금 오차 (최상위가 원래 어렵나, 자주 쓰는 카드는 맞나)")
    for lo, hi in ((-40, -3), (-3, 0), (0, 3), (3, 6), (6, 9), (9, 40)):
        mk = (gap >= lo) & (gap < hi)
        if mk.sum() >= 10: out.append(f"  기준 {lo:+d}~{hi:+d}: 전체 {mk.sum():,}장 {stats_line(p0, y, mk)[0]:.1%} · 하한 제외 {(mk & free).sum():,}장 {stats_line(p0, y, mk & free)[0]:.1%} · 잔차 표준편차 {(y - p0)[mk & free].std():.2f}")
    rank = np.argsort(-use)
    for n in (50, 100, 300, 1000):
        mk = np.zeros(len(y), bool); mk[rank[:n]] = True
        out.append(f"  많이 쓰이는 {n}장: 전체 {stats_line(p0, y, mk)[0]:.1%} · 하한 제외 {(mk & free).sum():,}장 {stats_line(p0, y, mk & free)[0]:.1%}")
    out.append(f"  경기·랭커 기록에 안 나온 카드 {(use == 0).sum():,}장: {stats_line(p0, y, use == 0)[0]:.1%}")
    out.append("\n## 시험 (앱 지금 = 선형 + 나무 30%)")
    row("앱 지금", p0, None)
    # Same-footballer slopes also come out of the comparison alone.
    def comp_alone(tau, slope):
        p = np.full(len(y), np.nan)
        for f in range(5):
            tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
            p[te] = comparable(data, y, cens, players, tr, sv, logu, tau, slope)[te]
        return np.maximum(np.where(np.isnan(p), np.nanmedian(y), p), lf)
    for tau, slope in ((1.5, .3), (1.5, .6), (3.0, .3), (3.0, .6)):
        row(f"다른 선수 비슷한 카드만 τ{tau} 기울기{slope}", comp_alone(tau, slope), None)
    gap_cols = np.column_stack([gap, np.maximum(0, gap), np.maximum(0, gap) ** 2])
    row("기준 거리 곡선", run([gap_block(data, gap, (0, 3, 6), 3)], gap_cols), p0)
    row("기준 거리 곡선 약하게 z=30", run([gap_block(data, gap, (0, 3, 6), 30)], gap_cols), p0)
    sb = share_block(data, use_of, 3)
    row("내 위에 있는 사용 몫·카드 수", run([sb], np.column_stack(sb.cols)), p0)
    sg = stat_gaps(data, refs)
    ss = stat_summary_block(sg, 3)
    row("능력치별 기준 초과 요약", run([ss], np.column_stack(ss.cols)), p0)
    row("능력치별 기준 초과 전부", run([stat_full_block(data, sg, 100)], np.maximum(0, sg)), p0)
    def comps(tr):
        c = comparable(data, y, cens, players, tr, sv, logu, 3.0, .45)
        return np.where(np.isnan(c), np.nanmedian(c), c)[:, None]
    row("비슷한 카드 시세 열", run([], None, comps), p0)
    # Heavier trees can read the new columns: the earlier weight was picked without them.
    row("곡선+몫+요약", run([gap_block(data, gap, (0, 3, 6), 3), sb, ss], np.column_stack([gap_cols] + sb.cols + ss.cols)), p0)
    pall = run([gap_block(data, gap, (0, 3, 6), 3), sb, ss], np.column_stack([gap_cols] + sb.cols + ss.cols), comps)
    row("곡선+몫+요약+비슷한 카드", pall, p0)
    save()


if __name__ == "__main__":
    sys.exit(main())
