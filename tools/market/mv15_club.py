"""MV-15: the most missed cards look like one club after another. Does the market pay per team colour beyond the single popularity
number the model has? Own value per team colour, per colour and line, and per colour above the position's reference level; all on
top of the played-grade columns. Also: does a lower error cost the model its use as a value signal (does the gap to the estimate at
the older snapshot still say which way the price goes)? Same half split. Read-only.
    python tools/market/mv15_club.py --db <copy of fchelper.db> --out artifacts/mv15_club.txt
"""
import argparse, math, os, sys
from collections import Counter, defaultdict
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import numpy as np
from mv12 import Block, design, fold_of, floor_of, load
from mv12_pool import unique_cards, pooled_block, colour_block, compat_block, stats_line, paired
from mv12_search import rare_block
from mv14 import usage_block, match_usage
from mv14_blend import plain_columns
from mv14_fit import fit
from mv15 import boosted_fold, TREE_WEIGHT
from mv15_grade import read_matches, count, columns


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
    rows = read_matches(a.db)
    usage, _ = match_usage(a.db)
    h = columns(data, count(rows))
    has = (h["n"] > 0).astype(float)
    ranker = np.array([m["use_card"][c["sp_id"]] for c in data], float)
    logr = np.log1p(ranker)
    use = h["n"] + ranker
    rank = np.argsort(-use)
    def top_mask(n):
        mk = np.zeros(len(y), bool); mk[rank[:n]] = True
        return mk
    top100, top300, top1000 = top_mask(100), top_mask(300), top_mask(1000)
    everyone = np.ones(len(y), bool); top = ovr >= 119; free = ~cens
    no_trait = np.array([not c["traits"] for c in data])
    out = []
    def save():
        Path(a.out).parent.mkdir(parents=True, exist_ok=True)
        Path(a.out).write_text("\n".join(out), encoding="utf-8")
    def errs(p):
        return (f"전체 {stats_line(p, y, everyone)[0]:.1%} · 쓰임100 {stats_line(p, y, top100)[0]:.1%} · 300 {stats_line(p, y, top300)[0]:.1%} · 1000 {stats_line(p, y, top1000)[0]:.1%} · 상위 특성없음 {stats_line(p, y, top & free & no_trait)[0]:.1%} · 있음 {stats_line(p, y, top & free & ~no_trait)[0]:.1%}")
    def row(name, p, ref):
        s = f"  {name:<26} {errs(p)}"
        if ref is not None:
            for lab, mk in (("선택", ~hold), ("확인", hold)):
                d, lo, hi = paired(p, ref, y, players, mk, rng)
                s += f" | {lab} {d:+.3f} ({lo:+.3f}~{hi:+.3f})"
        out.append(s); save()
    def block(pairs, z):
        b = Block()
        for k, v in pairs: b.add(k, v, z=z)
        return b
    base = [pooled_block(data, dict(statz=300)), colour_block(data, m), rare_block(data), compat_block(data), usage_block(data, usage, False)]
    grade = block([("n11", h["log11"]), ("n11_any", h["n11"] > 0), ("share11", h["share11"] * has), ("r", logr), ("r_any", ranker > 0)], 30)
    F = np.column_stack([plain_columns(data, m, usage, None), h["log11"], h["share11"] * has, logr])
    def run(blocks, tree_cols=None):
        _, X, pen = design(base + blocks)
        F1 = F if tree_cols is None else np.column_stack([F, tree_cols])
        pl, pt = np.full(len(y), np.nan), np.full(len(y), np.nan)
        for f in range(5):
            tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
            pl[te] = fit(X, pen, y, cens, players, tr, te, floors, ovr, data)
            pt[te] = boosted_fold(F1, y, players, ovr, seasons, tr, te, lf)
        return np.maximum((1 - TREE_WEIGHT) * pl + TREE_WEIGHT * pt, lf)
    p1 = run([grade])
    row("새 열까지", p1, None)
    # Which colours does the fit leave money on?
    names = {}
    r = y - p1
    colours = Counter(t for c in data for t in c["colours"])
    import sqlite3
    conn = sqlite3.connect(Path(a.db).resolve().as_uri() + "?mode=ro", uri=True)
    for i, n in conn.execute("SELECT id,name FROM team_color"): names[i] = n
    conn.close()
    table = []
    for t, n in colours.items():
        if n < 12: continue
        mk = np.array([t in c["colours"] for c in data]) & free
        if mk.sum() >= 8:
            se = r[mk].std() / math.sqrt(mk.sum())
            table.append((r[mk].mean() / se, names.get(t, str(t)), mk.sum(), r[mk].mean(), np.median(np.abs(np.exp(r[mk]) - 1)), m["colour_share"].get(t, 0)))
    table.sort()
    out.append("\n## 팀컬러별 잔차 (양수 = 시장이 예상보다 비쌈, 하한 제외)")
    for row_ in table[-12:][::-1] + table[:8]:
        out.append(f"  {row_[1]:<18} {row_[2]:>4}장 · 평균 {math.exp(row_[3]) - 1:+.0%} · 오차 {row_[4]:.0%} · 랭커 몫 {row_[5]:.1%} · t {row_[0]:+.1f}")
    out.append(f"  팀컬러 {len(table)}개, |t|>2: {sum(abs(x[0]) > 2 for x in table)}개")
    out.append("\n## 시즌·포지션별 잔차")
    groups = defaultdict(list)
    for i, c in enumerate(data):
        if free[i]: groups[c["season"], c["family"]].append(r[i])
    rows2 = sorted(((np.mean(v) / (np.std(v) / math.sqrt(len(v)) + 1e-9), k, len(v), np.mean(v)) for k, v in groups.items() if len(v) >= 8), key=lambda x: x[0])
    for t, k, n, mean in rows2[-8:][::-1] + rows2[:4]:
        out.append(f"  {k[0]} {k[1]}: {n}장 · 평균 {math.exp(mean) - 1:+.0%} · t {t:+.1f}")
    save()
    out.append("\n## 시험")
    def club_cols(min_cards):
        return [t for t, n in colours.items() if n >= min_cards]
    def own(ts, z):
        return block([(f"club:{t}", [t in c["colours"] for c in data]) for t in ts], z)
    def lines(ts, z):
        b = Block()
        for t in ts:
            hh = np.array([t in c["colours"] for c in data], float)
            for f in ("ATT", "MID", "DEF"):
                l = np.array([c["line"] == f for c in data], float)
                if (hh * l).sum() >= 5: b.add(f"club:{t}×{f}", hh * l, z=z)
        return b
    def slope(ts, z):
        d = ovr - ovr.mean()
        return block([(f"club:{t}×ovr", np.array([t in c["colours"] for c in data], float) * d) for t in ts], z)
    ts = club_cols(12)
    # Target encoding for the trees would see the card's own price, so only the linear part gets these; a tree column counts rankers per club instead.
    club_use = Counter()
    for c, u in zip(data, use):
        for t in c["colours"]: club_use[t] += u
    tree_club = np.log1p([max((club_use[t] for t in c["colours"]), default=0) for c in data])[:, None]
    for z in (30, 100, 300):
        row(f"팀컬러마다 값 z={z}", run([grade, own(ts, z)], tree_club), p1)
    row("팀컬러×공수 z=100", run([grade, own(ts, 100), lines(ts, 100)], tree_club), p1)
    row("팀컬러×OVR z=100", run([grade, own(ts, 100), slope(ts, 100)], tree_club), p1)
    save()
    out.append("\n## 오차를 줄이면 값어치 신호가 약해지나 (앞 스냅샷의 예상과의 차이 → 그 뒤 시세 변화)")
    for name, blocks in (("지금 앱", []), ("새 열까지", [grade])):
        for s in m["snaps"][-3:-1]:
            prices = m["prev"][s]
            old = np.array([prices.get((c["grp"], c["sp_id"]), {}).get(g, 0) for c in data], float)
            ok = old > 1200
            fl = np.array([floor_of(prices.get((c["grp"], c["sp_id"]), {}), g) for c in data])
            co = ok & (old <= fl * 1.007)
            yo = np.where(ok, np.log(np.maximum(old, 1)), y)
            _, X, pen = design(base + blocks)
            po = np.full(len(y), np.nan)
            for f in range(5):
                tr, te = np.where((folds != f) & ok)[0], np.where((folds == f) & ok)[0]
                if len(te): po[te] = fit(X, pen, yo, co, players, tr, te, fl, ovr, data)
            use_mk = ok & ~co & free & ~np.isnan(po)
            gap_old = (yo - po)[use_mk]
            move = (y - yo)[use_mk]
            move = move - np.median(move)
            cheap = gap_old < np.percentile(gap_old, 20); dear = gap_old > np.percentile(gap_old, 80)
            out.append(f"  {name} · 스냅샷 {s}: {use_mk.sum():,}장 · 상관 {np.corrcoef(gap_old, move)[0, 1]:+.3f} · 싼 20% 변화 {np.median(move[cheap]):+.3f} · 비싼 20% {np.median(move[dear]):+.3f}")
            save()


if __name__ == "__main__":
    sys.exit(main())
