"""MV-14: where the remaining error is and how far the fit itself can go. Splits the residual into what persists (the card's
own unexplained value) and what does not (price noise), then tests the fit: robust loss, a wider training pool, a flexible OVR
curve, cleaner training targets, and which sibling cards to trust. Chosen on half the footballers, confirmed on the other half.
    python tools/market/mv14_fit.py --out artifacts/mv14_fit.txt
"""
import argparse, math, os, sys
from collections import defaultdict
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import numpy as np
from mv12 import Block, design, fold_of, floor_of, load, mills
from mv12_pool import unique_cards, pooled_block, colour_block, compat_block, stats_line, paired
from mv12_search import rare_block
from mv14 import match_usage, usage_block


def fit(X, pen, y, cens, players, train, test, floors, ovr, data, near=3.0, huber=None, target=None, trait_t=0.0, fam_f=0.0, local=None):
    """mv12.fit_predict plus: a Huber loss by pseudo-values, a cleaner training target, siblings weighted by likeness, and a
    second look at the training residuals of nearby cards of the same season."""
    x = X[train]
    mu, sd = x.mean(0), x.std(0)
    sd[sd < 1e-12] = 1
    z = np.column_stack([np.ones(len(x)), (x - mu) / sd])
    k = z.shape[1]
    pz = np.array([1e-9] + [p[1] if p[0] == "z" else p[1] / (s * s) for p, s in zip(pen, sd)])
    uniq, inv, counts = np.unique(players[train], return_inverse=True, return_counts=True)
    several = counts >= 2
    a = z.T @ z + np.diag(pz)
    bsum = np.zeros((len(uniq), k))
    np.add.at(bsum, inv, z)
    d = counts + 0.05
    a -= (bsum[several] / d[several, None]).T @ bsum[several]
    ainv = np.linalg.inv(a)
    w = z.copy()
    m = several[inv]
    w[m] -= bsum[inv[m]] / d[inv[m], None]
    yt = (target if target is not None else y)[train]
    ct = cens[train]
    yi = yt.copy()
    def solve(t):
        b = ainv @ (w.T @ t)
        r = t - z @ b
        e = np.bincount(inv, weights=r, minlength=len(uniq)) / d
        e[~several] = 0
        return b, e[inv]
    b, e = solve(yi)
    sigma = max(float(np.std((yt - z @ b - e)[~ct])), .05)
    for it in range(40):
        p = z @ b + e
        t = (yt[ct] - p[ct]) / sigma
        ml = mills(t)
        yi[ct] = p[ct] - sigma * ml
        var = sigma * sigma * np.maximum(1 - t * ml - ml * ml, 1e-6)
        if huber is not None:
            r = yt[~ct] - p[~ct]
            scale = max(1.4826 * float(np.median(np.abs(r))), .05)
            yi[~ct] = p[~ct] + np.clip(r, -huber * scale, huber * scale)
        nb, ne = solve(yi)
        r = yi - z @ nb - ne
        sigma = max(math.sqrt((r @ r + var.sum()) / len(r)), .05)
        done = np.max(np.abs(nb - b)) < 1e-5
        b, e = nb, ne
        if done: break
    residual = yi - z @ b
    sums = np.bincount(inv, weights=residual, minlength=len(uniq))
    tau2 = float(np.mean((sums[several] / counts[several]) ** 2 - sigma * sigma / counts[several])) if several.sum() >= 10 else 0
    shrink = float(np.clip(sigma * sigma / tau2, .1, 10)) if tau2 > 1e-4 else 10
    rows = defaultdict(list)
    for i, r in zip(train, residual): rows[players[i]].append((i, r))
    eff = []
    for i in test:
        v = rows.get(players[i], [])
        ws = [math.exp(-abs(ovr[j] - ovr[i]) / near - trait_t * len(data[j]["traits"] ^ data[i]["traits"]) - fam_f * (data[j]["family"] != data[i]["family"])) for j, _ in v]
        eff.append(sum(a * r for a, (_, r) in zip(ws, v)) / (sum(ws) + shrink) if v else 0.0)
    pred = np.column_stack([np.ones(len(test)), (X[test] - mu) / sd]) @ b + np.array(eff)
    if local is not None:
        # What the fit leaves in nearby cards of the same season.
        pe = sums / (counts + shrink)
        rest = residual - pe[inv]
        by = defaultdict(list)
        for i, r in zip(train, rest): by[data[i]["season"]].append((ovr[i], r))
        pred += np.array([sum(math.exp(-abs(o - ovr[i]) / 2) * r for o, r in by[data[i]["season"]]) / (sum(math.exp(-abs(o - ovr[i]) / 2) for o, r in by[data[i]["season"]]) + local) if by[data[i]["season"]] else 0.0 for i in test])
    return np.maximum(pred, np.log(np.maximum(floors[test], 1)))


