"""MV-16: a low salary is only worth paying for on a card people field. Scarcity at its pay crossed with use, the pay curve itself per
position, and what one pay point is worth in the fit. Lists two cards of one footballer the market prices the opposite way to their
stats. Same half split, three tree seeds, paired. Read-only.
    python tools/market/mv16_pay.py --db <copy of fchelper.db> --out artifacts/mv16_pay.txt
"""
import argparse, math, os, sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import numpy as np
from mv12 import Block, FIELD_STATS, design, fit_predict, fold_of, floor_of, load
from mv12_pool import unique_cards, pooled_block, colour_block, compat_block, stats_line, paired
from mv12_search import rare_block
from mv14 import usage_block, match_usage
from mv14_blend import plain_columns
from mv14_fit import fit
from mv15 import TREE_WEIGHT
from mv15_grade import read_matches, count, columns
from mv15_pick import forest
from mv16 import rivals


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
    pay = np.array([c["pay"] for c in data], float)
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
    base = [pooled_block(data, dict(statz=300)), colour_block(data, m), rare_block(data), compat_block(data), usage_block(data, usage, False), block([("n11", h["log11"]), ("n11_any", h["n11"] > 0)])]
    F0 = np.column_stack([plain_columns(data, m, usage, None), h["log11"]])
    # One pay point in the fit, per position, with the card's other columns held still.
    names, X, pen = design(base)
    _, b, sd = fit_predict(X, pen, y, cens, players, np.arange(len(y)), np.arange(1), floors)
    full = b[1:] / sd
    at = {n: i for i, n in enumerate(names)}
    out.append("## 급여 1이 시세에 주는 값 (전체 자료 적합, 같은 OVR·능력치)")
    for f in sorted(set(fam)):
        out.append(f"  {f}: 급여 1 낮으면 {math.exp(-(full[at['pay']] + full[at['pay×' + f]])) - 1:+.0%} · OVR 1 높으면 {math.exp(full[at['ovr']] + full[at['ovr×' + f]]) - 1:+.0%}")
    out.append("\n## 같은 선수, OVR 차이 1 이하, 급여가 다른 카드끼리 (하한 제외): 급여 1당 로그 시세 차이")
    defend = np.isin(fam, ["CDM", "FB"])
    for lab, mk in (("CDM·FB", defend), ("그 외", ~defend)):
        d = []
        ids = np.where(mk & free)[0]
        for i in ids:
            for j in ids:
                if players[i] == players[j] and pay[i] < pay[j] and abs(ovr[i] - ovr[j]) <= 1: d.append((y[i] - y[j]) / (pay[j] - pay[i]))
        if d: out.append(f"  {lab}: {len(d)}쌍 · 급여 1 낮은 쪽 시세 중앙 {math.exp(np.median(d)) - 1:+.0%} (사분위 {math.exp(np.percentile(d, 25)) - 1:+.0%}~{math.exp(np.percentile(d, 75)) - 1:+.0%})")
    out.append("\n## 같은 선수 두 카드 (뎀벨레)")
    for c in data:
        if "우스만 뎀벨레" in c["name"] and c["season"] in ("26TOTS", "26TOTY", "25TOTS"):
            hist = [m["prev"][s].get((c["grp"], c["sp_id"]), {}).get(g) for s in sorted(m["prev"])[-4:]]
            u = h["n"][data.index(c)]; u11 = h["n11"][data.index(c)]
            shot = {s: c["stats"].get(s) for s in ("finishing", "shotpower", "longshots", "positioning", "volleys", "curve", "sprintspeed", "acceleration", "dribbling")}
            out.append(f"  {c['season']} OVR{c['ovr1']} 급여{c['pay']} 약발{c['weak_foot']} 포지션 {c['positions']} 특성 {sorted(c['traits'])} · 시세 {c['prices'][g] / 1e8:.0f}억 · 앞 스냅샷 {[round(v / 1e8) if v else None for v in hist]} · 경기 {int(u)}(+11 {int(u11)}) · {shot}")
    save_out()
    dom, saved, margin = rivals(data)
    logs = np.log1p(h["n"])
    scarce = np.maximum(0, 4 - np.minimum(dom, 4)) / 4  # 1 with no alternative, 0 with four or more
    d = defend.astype(float)
    cross = [("scarce×use", scarce * logs), ("scarce×use×수비", scarce * logs * d), ("scarce×use11", scarce * h["log11"]), ("scarce", scarce), ("scarce×수비", scarce * d)]
    cross_cols = [scarce * logs, scarce, dom]
    curve = []
    for lo in (26, 27, 28, 29, 30, 31):
        curve.append((f"pay<={lo}", pay <= lo)); curve.append((f"pay<={lo}×수비", (pay <= lo) * d))
    curve_cols = [pay * d]
    payovr = [("pay×ovr", (pay - pay.mean()) * (ovr - ovr.mean())), ("pay×ovr×수비", (pay - pay.mean()) * (ovr - ovr.mean()) * d), ("pay×use", (pay - pay.mean()) * logs), ("pay×use×수비", (pay - pay.mean()) * logs * d)]
    payovr_cols = [(pay - pay.mean()) * logs]
    def linear(extra):
        _, X1, pen1 = design(base + extra)
        p = np.full(len(y), np.nan)
        for f in range(5):
            tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
            p[te] = fit(X1, pen1, y, cens, players, tr, te, floors, ovr, data)
        return p
    def trees(cols, seed):
        F1 = np.column_stack([F0] + cols) if cols else F0
        p = np.full(len(y), np.nan)
        for f in range(5):
            tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
            p[te] = forest(F1, y, players, ovr, seasons, tr, te, lf, seed)
        return p
    seeds = (7, 11, 23)
    rank = np.argsort(-h["n"])
    top300 = np.zeros(len(y), bool); top300[rank[:300]] = True
    used_scarce = (dom <= 3) & (h["n"] >= 10)
    parts = {"전체": np.ones(len(y), bool), "실사용": ovr >= 119, "120+특성있음": (ovr >= 120) & free & ~no_trait, "120+특성없음": (ovr >= 120) & free & no_trait, "경기300": top300,
             "CDM·FB": defend, "CDM·FB 실사용": defend & (ovr >= 119), "대안3이하·쓰임": used_scarce}
    def blend(extra, cols):
        l1 = linear(extra)
        ps = [np.maximum((1 - TREE_WEIGHT) * l1 + TREE_WEIGHT * trees(cols, s), lf) for s in seeds]
        return ps, np.mean(ps, axis=0)
    ref_s, ref = blend([], [])
    out.append("\n## 앱 지금")
    out.append("  " + " · ".join(f"{k} {np.mean([stats_line(p, y, v)[0] for p in ref_s]):.1%}" for k, v in parts.items()) + f" · 대안3이하·쓰임 {used_scarce.sum()}장")
    r = y - ref
    for lab, mk in (("CDM·FB 대안3이하·쓰임", used_scarce & defend), ("그 외 대안3이하·쓰임", used_scarce & ~defend), ("CDM·FB 대안3이하·안쓰임", (dom <= 3) & (h["n"] < 10) & defend), ("그 외 대안3이하·안쓰임", (dom <= 3) & (h["n"] < 10) & ~defend)):
        mk = mk & free
        if mk.sum() >= 3: out.append(f"  {lab}: {mk.sum()}장 · 잔차 평균 {math.exp(r[mk].mean()) - 1:+.0%} 중앙 {math.exp(np.median(r[mk])) - 1:+.0%} · 오차 {stats_line(ref, y, mk)[0]:.0%}")
    save_out()
    for name, pairs, cols, z in (("대안 없음 × 쓰임", cross, cross_cols, 30), ("대안 없음 × 쓰임 z=3", cross, cross_cols, 3), ("급여 곡선", curve, curve_cols, 30), ("급여×OVR·쓰임", payovr, payovr_cols, 30), ("셋 다", cross + curve + payovr, cross_cols + curve_cols + payovr_cols, 30)):
        ps, pb = blend([block(pairs, z)], cols)
        out.append(f"\n## {name}")
        out.append("  오차 " + " · ".join(f"{k} {np.mean([stats_line(p, y, v)[0] for p in ps]):.1%}" for k, v in parts.items()))
        for k, v in parts.items():
            d0, lo0, hi0 = paired(pb, ref, y, players, v & ~hold, rng)
            d1, lo1, hi1 = paired(pb, ref, y, players, v & hold, rng)
            out.append(f"  {k:<14} 선택 {d0:+.3f} ({lo0:+.3f}~{hi0:+.3f}) · 확인 {d1:+.3f} ({lo1:+.3f}~{hi1:+.3f})")
        if name == "대안 없음 × 쓰임":
            for w, season in (("루이스-스켈리", "PTG"), ("엔소 페르난데스", "PTG"), ("요주아 키미히", "25UCL"), ("쿠쿠레야", "PTG"), ("리자라쥐", "CH")):
                for i, c in enumerate(data):
                    if w in c["name"] and c["season"] == season:
                        out.append(f"  {season} {c['name']}: 시세 {math.exp(y[i]) / 1e8:.1f} / 지금 {math.exp(ref[i]) / 1e8:.1f} / 넣으면 {math.exp(pb[i]) / 1e8:.1f}")
        save_out()


if __name__ == "__main__":
    sys.exit(main())
