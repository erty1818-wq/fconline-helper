"""MV-14: top cards with a trait stay the hardest. Which way are they wrong, for which traits, and does anything confirmed help?
Tests a heavier tree share on trait cards, more trait columns for the trees (the card's trait set, the trait × stat pairs the user
named, stat levels at +11), and a higher ridge-free trait slope. Same half split as the rest of MV-14.
    python tools/market/mv14_traits.py --out artifacts/mv14_traits.txt
"""
import argparse, math, os, sys
from collections import Counter
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import numpy as np
from mv12 import COMPAT, design, fold_of, floor_of, load
from mv12_pool import unique_cards, pooled_block, colour_block, compat_block, stats_line, paired
from mv12_search import rare_block
from mv14 import match_usage, usage_block
from mv14_blend import plain_columns, boosted
from mv14_fit import fit

HOT = ["라인 브레이커", "크로스 포쳐", "트릭스터", "스피드스터"]


def trait_columns(data):
    """What a tree needs to tell one trait card from another: stat levels at the top grade, and the pairs the traits' names suggest."""
    rows = []
    for c in data:
        at11 = lambda s: c["stats"].get(s, c["ovr1"]) + 21
        row = [at11(s) for s in ("sprintspeed", "acceleration", "agility", "balance", "dribbling", "finishing", "positioning", "headingaccuracy", "jumping", "strength", "volleys", "shortpassing")]
        row += [sum(t in c["traits"] for t in HOT)]
        for t, stats in COMPAT.items():
            if t in HOT:
                row.append(np.mean([c["stats"].get(s, 0) - (0 if s == "height" else c["ovr1"]) for s in stats]) if t in c["traits"] else np.nan)
        rows.append(row)
    return np.array(rows, float)


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
    has = np.array([bool(c["traits"] & set(HOT)) for c in data])
    tt = top & free & has
    usage, _ = match_usage(a.db)
    base = [pooled_block(data, dict(statz=300)), colour_block(data, m), rare_block(data), compat_block(data), usage_block(data, usage, False)]
    _, X, pen = design(base)
    lin = np.full(len(y), np.nan)
    for f in range(5):
        tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
        lin[te] = fit(X, pen, y, cens, players, tr, te, floors, ovr, data)
    F = plain_columns(data, m, usage, None)
    b0 = boosted(F, y, players, ovr, seasons, folds, lf, 200, 15, .05)
    p0 = np.maximum(.7 * lin + .3 * b0, lf)
    out = []
    def row(name, p, ref):
        s = f"  {name:<34} 전체 {stats_line(p, y, everyone)[0]:.1%} · 상위 {stats_line(p, y, top)[0]:.1%} · 상위(하한 제외) {stats_line(p, y, top & free)[0]:.1%} · 인기 특성 {stats_line(p, y, tt)[0]:.1%}"
        if ref is not None:
            for lab, mk in (("선택", ~hold), ("확인", hold)):
                d, lo, hi = paired(p, ref, y, players, mk & has & top, rng)
                s += f" | {lab} {d:+.3f} ({lo:+.3f}~{hi:+.3f})"
        out.append(s)
        Path(a.out).parent.mkdir(parents=True, exist_ok=True)
        Path(a.out).write_text("\n".join(out), encoding="utf-8")
    out.append(f"인기 특성(라인 브레이커·크로스 포쳐·트릭스터·스피드스터) 상위·하한 제외 카드 {tt.sum():,}장")
    row("지금 (선형 + 나무 30%)", p0, None)
    out.append("\n## 어느 쪽으로 틀리나 (실제/예상, 중앙값)")
    r = y - p0
    for t in HOT:
        mk = np.array([t in c["traits"] for c in data]) & top & free
        out.append(f"  {t}: {mk.sum()}장 · 오차 {stats_line(p0, y, mk)[0]:.1%} · 치우침 {math.exp(np.median(r[mk])) - 1:+.0%}")
    for lab, lo, hi in (("OVR 119~122", 119, 122), ("OVR 123~126", 123, 126), ("OVR 127+", 127, 200)):
        mk = tt & (ovr >= lo) & (ovr <= hi)
        if mk.sum() >= 10: out.append(f"  {lab}: {mk.sum()}장 · 오차 {stats_line(p0, y, mk)[0]:.1%} · 치우침 {math.exp(np.median(r[mk])) - 1:+.0%}")
    for n, lab in ((1, "인기 특성 1개"), (2, "2개"), (3, "3개 이상")):
        mk = tt & np.array([(len(c["traits"] & set(HOT)) == n) if n < 3 else len(c["traits"] & set(HOT)) >= 3 for c in data])
        if mk.sum() >= 10: out.append(f"  {lab}: {mk.sum()}장 · 오차 {stats_line(p0, y, mk)[0]:.1%} · 치우침 {math.exp(np.median(r[mk])) - 1:+.0%}")
    seen = Counter(c["player"] for c in data)
    for lab, mk2 in (("다른 카드 없음", np.array([seen[c["player"]] == 1 for c in data])), ("다른 카드 있음", np.array([seen[c["player"]] > 1 for c in data]))):
        mk = tt & mk2
        out.append(f"  {lab}: {mk.sum()}장 · 오차 {stats_line(p0, y, mk)[0]:.1%}")
    bad = Counter(c["season"] for c, m_ in zip(data, tt) if m_)
    for s, n in bad.most_common(10):
        mk = tt & (seasons == s)
        out.append(f"  {s}: {n}장 · 오차 {stats_line(p0, y, mk)[0]:.1%} · 치우침 {math.exp(np.median(r[mk])) - 1:+.0%}")
    out.append("\n## 시험")
    for w in (.45, .6):
        row(f"인기 특성 카드만 나무 {w:.0%}", np.maximum(np.where(has, (1 - w) * lin + w * b0, p0), lf), p0)
    b1 = boosted(np.column_stack([F, trait_columns(data)]), y, players, ovr, seasons, folds, lf, 200, 15, .05)
    row("나무 열 추가 (강화 후 능력치·궁합) 30%", np.maximum(.7 * lin + .3 * b1, lf), p0)
    row("나무 열 추가 45%", np.maximum(.55 * lin + .45 * b1, lf), p0)
    # A separate tree for the trait cards' leftover, fitted on other folds' errors.
    out.append("\n## 잔차 보정: 다른 묶음의 오차로 특성 카드만 보정")
    corr = np.zeros(len(y))
    for f in range(5):
        tr = np.where((folds != f) & has & top & free)[0]; te = np.where((folds == f) & has & top)[0]
        A = np.column_stack([np.ones(len(tr)), ovr[tr] - 122, [len(data[i]["traits"] & set(HOT)) for i in tr]])
        beta = np.linalg.solve(A.T @ A + np.eye(3), A.T @ (y[tr] - p0[tr]))
        Z = np.column_stack([np.ones(len(te)), ovr[te] - 122, [len(data[i]["traits"] & set(HOT)) for i in te]])
        corr[te] = Z @ beta
    row("OVR·특성 수별 치우침 보정", np.maximum(p0 + corr, lf), p0)


if __name__ == "__main__":
    sys.exit(main())
