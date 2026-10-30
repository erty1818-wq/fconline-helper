"""MV-15: which grades a card is played at. A card nearly always fielded at +11 is cheap enough at +11; one played at +5 with the
same use is dear at +11. Tests that signal, a steeper value for the very most used cards, use against same-level cards in the
position, and how much survives when a PC stores fewer matches or leaves out the most frequent manager. Same half split. Read-only.
    python tools/market/mv15_grade.py --db <copy of fchelper.db> --out artifacts/mv15_grade.txt
"""
import argparse, json, math, os, sqlite3, sys
from collections import Counter, defaultdict
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import numpy as np
from mv12 import Block, design, fold_of, floor_of, load
from mv12_pool import unique_cards, pooled_block, colour_block, compat_block, stats_line, paired
from mv12_search import rare_block
from mv14_blend import plain_columns
from mv14_fit import fit
from mv15 import boosted_fold, TREE_WEIGHT


def read_matches(db):
    import brotli
    c = sqlite3.connect(Path(db).resolve().as_uri() + "?mode=ro", uri=True)
    rows = []
    for (blob,) in c.execute("SELECT json FROM matches"):
        d = json.loads(brotli.decompress(bytes(blob)))
        for m in d.get("matchInfo", []):
            rows.append((m.get("ouid"), [(p.get("spId"), p.get("spGrade", 0)) for p in m.get("player") or [] if p.get("spPosition") != 28]))
    return rows


def count(rows):
    """Card → starts at each grade."""
    u = defaultdict(Counter)
    for _, players in rows:
        for sp, g in players: u[sp][g] += 1
    return u


