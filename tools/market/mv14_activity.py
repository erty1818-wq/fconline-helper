"""MV-14: is a card being traded? Days on which a card's shown price changed, per grade, from the stored daily prices. Only
whether it changed, never its level, so the card's own price does not leak in. Same half split as the rest of MV-14.
    python tools/market/mv14_activity.py --out artifacts/mv14_activity.txt
"""
import argparse, json, math, os, sqlite3, sys
from collections import defaultdict
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import numpy as np
from mv12 import Block, design, fold_of, floor_of, load
from mv12_pool import unique_cards, pooled_block, colour_block, compat_block, stats_line, paired
from mv12_search import rare_block
from mv14 import match_usage, usage_block
from mv14_fit import curve_block, fit


def daily(db):
    c = sqlite3.connect(Path(db).resolve().as_uri() + "?mode=ro", uri=True)
    days = defaultdict(dict)
    all_days = set()
    for day, sp, prices in c.execute("SELECT day,sp_id,prices FROM price_history ORDER BY day"):
        all_days.add(day)
        days[sp][day] = {int(k): v for k, v in json.loads(prices).items()}
    return days, sorted(all_days)


def activity(rows, grades):
    """Per grade: observed day pairs, and those on which the price changed. A grade missing in the early days just has fewer pairs."""
    out = {}
    for g in grades:
        vals = [p[g] for _, p in sorted(rows.items()) if p.get(g, 0) > 0]
        out[g] = (len(vals) - 1, sum(a != b for a, b in zip(vals, vals[1:])))
    return out


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
    floors = np.array([floor_of(c["prices"], g) for c in data])
    cens = np.array([c["prices"][g] <= floor_of(c["prices"], g) * 1.007 for c in data])
    players = np.array([c["player"] for c in data]); ovr = np.array([c["ovr1"] for c in data], float)
    folds = np.array([fold_of(c["sp_id"]) for c in data])
    hold = np.array([(c["player"] * 2654435761) % 2**32 % 2 == 0 for c in data])
    top = ovr >= 119; free = ~cens; everyone = np.ones(len(y), bool)
    daily_rows, days = daily(a.db)
    out = [f"저장된 날짜 {len(days)}일 ({days[0]} ~ {days[-1]}) · 일별 시세가 있는 카드 {sum(c['sp_id'] in daily_rows for c in data):,}/{len(data):,}장"]
    acts = [activity(daily_rows.get(c["sp_id"], {}), (8, 10, 11)) for c in data]
    def rate(k): return np.array([(u[k][1] + .5) / (max(u[k][0], 0) + 2) for u in acts])
    r11, r10, r8 = rate(11), rate(10), rate(8)
    out.append(f"+11 하루 변화 비율 분포: 10% {np.quantile(r11, .1):.2f} · 중앙 {np.median(r11):.2f} · 90% {np.quantile(r11, .9):.2f} · 관측 일 중앙값 {np.median([u[11][0] for u in acts]):.0f}")
    base = [pooled_block(data, dict(statz=300)), colour_block(data, m), rare_block(data), compat_block(data), usage_block(data, usage_of(a.db), False), curve_block(data)]
    def run(blocks):
        _, X, pen = design(blocks)
        p = np.full(len(y), np.nan)
        for f in range(5):
            tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
            p[te] = fit(X, pen, y, cens, players, tr, te, floors, ovr, data, local=5.0)
        return p
    def row(name, p, ref):
        s = f"  {name:<28} 전체 {stats_line(p, y, everyone)[0]:.1%} · 상위 {stats_line(p, y, top)[0]:.1%} · 상위(하한 제외) {stats_line(p, y, top & free)[0]:.1%}"
        if ref is not None:
            for lab, mk in (("선택", ~hold), ("확인", hold)):
                d, lo, hi = paired(p, ref, y, players, mk, rng)
                s += f" | {lab} {d:+.3f} ({lo:+.3f}~{hi:+.3f})"
        out.append(s)
        Path(a.out).parent.mkdir(parents=True, exist_ok=True)
        Path(a.out).write_text("\n".join(out), encoding="utf-8")
    p0 = run(base)
    row("기준", p0, None)
    for name, cols in (("+11 활발도", [("a11", r11)]), ("+10 활발도", [("a10", r10)]), ("+8·+10·+11 활발도", [("a8", r8), ("a10", r10), ("a11", r11)])):
        for z in (30, 3):
            b = Block()
            for n, v in cols:
                b.add(n, v, z=z); b.add(n + "_log", np.log(v), z=z)
            row(f"{name} z={z}", run(base + [b]), p0)
    q = np.quantile(r11, [.25, .5, .75])
    t = np.digitize(r11, q)
    for i, lab in enumerate(["가장 조용한 25%", "그다음", "그다음", "가장 활발한 25%"]):
        mk = t == i
        out.append(f"  {lab}: {mk.sum():,}장 · 하한 {cens[mk].mean():.0%} · 오차(하한 제외) {stats_line(p0, y, mk & free)[0]:.1%}")
    Path(a.out).write_text("\n".join(out), encoding="utf-8")


def usage_of(db):
    return match_usage(db)[0]


if __name__ == "__main__":
    sys.exit(main())
