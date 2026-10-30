"""MV-12: second-round candidates on the pooled model. Each is compared with the current best on the same unseen
cards; only what also improves the footballers it was not chosen on is kept.
    python tools/market/mv12_search.py --out artifacts/mv12_search.txt
"""
import argparse, math, os, sys
from collections import Counter
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import numpy as np
import mv12
from mv12 import Block, design, fit_predict, fold_of, floor_of, load
from mv12_pool import unique_cards, pooled_block, colour_block, rating_block, compat_block, level_block, stats_line, paired


def colour_dummies(data, m, top=40, z=30):
    b = Block()
    count = Counter(t for c in data for t in c["colours"])
    for t, n in count.most_common(top):
        if n >= 12: b.add(f"colour:{m['kinds'][t][1]}", [t in c["colours"] for c in data], z=z)
    return b


def feature_colours(m, data, z=100):
    import sqlite3
    conn = sqlite3.connect(Path(os.path.expandvars(r"%LOCALAPPDATA%\FcHelper\fchelper.db")).resolve().as_uri() + "?mode=ro", uri=True)
    ids = {c["sp_id"]: i for i, c in enumerate(data)}
    mem = {}
    for tc, sp in conn.execute("SELECT team_color,sp_id FROM team_color_member"):
        if m["kinds"].get(tc, ("", ""))[0] == "Feature" and sp in ids:
            mem.setdefault(tc, set()).add(sp)
    conn.close()
    b = Block()
    for tc, sps in sorted(mem.items(), key=lambda kv: -len(kv[1]))[:60]:
        if len(sps) >= 15: b.add(f"feature:{tc}", [c["sp_id"] in sps for c in data], z=z)
    return b


def rare_block(data):
    seasons = Counter(c["season"] for c in data)
    b = Block()
    b.add("season:rare", [seasons[c["season"]] < 5 for c in data], z=1e-3)
    return b


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=os.path.expandvars(r"%LOCALAPPDATA%\FcHelper\fchelper.db"))
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    m = load(a.db); g = 11
    rng = np.random.default_rng(3)
    data = unique_cards(m, g, 115)
    y = np.log([c["prices"][g] for c in data])
    cens = np.array([c["prices"][g] <= floor_of(c["prices"], g) * 1.007 for c in data])
    floors = np.array([floor_of(c["prices"], g) for c in data])
    players = np.array([c["player"] for c in data]); ovr = np.array([c["ovr1"] for c in data], float)
    folds = np.array([fold_of(c["sp_id"]) for c in data])
    hold = np.array([(c["player"] * 2654435761) % 2**32 % 2 == 0 for c in data])
    top = np.array([c["ovr1"] >= 119 for c in data]); free = ~cens
    def run(blocks, **kw):
        _, X, pen = design(blocks)
        p = np.full(len(y), np.nan)
        for f in range(5):
            tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
            p[te], _, _ = fit_predict(X, pen, y, cens, players, tr, te, floors, ovr=ovr, **kw)
        return p
    out = []
    def row(name, p, ref):
        s = f"  {name:<34} 전체 {stats_line(p, y, np.ones(len(y), bool))[0]:.1%} · 상위 {stats_line(p, y, top)[0]:.1%} · 상위(하한 제외) {stats_line(p, y, top & free)[0]:.1%}"
        if ref is not None:
            for lab, mk in (("선택", ~hold), ("확인", hold)):
                d, lo, hi = paired(p, ref, y, players, mk, rng)
                s += f" | {lab} {d:+.3f} ({lo:+.3f}~{hi:+.3f})"
        out.append(s)
    base_opts = dict(statz=300)
    best = [pooled_block(data, base_opts), colour_block(data, m), rare_block(data)]
    p_best = run(best)
    out.append("기준: 통합 + statz 300 + 팀컬러 인기·깊이 + 희귀 시즌")
    row("기준", p_best, None)
    trials = {
        "statz=1000": ([pooled_block(data, dict(statz=1000)), colour_block(data, m), rare_block(data)], {}),
        "statz=3000": ([pooled_block(data, dict(statz=3000)), colour_block(data, m), rare_block(data)], {}),
        "colour z=10": ([best[0], colour_block(data, m, z=10), best[2]], {}),
        "colour z=100": ([best[0], colour_block(data, m, z=100), best[2]], {}),
        "+ 소속 팀컬러 각각": (best + [colour_dummies(data, m)], {}),
        "+ 특성 팀컬러 각각": (best + [feature_colours(m, data)], {}),
        "+ 특성×능력치 궁합": (best + [compat_block(data)], {}),
        "+ 능력치 수준·키": (best + [level_block(data)], {}),
        "+ 평점·평점 수": (best + [rating_block(data)], {}),
        "가까운 OVR 카드 우대 τ=3": (best, dict(near=3.0)),
        "가까운 OVR 카드 우대 τ=6": (best, dict(near=6.0)),
        "가까운 OVR 카드 우대 τ=12": (best, dict(near=12.0)),
        "선수 효과 학습 릿지 1": (best, dict(fit_ridge=1.0)),
        "선수 효과 학습 릿지 .01": (best, dict(fit_ridge=0.01)),
    }
    for name, (blocks, kw) in trials.items():
        row(name, run(blocks, **kw), p_best)
    Path(a.out).write_text("\n".join(out), encoding="utf-8")


if __name__ == "__main__":
    sys.exit(main())