def curve_block(data, knots=(117, 120, 123, 126), z=3):
    b = Block()
    ovr = np.array([c["ovr1"] for c in data], float)
    for k in knots: b.add(f"ovr>{k}", np.maximum(0, ovr - k), z=z)
    for line in ("ATT", "MID", "DEF"):
        h = np.array([c["line"] == line for c in data], float)
        b.add(f"ovr>120×{line}", h * np.maximum(0, ovr - 120), z=z * 10)
    return b


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=os.path.expandvars(r"%LOCALAPPDATA%\FcHelper\fchelper.db"))
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    g = 11
    m = load(a.db)
    rng = np.random.default_rng(14)
    out = []
    def save():
        Path(a.out).parent.mkdir(parents=True, exist_ok=True)
        Path(a.out).write_text("\n".join(out), encoding="utf-8")
    data = unique_cards(m, g, 115)
    y = np.log([c["prices"][g] for c in data])
    floors = np.array([floor_of(c["prices"], g) for c in data])
    cens = np.array([c["prices"][g] <= floor_of(c["prices"], g) * 1.007 for c in data])
    players = np.array([c["player"] for c in data]); ovr = np.array([c["ovr1"] for c in data], float)
    folds = np.array([fold_of(c["sp_id"]) for c in data])
    hold = np.array([(c["player"] * 2654435761) % 2**32 % 2 == 0 for c in data])
    top = ovr >= 119; free = ~cens
    usage, _ = match_usage(a.db)
    base = [pooled_block(data, dict(statz=300)), colour_block(data, m), rare_block(data), compat_block(data), usage_block(data, usage, False)]
    _, X, pen = design(base)
    def run(X_=None, pen_=None, cen=None, flo=None, **kw):
        X1 = X if X_ is None else X_; pen1 = pen if pen_ is None else pen_
        c1 = cens if cen is None else cen; f1 = floors if flo is None else flo
        p = np.full(len(y), np.nan)
        for f in range(5):
            tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
            p[te] = fit(X1, pen1, y, c1, players, tr, te, f1, ovr, data, **kw)
        return p
    def row(name, p, ref):
        s = f"  {name:<34} 전체 {stats_line(p, y, np.ones(len(y), bool))[0]:.1%} · 상위 {stats_line(p, y, top)[0]:.1%} · 상위(하한 제외) {stats_line(p, y, top & free)[0]:.1%}"
        if ref is not None:
            for lab, mk in (("선택", ~hold), ("확인", hold)):
                d, lo, hi = paired(p, ref, y, players, mk, rng)
                s += f" | {lab} {d:+.3f} ({lo:+.3f}~{hi:+.3f})"
        out.append(s); save()
    p0 = run()
    out.append(f"카드 {len(y):,}장 · 하한 {cens.mean():.0%}\n\n## 잔차 나누기: 오래 남는 몫 = 카드 고유 값 (설명 가능), 사라지는 몫 = 시세 잡음 (누구도 못 맞힘)")
    r0 = y - p0
    ms = free
    out.append(f"  지금 잔차 표준편차(하한 제외): {r0[ms].std():.3f} · 오차 중앙값 {np.median(np.abs(np.exp(r0[ms]) - 1)):.1%}")
    for s in sorted(m["prev"]):
        prices = m["prev"][s]
        old = np.array([prices.get((c["grp"], c["sp_id"]), {}).get(g, 0) for c in data], float)
        have = old > 1200
        if have.sum() < 500: continue
        fl = np.array([floor_of(prices.get((c["grp"], c["sp_id"]), {}), g) for c in data])
        co = have & (old <= fl * 1.007)
        yo = np.where(have, np.log(np.maximum(old, 1)), y)
        po = np.full(len(y), np.nan)
        for f in range(5):
            tr, te = np.where((folds != f) & have)[0], np.where((folds == f) & have)[0]
            if len(te): po[te] = fit(X, pen, yo, co, players, tr, te, fl, ovr, data)
        ok = have & ~co & free & ~np.isnan(po)
        r1 = yo - po
        rho = float(np.corrcoef(r0[ok], r1[ok])[0, 1])
        noise = r0[ok].std() * math.sqrt(max(1 - rho, 0))
        out.append(f"  스냅샷 {s}: {ok.sum():,}장 · 잔차 상관 {rho:.2f} · 사라지는 몫 표준편차 {noise:.3f} → 그 잡음만 남을 때 오차 중앙값 {math.exp(.6745 * noise) - 1:.1%}")
    tens = np.array([c["prices"].get(10, 0) for c in data], float)
    has10 = tens > 1200
    if has10.sum() > 500:
        y10 = np.where(has10, np.log(np.maximum(tens, 1)), y)
        fl10 = np.array([floor_of(c["prices"], 10) for c in data])
        c10 = has10 & (tens <= fl10 * 1.007)
        p10 = np.full(len(y), np.nan)
        for f in range(5):
            tr, te = np.where((folds != f) & has10)[0], np.where((folds == f) & has10)[0]
            p10[te] = fit(X, pen, y10, c10, players, tr, te, fl10, ovr, data)
        ok = has10 & ~c10 & free & ~np.isnan(p10)
        out.append(f"  +10과 +11 잔차 상관 {np.corrcoef(r0[ok], (y10 - p10)[ok])[0, 1]:.2f} ({ok.sum():,}장)")
    save()
    out.append("\n## 진단: 그 카드의 다른 강화 시세를 알면 (순환 논리라 가치 판단엔 못 씀, 도달 가능한 바닥 확인용)")
    for name, gs in (("자기 +10", [10]), ("자기 +8·+10", [8, 10]), ("자기 +5·+8·+10", [5, 8, 10])):
        b = Block()
        for k in gs:
            v = np.array([math.log(max(c["prices"].get(k, 1), 1)) for c in data])
            b.add(f"own{k}", v - v.mean(), z=1e-3)
        _, X2, pen2 = design(base + [b])
        row(name, run(X2, pen2), None)
    out.append("\n## 적합 방식")
    row("기준 (앱 + 경기 사용량)", p0, None)
    best_h = None
    for h in (2.0, 1.345, 0.8):
        p = run(huber=h); row(f"Huber k={h} (튀는 카드 덜 믿기)", p, p0)
    _, Xc, penc = design(base + [curve_block(data)])
    row("OVR 곡선 유연하게", run(Xc, penc), p0)
    # Cleaner training target: other prices of the same card in training rows only; test rows are never touched by it.
    y10 = np.array([math.log(c["prices"][10]) if c["prices"].get(10, 0) > 1200 else np.nan for c in data])
    gap = np.nanmedian(y[free] - y10[free])
    for lam in (0.3, 0.5):
        t = np.where(np.isnan(y10) | cens, y, (1 - lam) * y + lam * (y10 + gap))
        row(f"학습 목표에 +10 섞기 {lam}", run(target=t), p0)
    recent = sorted(m["prev"])[-2:]
    olds = [np.array([prices_get(m["prev"][s], c, g) for c in data]) for s in recent]
    drift = [np.nanmedian(y[free] - o[free]) for o in olds]
    avg = np.nanmean(np.stack([y] + [o + d for o, d in zip(olds, drift)]), axis=0)
    row(f"학습 목표를 최근 {len(recent) + 1}회 평균으로", run(target=np.where(cens, y, avg)), p0)
    out.append("\n## 어떤 형제 카드를 믿을까")
    for near in (2.0, 5.0, 8.0):
        row(f"OVR 거리 {near}", run(near=near), p0)
    for t in (0.1, 0.3):
        row(f"특성 다르면 덜 믿기 {t}", run(trait_t=t), p0)
    for f_ in (0.3, 1.0):
        row(f"포지션 다르면 덜 믿기 {f_}", run(fam_f=f_), p0)
    for lc in (5.0, 15.0):
        row(f"같은 시즌 가까운 카드 잔차 λ={lc}", run(local=lc), p0)
    out.append("\n## 학습 범위 넓히기 (평가는 같은 카드 115+, 아래 카드는 형제 정보로만)")
    for cut in (110, 105, 100):
        wide = unique_cards(m, g, cut)
        yw = np.log([c["prices"][g] for c in wide])
        flw = np.array([floor_of(c["prices"], g) for c in wide])
        cw = np.array([c["prices"][g] <= floor_of(c["prices"], g) * 1.007 for c in wide])
        pw = np.array([c["player"] for c in wide]); ow = np.array([c["ovr1"] for c in wide], float)
        fw = np.array([fold_of(c["sp_id"]) for c in wide])
        bw = [pooled_block(wide, dict(statz=300)), colour_block(wide, m), rare_block(wide), compat_block(wide), usage_block(wide, usage, False), curve_block(wide, (110, 115, 120, 125))]
        _, Xw, penw = design(bw)
        pr = np.full(len(yw), np.nan)
        for f in range(5):
            tr, te = np.where(fw != f)[0], np.where((fw == f) & (ow >= 115))[0]
            pr[te] = fit(Xw, penw, yw, cw, pw, tr, te, flw, ow, wide)
        lookup = {c["sp_id"]: v for c, v in zip(wide, pr)}
        row(f"학습 +1 OVR {cut}+ ({len(wide):,}장)", np.array([lookup.get(c["sp_id"], np.nan) for c in data]), p0)
    save()


def prices_get(prices, c, g):
    v = prices.get((c["grp"], c["sp_id"]), {}).get(g, 0)
    return math.log(v) if v > 1200 else np.nan


if __name__ == "__main__":
    sys.exit(main())
