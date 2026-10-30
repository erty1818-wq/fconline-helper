"""MV-13: is the model right, or only wrong by less? Two tests of the pooled model that do not depend on its own error.
1. Ranking: unseen cards against simple rules (OVR only; OVR + season), rank correlation and "within 2×".
2. Forward test: fit on an old snapshot, then see whether cards the model called cheap rose more than the others by the
   latest snapshot. If the gap is only noise, cheap cards do not rise. Read-only; every number is an association [추정].
    python tools/market/mv13_signal.py --out artifacts/mv13_signal.txt
"""
import argparse, json, math, os, sqlite3, sys
from collections import Counter, defaultdict
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import numpy as np
from mv12 import design, fit_predict, fold_of, floor_of, load, Block
from mv12_pool import unique_cards, pooled_block, colour_block
from mv12_search import rare_block


def spearman(a, b):
    ra = np.argsort(np.argsort(a)); rb = np.argsort(np.argsort(b))
    return float(np.corrcoef(ra, rb)[0, 1])


def within(p, y, ratio): return float(np.mean(np.abs(p - y) <= math.log(ratio)))


def arrays(data, g):
    y = np.log([c["prices"][g] for c in data])
    cens = np.array([c["prices"][g] <= floor_of(c["prices"], g) * 1.007 for c in data])
    floors = np.array([floor_of(c["prices"], g) for c in data])
    players = np.array([c["player"] for c in data]); ovr = np.array([c["ovr1"] for c in data], float)
    return y, cens, floors, players, ovr


def simple_block(data, seasons):
    b = Block()
    ovr = np.array([c["ovr1"] for c in data], float); d = ovr - ovr.mean()
    b.add("ovr", d, z=1e-3); b.add("ovr2", d * d, z=1e-3)
    if seasons:
        count = Counter(c["season"] for c in data)
        base = count.most_common(1)[0][0]
        for s, n in count.items():
            if s != base and n >= 5: b.add("season:" + s, [c["season"] == s for c in data], z=1e-3)
    return b


def cv(blocks, y, cens, floors, players, ovr, folds, near=None, ridge=0.05):
    _, X, pen = design(blocks)
    p = np.full(len(y), np.nan)
    for f in range(5):
        tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
        p[te], _, _ = fit_predict(X, pen, y, cens, players, tr, te, floors, ovr=ovr, near=near, fit_ridge=ridge)
    return p


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=os.path.expandvars(r"%LOCALAPPDATA%\FcHelper\fchelper.db"))
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    g = 11
    out = []
    now = load(a.db)
    data = unique_cards(now, g, 115)
    y, cens, floors, players, ovr = arrays(data, g)
    folds = np.array([fold_of(c["sp_id"]) for c in data])
    free = ~cens; top = ovr >= 119
    rows = {
        "OVR만": cv([simple_block(data, False)], y, cens, floors, np.arange(len(y)) * -1 - 1, ovr, folds),
        "OVR + 시즌": cv([simple_block(data, True)], y, cens, floors, np.arange(len(y)) * -1 - 1, ovr, folds),
        "OVR + 시즌 + 같은 선수": cv([simple_block(data, True)], y, cens, floors, players, ovr, folds, near=3.0),
        "지금 모델": cv([pooled_block(data, dict(statz=300)), colour_block(data, now), rare_block(data)], y, cens, floors, players, ovr, folds, near=3.0),
    }
    out.append(f"## 1. 처음 보는 카드 {len(y):,}장 (+11, +1 OVR 115+). 왼쪽 전체 / 오른쪽 상위(+1 OVR 119+)이면서 하한 아닌 카드 {(top & free).sum():,}장")
    out.append("  모델 | 중앙오차 | 2배 안 적중 | 1.5배 안 | 시세 순위 상관")
    mid = float(np.median(y))
    out.append(f"  (참고) 전부 시세 중앙값으로 찍기: 2배 안 {within(np.full(len(y), mid), y, 2):.0%}, 상위 {within(np.full(len(y), mid), y, 2) if False else within(np.full((top & free).sum(), float(np.median(y[top & free]))), y[top & free], 2):.0%}")
    for name, p in rows.items():
        m = top & free
        out.append(f"  {name}: 오차 {np.median(np.abs(np.exp(p - y) - 1)):.0%}/{np.median(np.abs(np.exp(p[m] - y[m]) - 1)):.0%} · 2배 안 {within(p, y, 2):.0%}/{within(p[m], y[m], 2):.0%} · 1.5배 안 {within(p, y, 1.5):.0%}/{within(p[m], y[m], 1.5):.0%} · 순위상관 {spearman(p, y):.3f}/{spearman(p[m], y[m]):.3f}")
    # 2. Forward tests from each older snapshot.
    out.append("\n## 2. 예전 스냅샷의 예상가로 나중 시세 맞히기 (모델이 싸다고 한 카드가 실제로 올랐나)")
    rng = np.random.default_rng(13)
    for s in [x for x in now["snaps"][:-1]]:
        old = load(a.db, s)
        od = unique_cards(old, g, 115)
        oy, oc, of, op, oo = arrays(od, g)
        ofolds = np.array([fold_of(c["sp_id"]) for c in od])
        pred = cv([pooled_block(od, dict(statz=300)), colour_block(od, old), rare_block(od)], oy, oc, of, op, oo, ofolds, near=3.0)
        later = {c["sp_id"]: c["prices"].get(g, 0) for c in data}
        idx = [i for i, c in enumerate(od) if later.get(c["sp_id"], 0) > 1200 and not oc[i]]
        if len(idx) < 100: continue
        idx = np.array(idx)
        gap = pred[idx] - oy[idx]  # + = the model thought the card was cheap
        ret = np.array([math.log(later[od[i]["sp_id"]]) for i in idx]) - oy[idx]
        slope = float(np.polyfit(gap, ret, 1)[0])
        ids = np.array([od[i]["player"] for i in idx]); u = np.unique(ids); ix = {v: np.where(ids == v)[0] for v in u}
        boots = []
        for _ in range(400):
            pk = np.concatenate([ix[v] for v in rng.choice(u, len(u))])
            boots.append(np.polyfit(gap[pk], ret[pk], 1)[0])
        lo, hi = np.percentile(boots, [2.5, 97.5])
        q = np.quantile(gap, [.2, .8])
        cheap, dear = ret[gap >= q[1]], ret[gap <= q[0]]
        flagged = ret[gap >= math.log(1 / 0.7)]  # at least 30% under the expectation
        out.append(f"  스냅샷 {s} → {now['snapshot']}: {len(idx):,}장 · 전체 변화 중앙 {np.median(ret):+.1%}(log)")
        out.append(f"    가장 싸다고 본 20% 중앙 {np.median(cheap):+.1%} · 가장 비싸다고 본 20% {np.median(dear):+.1%} · 30%+ 싸다고 본 {len(flagged)}장 {np.median(flagged):+.1%}")
        out.append(f"    차이 1(log)당 이후 변화 {slope:+.3f} (95% {lo:+.3f}~{hi:+.3f}) · 0이면 신호 없음, 1이면 예상가까지 전부 수렴")
    Path(a.out).parent.mkdir(parents=True, exist_ok=True)
    Path(a.out).write_text("\n".join(out), encoding="utf-8")


if __name__ == "__main__":
    sys.exit(main())
