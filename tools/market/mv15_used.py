"""MV-15: the cards people use are where the model is worst (MV-15 first table). Is that a bias the fit can remove, or a wider spread?
Lists the most used cards against their estimates, then tests fits aimed at them: usage at +11 only, ranker use, a card's share of
its footballer's use, less shrinkage, and used cards getting their own slopes. Chosen on half the footballers, confirmed on the
other half. Read-only.
    python tools/market/mv15_used.py --db <copy of fchelper.db> --out artifacts/mv15_used.txt
"""
import argparse, math, os, sys
from collections import Counter
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import numpy as np
from mv12 import Block, design, fold_of, floor_of, load
from mv12_pool import unique_cards, pooled_block, colour_block, compat_block, stats_line, paired
from mv12_search import rare_block
from mv14 import match_usage, usage_block
from mv14_blend import plain_columns
from mv14_fit import fit
from mv15 import boosted_fold, TREE_WEIGHT


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=os.path.expandvars(r"%LOCALAPPDATA%\FcHelper\fchelper.db"))
    ap.add_argument("--out", required=True)
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
    n_all = np.array([usage[c["sp_id"]]["n"] if c["sp_id"] in usage else 0 for c in data], float)
    n_11 = np.array([usage[c["sp_id"]]["n11"] if c["sp_id"] in usage else 0 for c in data], float)
    rank_use = np.array([m["use_card"][c["sp_id"]] for c in data], float)
    use = n_all + rank_use
    out = []
    def save():
        Path(a.out).parent.mkdir(parents=True, exist_ok=True)
        Path(a.out).write_text("\n".join(out), encoding="utf-8")
    app = [pooled_block(data, dict(statz=300)), colour_block(data, m), rare_block(data), compat_block(data), usage_block(data, usage, False)]
    F = plain_columns(data, m, usage, None)
    def run(blocks, tree_cols=None, weight=TREE_WEIGHT):
        _, X, pen = design(app + blocks)
        F1 = F if tree_cols is None else np.column_stack([F, tree_cols])
        pl, pt = np.full(len(y), np.nan), np.full(len(y), np.nan)
        for f in range(5):
            tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
            pl[te] = fit(X, pen, y, cens, players, tr, te, floors, ovr, data)
            pt[te] = boosted_fold(F1, y, players, ovr, seasons, tr, te, lf)
        return np.maximum((1 - weight) * pl + weight * pt, lf)
    p0 = run([])
    rank = np.argsort(-use)
    def top_mask(n):
        mk = np.zeros(len(y), bool); mk[rank[:n]] = True
        return mk
    top100, top300, top1000 = top_mask(100), top_mask(300), top_mask(1000)
    everyone = np.ones(len(y), bool)
    def errs(p):
        return (f"전체 {stats_line(p, y, everyone)[0]:.1%} · 많이 쓰임 100 {stats_line(p, y, top100)[0]:.1%} · 300 {stats_line(p, y, top300)[0]:.1%} · 1000 {stats_line(p, y, top1000)[0]:.1%}")
    def row(name, p, ref):
        s = f"  {name:<30} {errs(p)}"
        if ref is not None:
            for lab, mk in (("선택", ~hold), ("확인", hold)):
                d, lo, hi = paired(p, ref, y, players, mk, rng)
                s += f" | {lab} {d:+.3f} ({lo:+.3f}~{hi:+.3f})"
            d, lo, hi = paired(p, ref, y, players, top1000 & hold, rng)
            s += f" | 1000장 확인 {d:+.3f} ({lo:+.3f}~{hi:+.3f})"
        out.append(s); save()
    r = y - p0  # positive: the market pays more than the estimate
    out.append("## 사용량 구간별 잔차 (양수 = 시장이 예상보다 비쌈)")
    bins = [(0, 0), (1, 3), (4, 10), (11, 30), (31, 100), (101, 300), (301, 10**9)]
    for lo, hi in bins:
        mk = (use >= lo) & (use <= hi)
        if mk.sum() < 5: continue
        out.append(f"  사용 {lo}~{hi}: {mk.sum():,}장 · 하한 {cens[mk].mean():.0%} · 오차 {stats_line(p0, y, mk)[0]:.1%} · 잔차 중앙 {np.median(r[mk]):+.2f} 평균 {r[mk].mean():+.2f} 표준편차 {r[mk].std():.2f} · 중앙 시세 {math.exp(np.median(y[mk])) / 1e8:,.1f}억 · 중앙 OVR {np.median(ovr[mk]):.0f}")
    out.append("\n## 많이 쓰이는 30장")
    for i in rank[:30]:
        c = data[i]
        out.append(f"  {c['season']:>6} {c['name']:<14} {c['family']:<3} OVR{c['ovr1']} 특성{len(c['traits'])} 경기 {int(n_all[i])}(+11 {int(n_11[i])}) 랭커 {int(rank_use[i])} · 시세 {math.exp(y[i]) / 1e8:,.1f}억 예상 {math.exp(p0[i]) / 1e8:,.1f}억 ({math.exp(r[i]) - 1:+.0%})")
    # Same footballers: does the most used card of a footballer sit above its siblings' line?
    by = {}
    for i, p in enumerate(players): by.setdefault(p, []).append(i)
    lead, other = [], []
    for ids in by.values():
        if len(ids) < 2: continue
        best = max(ids, key=lambda i: use[i])
        if use[best] == 0: continue
        lead.append(r[best]); other += [r[i] for i in ids if i != best]
    out.append(f"\n선수의 가장 많이 쓰이는 카드 잔차 중앙 {np.median(lead):+.3f} ({len(lead):,}장) · 나머지 카드 {np.median(other):+.3f} ({len(other):,}장)")
    out.append("\n## 시험")
    row("앱 지금", p0, None)
    def block(name_cols, z):
        b = Block()
        for k, v in name_cols: b.add(k, v, z=z)
        return b
    log11 = np.log1p(n_11); logr = np.log1p(rank_use); logu = np.log1p(use)
    per = Counter()
    for p, u in zip(players, use): per[p] += u
    lead_share = np.array([u / per[p] if per[p] > 0 else 0 for p, u in zip(players, use)])
    best_use = np.array([u > 0 and u == max(use[i] for i in by[p]) for p, u in zip(players, use)], float)
    row("+11 경기 사용", run([block([("n11", log11), ("n11_any", n_11 > 0)], 30)], np.column_stack([log11])), p0)
    row("+11 경기 사용 z=3", run([block([("n11", log11), ("n11_any", n_11 > 0)], 3)], np.column_stack([log11])), p0)
    row("랭커 사용", run([block([("r", logr), ("r_any", rank_use > 0)], 30)], np.column_stack([logr])), p0)
    row("랭커 사용 z=3", run([block([("r", logr), ("r_any", rank_use > 0)], 3)], np.column_stack([logr])), p0)
    row("선수 안 사용 몫", run([block([("lead", lead_share), ("best", best_use)], 3)], np.column_stack([lead_share, best_use])), p0)
    d = ovr - ovr.mean()
    fams = sorted({c["family"] for c in data})
    used = (use > 0).astype(float)
    inter = [("used", used), ("used×ovr", used * d), ("used×ovr2", used * d * d), ("logu×ovr", logu * d)]
    inter += [(f"used×{f}", used * np.array([c["family"] == f for c in data], float)) for f in fams]
    inter += [(f"logu×{f}", logu * np.array([c["family"] == f for c in data], float)) for f in fams]
    row("쓰이는 카드 따로 곡선 z=3", run([block(inter, 3)], np.column_stack([logu * d])), p0)
    row("쓰이는 카드 따로 곡선 z=30", run([block(inter, 30)], np.column_stack([logu * d])), p0)
    all_use = block([("n11", log11), ("n11_any", n_11 > 0), ("r", logr), ("r_any", rank_use > 0), ("lead", lead_share), ("best", best_use)] + inter, 30)
    p_all = run([all_use], np.column_stack([log11, logr, lead_share, best_use, logu * d]))
    row("전부 z=30", p_all, p0)
    for w in (.5, .7):
        row(f"전부 + 나무 {w:.0%}", run([all_use], np.column_stack([log11, logr, lead_share, best_use, logu * d]), w), p0)
    r1 = y - p_all
    out.append("\n## 전부 넣은 뒤 사용량 구간별")
    for lo, hi in bins:
        mk = (use >= lo) & (use <= hi)
        if mk.sum() < 5: continue
        out.append(f"  사용 {lo}~{hi}: 오차 {stats_line(p_all, y, mk)[0]:.1%} · 잔차 중앙 {np.median(r1[mk]):+.2f} 표준편차 {r1[mk].std():.2f}")
    save()


if __name__ == "__main__":
    sys.exit(main())
