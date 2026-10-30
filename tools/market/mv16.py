"""MV-16: what the user says the market pays for. No alternative at that salary: how many cards in the position are as good for the
same pay or less, and how much pay a card saves against the cheapest as good (strongest at CDM and full back, the user says, and
inside a team colour). The footballer's lead card, and the best attacker of a popular team colour. On top of the app's model; same
half split, three tree seeds, paired. Read-only.
    python tools/market/mv16.py --db <copy of fchelper.db> --out artifacts/mv16.txt
"""
import argparse, math, os, sys
from collections import defaultdict
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import numpy as np
from mv12 import Block, FIELD_STATS, design, fold_of, floor_of, load
from mv12_pool import unique_cards, pooled_block, colour_block, compat_block, stats_line, paired
from mv12_search import rare_block
from mv14 import usage_block, match_usage
from mv14_blend import plain_columns
from mv14_fit import fit
from mv15 import TREE_WEIGHT
from mv15_grade import read_matches, count, columns
from mv15_pick import forest


def rivals(data, members=None):
    """Per card, its best position's dominated count, pay saved and OVR margin. Rivals are the cards playing that position, inside `members` when given."""
    by = defaultdict(list)
    for i, c in enumerate(data):
        if members is not None and i not in members: continue
        for p, v in c["positions"].items(): by[p].append((i, v, c["pay"]))
    arr = {p: (np.array([r[0] for r in v]), np.array([r[1] for r in v], float), np.array([r[2] for r in v], float)) for p, v in by.items()}
    dom = np.full(len(data), np.inf); save = np.full(len(data), -np.inf); margin = np.full(len(data), -np.inf)
    for i, c in enumerate(data):
        if members is not None and i not in members: continue
        for p, v in c["positions"].items():
            ids, vals, pays = arr[p]
            others = ids != i
            as_good = others & (vals >= v)
            dom[i] = min(dom[i], (as_good & (pays <= c["pay"])).sum())
            # No card as good at all: the most anyone could save, kept finite.
            save[i] = max(save[i], (pays[as_good].min() - c["pay"]) if as_good.any() else 5)
            cheap = others & (pays <= c["pay"])
            margin[i] = max(margin[i], v - vals[cheap].max() if cheap.any() else 5)
    return dom, np.clip(save, -6, 5), np.clip(margin, -8, 5)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=os.path.expandvars(r"%LOCALAPPDATA%\FcHelper\fchelper.db"))
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    g = 11
    m = load(a.db)
    rng = np.random.default_rng(16)
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
    fam = np.array([c["family"] for c in data])
    free = ~cens; no_trait = np.array([not c["traits"] for c in data])
    out = []
    def save_out():
        Path(a.out).parent.mkdir(parents=True, exist_ok=True)
        Path(a.out).write_text("\n".join(out), encoding="utf-8")
    def block(pairs, z=30):
        b = Block()
        for k, v in pairs: b.add(k, v, z=z)
        return b
    dom, saved, margin = rivals(data)
    share = m["colour_share"]
    def club_features():
        by = defaultdict(set)
        for i, c in enumerate(data):
            for t in c["colours"]:
                if share.get(t, 0) > .005: by[t].add(i)
        best_free = np.zeros(len(y)); best_save = np.zeros(len(y)); best_dom = np.full(len(y), 50.0)
        for t, members in by.items():
            d, s, _ = rivals(data, members)
            for i in members:
                best_dom[i] = min(best_dom[i], d[i])
                best_free[i] = max(best_free[i], share[t] * (d[i] == 0))
                best_save[i] = max(best_save[i], share[t] * max(s[i], 0))
        return best_dom, best_free, best_save
    club_dom, club_free, club_save = club_features()
    # The footballer's lead card: top OVR among its cards, by how much, and the stats it alone leads.
    by_player = defaultdict(list)
    for i, p in enumerate(players): by_player[p].append(i)
    lead = np.zeros(len(y)); lead_gap = np.zeros(len(y)); lead_stats = np.zeros(len(y)); solo = np.zeros(len(y))
    for ids in by_player.values():
        if len(ids) == 1:
            solo[ids[0]] = 1; continue
        for i in ids:
            rest = [j for j in ids if j != i]
            best = max(ovr[j] for j in rest)
            lead[i] = ovr[i] >= best; lead_gap[i] = np.clip(ovr[i] - best, -8, 8)
            lead_stats[i] = sum(data[i]["stats"].get(s, 0) > max(data[j]["stats"].get(s, 0) for j in rest) for s in FIELD_STATS)
    finish_lead = np.array([c["line"] == "ATT" for c in data], float) * np.array([
        max(0, c["stats"].get("finishing", 0) - max((data[j]["stats"].get("finishing", 0) for j in by_player[c["player"]] if j != i), default=c["stats"].get("finishing", 0))) for i, c in enumerate(data)], float)
    # Best attacker of a popular colour: rank over the line, not just the position.
    line_rank = np.full(len(y), 50.0); line_top = np.zeros(len(y))
    by_line = defaultdict(list)
    for i, c in enumerate(data):
        for t in c["colours"]:
            if share.get(t, 0) > .005: by_line[t, c["line"]].append(i)
    for (t, _), ids in by_line.items():
        for i in ids:
            rank = sum(ovr[j] > ovr[i] for j in ids)
            line_rank[i] = min(line_rank[i], rank)
            if rank == 0: line_top[i] = max(line_top[i], share[t])
    ratings = np.array([c["rating"] or 0 for c in data], float); rated = np.array([c["rating_count"] or 0 for c in data], float)
    rating = np.where(rated > 0, ratings - ratings[rated > 0].mean(), 0)
    defend = np.isin(fam, ["CDM", "FB"]).astype(float)
    out.append(f"카드 {len(y):,}장 · 대안 0장 {(dom == 0).sum()} · 1~3장 {((dom >= 1) & (dom <= 3)).sum()} · 4~20장 {((dom >= 4) & (dom <= 20)).sum()} · 그 이상 {(dom > 20).sum()}")
    base = [pooled_block(data, dict(statz=300)), colour_block(data, m), rare_block(data), compat_block(data), usage_block(data, usage, False), block([("n11", h["log11"]), ("n11_any", h["n11"] > 0)])]
    F0 = np.column_stack([plain_columns(data, m, usage, None), h["log11"]])
    def linear(extra):
        _, X, pen = design(base + extra)
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
    use = h["n"]
    rank = np.argsort(-use)
    def top_mask(n):
        mk = np.zeros(len(y), bool); mk[rank[:n]] = True
        return mk
    parts = {"전체": np.ones(len(y), bool), "실사용": ovr >= 119, "120+특성있음": (ovr >= 120) & free & ~no_trait, "120+특성없음": (ovr >= 120) & free & no_trait, "경기300": top_mask(300), "경기1000": top_mask(1000),
             "CDM": fam == "CDM", "FB": fam == "FB", "대안3이하": dom <= 3}
    def blend(extra, cols):
        l1 = linear(extra)
        ps = [np.maximum((1 - TREE_WEIGHT) * l1 + TREE_WEIGHT * trees(cols, s), lf) for s in seeds]
        return ps, np.mean(ps, axis=0)
    ref_s, ref = blend([], [])
    out.append("\n## 앱 지금")
    out.append("  " + " · ".join(f"{k} {np.mean([stats_line(p, y, v)[0] for p in ref_s]):.1%}" for k, v in parts.items()))
    r = y - ref
    out.append("\n## 대안 수별 지금 잔차 (양수 = 시장이 예상보다 비쌈, 하한 제외)")
    for fname, fm in (("전체", np.ones(len(y), bool)), ("CDM·FB", defend > 0), ("그 외", defend == 0)):
        for lab, lo, hi in (("0장", 0, 0), ("1~3장", 1, 3), ("4~20장", 4, 20), ("21장+", 21, 10**6)):
            mk = fm & free & (dom >= lo) & (dom <= hi)
            if mk.sum() >= 5: out.append(f"  {fname} 대안 {lab}: {mk.sum()}장 · 잔차 평균 {math.exp(r[mk].mean()) - 1:+.0%} 중앙 {math.exp(np.median(r[mk])) - 1:+.0%} · 오차 {stats_line(ref, y, mk)[0]:.0%} · 중앙 급여 {np.median([data[i]['pay'] for i in np.where(mk)[0]]):.0f} · 중앙 OVR {np.median(ovr[mk]):.0f}")
    save_out()
    alt = [("dom_log", np.log1p(dom)), ("dom0", dom == 0), ("dom3", dom <= 3), ("saved", saved), ("saved+", np.maximum(saved, 0)), ("margin+", np.maximum(margin, 0)), ("margin-", np.minimum(margin, 0))]
    alt_cols = [np.log1p(dom), saved, margin]
    alt_def = [(k + "×수비", np.asarray(v, float) * defend) for k, v in alt]
    alt_def_cols = [np.log1p(dom) * defend, saved * defend]
    club = [("club_dom_log", np.log1p(club_dom)), ("club_free", club_free), ("club_save", club_save)]
    club_cols = [np.log1p(club_dom), club_free, club_save]
    leadb = [("lead", lead), ("lead_gap", lead_gap), ("lead_stats", np.log1p(lead_stats)), ("solo", solo), ("finish_lead", finish_lead)]
    lead_cols = [lead_gap, lead_stats, finish_lead]
    line = [("line_rank", np.log1p(line_rank)), ("line_top", line_top)]
    line_cols = [np.log1p(line_rank), line_top]
    rate = [("rating", rating), ("rating_n", np.log1p(rated))]
    rate_cols = [rating, np.log1p(rated)]
    variants = [("급여 대안", alt, alt_cols), ("급여 대안 + 수비 더", alt + alt_def, alt_cols + alt_def_cols), ("급여 대안 z=3", None, alt_cols), ("팀 안 급여 대안", club, club_cols),
                ("대장 카드", leadb, lead_cols), ("팀 공수 안 최고", line, line_cols), ("평점(체감)", rate, rate_cols),
                ("급여+팀 안", alt + club, alt_cols + club_cols), ("급여+팀 안+대장+최고", alt + club + leadb + line, alt_cols + club_cols + lead_cols + line_cols)]
    best = {}
    for name, pairs, cols in variants:
        extra = [block(alt, 3)] if pairs is None else [block(pairs)]
        ps, pb = blend(extra, cols)
        best[name] = pb
        out.append(f"\n## {name}")
        out.append("  오차 " + " · ".join(f"{k} {np.mean([stats_line(p, y, v)[0] for p in ps]):.1%}" for k, v in parts.items()))
        for k, v in parts.items():
            d0, lo0, hi0 = paired(pb, ref, y, players, v & ~hold, rng)
            d1, lo1, hi1 = paired(pb, ref, y, players, v & hold, rng)
            out.append(f"  {k:<10} 선택 {d0:+.3f} ({lo0:+.3f}~{hi0:+.3f}) · 확인 {d1:+.3f} ({lo1:+.3f}~{hi1:+.3f})")
        save_out()
    out.append("\n## 말씀하신 카드: 시세 / 지금 / 급여 대안 / 전부 (억)")
    want = [("루이스-스켈리", "PTG"), ("엔소 페르난데스", "PTG"), ("요주아 키미히", "25UCL"), ("우스만 뎀벨레", "26TOTS"), ("우스만 뎀벨레", "26TOTY"), ("리자라쥐", "CH"), ("쿠쿠레야", "PTG")]
    allp = best["급여+팀 안+대장+최고"]
    for name, season in want:
        for i, c in enumerate(data):
            if name in c["name"] and c["season"] == season:
                out.append(f"  {season} {c['name']}: {math.exp(y[i]) / 1e8:.1f} / {math.exp(ref[i]) / 1e8:.1f} / {math.exp(best['급여 대안'][i]) / 1e8:.1f} / {math.exp(allp[i]) / 1e8:.1f} · 대안 {int(dom[i])}장 · 절약 {saved[i]:+.0f}")
    save_out()


if __name__ == "__main__":
    sys.exit(main())