def columns(data, u, prior=5):
    n = np.array([sum(u[c["sp_id"]].values()) if c["sp_id"] in u else 0 for c in data], float)
    def at_least(k): return np.array([sum(v for g, v in u[c["sp_id"]].items() if g >= k) if c["sp_id"] in u else 0 for c in data], float)
    n11, n10, n8 = at_least(11), at_least(10), at_least(8)
    mean = np.array([sum(g * v for g, v in u[c["sp_id"]].items()) if c["sp_id"] in u else 0 for c in data], float)
    overall = mean.sum() / max(n.sum(), 1)
    return dict(n=n, n11=n11, log11=np.log1p(n11), share11=(n11 + prior * n11.sum() / max(n.sum(), 1)) / (n + prior), share10=(n10 + prior * n10.sum() / max(n.sum(), 1)) / (n + prior),
                share8=(n8 + prior * n8.sum() / max(n.sum(), 1)) / (n + prior), grade=(mean + prior * overall) / (n + prior))


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
    out = []
    def save():
        Path(a.out).parent.mkdir(parents=True, exist_ok=True)
        Path(a.out).write_text("\n".join(out), encoding="utf-8")
    mgr = Counter(o for o, _ in rows)
    top_mgr = mgr.most_common(5)
    out.append(f"경기 기록 팀 {len(rows):,} · 구단주 {len(mgr):,}명 · 가장 많은 구단주 몫 {[round(v / len(rows), 3) for _, v in top_mgr]}")
    ranker = np.array([m["use_card"][c["sp_id"]] for c in data], float)
    logr = np.log1p(ranker)
    full = columns(data, count(rows))
    use = full["n"] + ranker
    rank = np.argsort(-use)
    def top_mask(n):
        mk = np.zeros(len(y), bool); mk[rank[:n]] = True
        return mk
    top100, top300, top1000 = top_mask(100), top_mask(300), top_mask(1000)
    everyone = np.ones(len(y), bool); top = ovr >= 119; free = ~cens
    no_trait = np.array([not c["traits"] for c in data])
    def errs(p):
        return (f"전체 {stats_line(p, y, everyone)[0]:.1%} · 쓰임100 {stats_line(p, y, top100)[0]:.1%} · 300 {stats_line(p, y, top300)[0]:.1%} · 1000 {stats_line(p, y, top1000)[0]:.1%} · 상위 특성없음 {stats_line(p, y, top & free & no_trait)[0]:.1%} · 있음 {stats_line(p, y, top & free & ~no_trait)[0]:.1%}")
    def row(name, p, ref):
        s = f"  {name:<28} {errs(p)}"
        if ref is not None:
            for lab, mk in (("선택", ~hold), ("확인", hold)):
                d, lo, hi = paired(p, ref, y, players, mk, rng)
                s += f" | {lab} {d:+.3f} ({lo:+.3f}~{hi:+.3f})"
        out.append(s); save()
    base = [pooled_block(data, dict(statz=300)), colour_block(data, m), rare_block(data), compat_block(data)]
    F0 = plain_columns(data, m, defaultdict(lambda: dict(n=0)), None)[:, :]  # usage column filled below
    use_at = 9
    def usage_cols(cols, z=30):
        b = Block()
        per = Counter()
        for sp, v in cols.items(): per[sp % 1_000_000] += sum(v.values())
        b.add("use_card", np.log1p(np.array([sum(cols[c["sp_id"]].values()) if c["sp_id"] in cols else 0 for c in data], float)), z=z)
        b.add("use_any", np.array([c["sp_id"] in cols for c in data], float), z=z)
        b.add("use_player", np.log1p([per[c["player"]] for c in data]), z=z)
        return b
    def run(cols, blocks, tree_cols=None, weight=TREE_WEIGHT):
        _, X, pen = design(base + [usage_cols(cols)] + blocks)
        F1 = F0.copy()
        F1[:, use_at] = np.log1p(np.array([sum(cols[c["sp_id"]].values()) if c["sp_id"] in cols else 0 for c in data], float))
        if tree_cols is not None: F1 = np.column_stack([F1, tree_cols])
        pl, pt = np.full(len(y), np.nan), np.full(len(y), np.nan)
        for f in range(5):
            tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
            pl[te] = fit(X, pen, y, cens, players, tr, te, floors, ovr, data)
            pt[te] = boosted_fold(F1, y, players, ovr, seasons, tr, te, lf)
        return np.maximum((1 - weight) * pl + weight * pt, lf)
    def block(pairs, z):
        b = Block()
        for k, v in pairs: b.add(k, v, z=z)
        return b
    all_cols = count(rows)
    p0 = run(all_cols, [])
    row("앱 지금", p0, None)
    f = full
    has = (f["n"] > 0).astype(float)
    g11 = [("n11", f["log11"]), ("n11_any", f["n11"] > 0)]
    row("+11 경기 사용", run(all_cols, [block(g11, 30)], f["log11"][:, None]), p0)
    shares = [("share11", f["share11"] * has), ("share10", f["share10"] * has), ("share8", f["share8"] * has), ("grade", f["grade"] * has)]
    row("강화 분포(몫·평균)", run(all_cols, [block(shares, 30)], np.column_stack([v for _, v in shares])), p0)
    row("강화 분포 z=3", run(all_cols, [block(shares, 3)], np.column_stack([v for _, v in shares])), p0)
    row("+11 사용 + 강화 분포", run(all_cols, [block(g11 + shares, 30)], np.column_stack([f["log11"]] + [v for _, v in shares])), p0)
    hinge = [(f"use>{k}", np.maximum(0, np.log1p(use) - math.log(k))) for k in (100, 300, 1000)]
    row("아주 많이 쓰이는 카드 더 가파르게", run(all_cols, [block(hinge, 3)], np.column_stack([v for _, v in hinge])), p0)
    # Use against same-level cards in the position: being the pick at its level, whatever the level.
    fam = np.array([c["family"] for c in data])
    logu = np.log1p(use)
    rel = np.zeros(len(y))
    for i in range(len(y)):
        near = (fam == fam[i]) & (np.abs(ovr - ovr[i]) <= 1)
        rel[i] = logu[i] - logu[near].mean()
    row("같은 포지션·수준 안 사용", run(all_cols, [block([("rel", rel), ("rel+", np.maximum(0, rel))], 3)], rel[:, None]), p0)
    keep = g11 + shares + hinge + [("rel", rel), ("rel+", np.maximum(0, rel)), ("r", logr), ("r_any", ranker > 0)]
    tree = np.column_stack([f["log11"]] + [v for _, v in shares] + [v for _, v in hinge] + [rel, logr])
    pa = run(all_cols, [block(keep, 30)], tree)
    row("전부", pa, p0)
    compact = g11 + shares[:1] + [("r", logr), ("r_any", ranker > 0)]
    row("작게: +11 사용·몫·랭커", run(all_cols, [block(compact, 30)], np.column_stack([f["log11"], shares[0][1], logr])), p0)
    out.append("\n## 경기가 적은 PC에서도 남나 (경기 기록만 줄임, 랭커는 그대로)")
    def small_set(rs):
        c = count(rs); h = columns(data, c); hs = (h["n"] > 0).astype(float)
        blk = [("n11", h["log11"]), ("n11_any", h["n11"] > 0), ("share11", h["share11"] * hs), ("r", logr), ("r_any", ranker > 0)]
        return run(c, [block(blk, 30)], np.column_stack([h["log11"], h["share11"] * hs, logr])), run(c, [])
    no_top = [r for r in rows if r[0] != top_mgr[0][0]]
    pn, pb = small_set(no_top)
    row(f"가장 많은 구단주 빼기 {len(no_top):,}팀 지금 구성", pb, p0)
    row("  + 새 열", pn, pb)
    idx = np.random.default_rng(3).permutation(len(rows))
    for share in (.3, .1):
        sub = [rows[i] for i in idx[:int(len(rows) * share)]]
        pn, pb = small_set(sub)
        row(f"경기 {share:.0%} ({len(sub):,}팀) 지금 구성", pb, p0)
        row("  + 새 열", pn, pb)
    save()


if __name__ == "__main__":
    sys.exit(main())
