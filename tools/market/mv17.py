"""MV-17: a +11 price nobody trades at is not a market price (the user: 26TOTY 뎀벨레 has no +11 stock, 26TOTS has, and TOTY and TOTS
seasons have this most). The data center's daily price moves only on days a card sold. How many cards is that, in which seasons, does
it explain the error, and does the fit improve when it stops learning from such prices or knows about them? Needs mv17_harvest.py's
output. Same half split, three tree seeds, paired. Read-only.
    python tools/market/mv17.py --db <copy of fchelper.db> --graph artifacts/mv17_graph.json --out artifacts/mv17.txt
"""
import argparse, json, math, os, sys
from collections import defaultdict
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import numpy as np
from mv12 import Block, design, fold_of, floor_of, load
from mv12_pool import unique_cards, pooled_block, colour_block, compat_block, stats_line, paired
from mv12_search import rare_block
from mv14 import usage_block, match_usage
from mv14_blend import plain_columns
from mv14_fit import fit
from mv15 import TREE_WEIGHT
from mv15_grade import read_matches, count, columns
from mv15_pick import forest


def changes(prices, days):
    last = prices[-(days + 1):]
    return sum(a != b for a, b in zip(last, last[1:]))


def quiet_days(prices):
    n = 0
    for a, b in zip(prices[::-1], prices[-2::-1]):
        if a != b: break
        n += 1
    return n


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=os.path.expandvars(r"%LOCALAPPDATA%\FcHelper\fchelper.db"))
    ap.add_argument("--graph", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    g = 11
    m = load(a.db)
    rng = np.random.default_rng(17)
    graph = json.loads(Path(a.graph).read_text(encoding="utf-8"))
    data = [c for c in unique_cards(m, g, 115) if graph.get(str(c["sp_id"]))]
    y = np.log([c["prices"][g] for c in data])
    floors = np.array([floor_of(c["prices"], g) for c in data]); lf = np.log(np.maximum(floors, 1))
    cens = np.array([c["prices"][g] <= floor_of(c["prices"], g) * 1.007 for c in data])
    players = np.array([c["player"] for c in data]); ovr = np.array([c["ovr1"] for c in data], float)
    seasons = np.array([c["season"] for c in data])
    folds = np.array([fold_of(c["sp_id"]) for c in data])
    hold = np.array([(c["player"] * 2654435761) % 2**32 % 2 == 0 for c in data])
    series = [graph[str(c["sp_id"])] for c in data]
    days = np.array([len(v) for v in series], float)
    c30 = np.array([changes(v, 30) for v in series], float)
    c7 = np.array([changes(v, 7) for v in series], float)
    quiet = np.array([quiet_days(v) for v in series], float)
    usage, _ = match_usage(a.db)
    h = columns(data, count(read_matches(a.db)))
    free = ~cens; no_trait = np.array([not c["traits"] for c in data])
    new = days < 14
    thin = ~new & (c30 < 8)  # the app's CardLiquidity.MinChanges
    frozen = ~new & (c7 == 0) & ~thin
    live = ~thin & ~frozen
    out = []
    def save():
        Path(a.out).parent.mkdir(parents=True, exist_ok=True)
        Path(a.out).write_text("\n".join(out), encoding="utf-8")
    out.append(f"카드 {len(y):,}장 · 하한 {cens.mean():.0%}")
    out.append(f"거래 없음(30일 변동 8일 미만) {thin.sum():,}장 · 최근 7일 멈춤 {frozen.sum():,}장 · 거래 있음 {live.sum():,}장 · 2주 미만 새 카드 {new.sum():,}장")
    def block(pairs, z=30):
        b = Block()
        for k, v in pairs: b.add(k, v, z=z)
        return b
    base = [pooled_block(data, dict(statz=300)), colour_block(data, m), rare_block(data), compat_block(data), usage_block(data, usage, False), block([("n11", h["log11"]), ("n11_any", h["n11"] > 0)])]
    F0 = np.column_stack([plain_columns(data, m, usage, None), h["log11"]])
    def run(extra=None, cols=None, train_mask=None, seeds=(7, 11, 23)):
        _, X, pen = design(base + ([block(extra)] if extra else []))
        F1 = np.column_stack([F0] + cols) if cols else F0
        pl = np.full(len(y), np.nan); pts = {s: np.full(len(y), np.nan) for s in seeds}
        for f in range(5):
            tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
            if train_mask is not None: tr = tr[train_mask[tr]]
            pl[te] = fit(X, pen, y, cens, players, tr, te, floors, ovr, data)
            for s in seeds: pts[s][te] = forest(F1, y, players, ovr, seasons, tr, te, lf, s)
        ps = [np.maximum((1 - TREE_WEIGHT) * pl + TREE_WEIGHT * pts[s], lf) for s in seeds]
        return ps, np.mean(ps, axis=0)
    rank = np.argsort(-h["n"])
    top300 = np.zeros(len(y), bool); top300[rank[:300]] = True
    parts = {"전체": np.ones(len(y), bool), "실사용": ovr >= 119, "120+특성있음": (ovr >= 120) & free & ~no_trait, "120+특성없음": (ovr >= 120) & free & no_trait, "경기300": top300,
             "거래있음": live, "거래있음 실사용": live & (ovr >= 119), "거래있음 120+": live & (ovr >= 120) & free, "거래없음": thin}
    ref_s, ref = run()
    out.append("\n## 앱 지금")
    out.append("  " + " · ".join(f"{k} {np.mean([stats_line(p, y, v)[0] for p in ref_s]):.1%}({v.sum()})" for k, v in parts.items()))
    r = y - ref
    out.append("\n## 거래 정도별 오차 (양수 = 시세가 예상보다 비쌈)")
    for lab, mk in (("거래 없음", thin), ("최근 7일 멈춤", frozen), ("거래 있음", live), ("매일 움직임(25일+)", ~new & (c30 >= 25)), ("새 카드", new)):
        if mk.sum() < 5: continue
        fr = mk & free
        out.append(f"  {lab}: {mk.sum():,}장 · 하한 {cens[mk].mean():.0%} · 오차 {stats_line(ref, y, mk)[0]:.1%} · 하한 제외 {fr.sum():,}장 {stats_line(ref, y, fr)[0] if fr.sum() else 0:.1%} · 잔차 중앙 {math.exp(np.median(r[fr])) - 1 if fr.sum() else 0:+.0%} · 표준편차 {r[fr].std() if fr.sum() else 0:.2f} · 중앙 시세 {math.exp(np.median(y[mk])) / 1e8:.1f}억")
    out.append("\n## 시즌별 거래 없는 몫 (하한 제외, 카드 10장 이상, 많은 순)")
    by = defaultdict(list)
    for i, c in enumerate(data):
        if free[i]: by[c["season"]].append(i)
    rows = sorted(((thin[v].mean() + frozen[v].mean(), s, v) for s, v in by.items() if len(v) >= 10), key=lambda t: -t[0])
    for share, s, v in rows[:14]:
        out.append(f"  {s}: {len(v)}장 · 거래 없음 {thin[v].mean():.0%} · 7일 멈춤 {frozen[v].mean():.0%} · 중앙 시세 {math.exp(np.median(y[v])) / 1e8:.0f}억")
    all_share = thin[free].mean()
    out.append(f"  전체(하한 제외): 거래 없음 {all_share:.0%} · 7일 멈춤 {frozen[free].mean():.0%}")
    save()
    def report(name, ps, pb):
        out.append(f"\n## {name}")
        out.append("  오차 " + " · ".join(f"{k} {np.mean([stats_line(p, y, v)[0] for p in ps]):.1%}" for k, v in parts.items()))
        for k, v in parts.items():
            d0, lo0, hi0 = paired(pb, ref, y, players, v & ~hold, rng)
            d1, lo1, hi1 = paired(pb, ref, y, players, v & hold, rng)
            out.append(f"  {k:<14} 선택 {d0:+.3f} ({lo0:+.3f}~{hi0:+.3f}) · 확인 {d1:+.3f} ({lo1:+.3f}~{hi1:+.3f})")
        save()
    cols = [np.log1p(c30), np.log1p(c7), np.log1p(quiet)]
    tradable = (~thin & ~frozen) | cens
    report("거래 없는 시세로 학습하지 않음", *run(train_mask=tradable))
    report("7일 멈춤은 남기고 거래 없음만 뺌", *run(train_mask=~thin | cens))
    report("거래 정도를 열로", *run([("c30", np.log1p(c30)), ("c7", np.log1p(c7)), ("quiet", np.log1p(quiet)), ("thin", thin), ("frozen", frozen)], cols))
    report("거래 정도 열 + 학습 제외", *run([("c30", np.log1p(c30)), ("c7", np.log1p(c7)), ("quiet", np.log1p(quiet)), ("thin", thin), ("frozen", frozen)], cols, train_mask=tradable))
    save()


if __name__ == "__main__":
    sys.exit(main())
