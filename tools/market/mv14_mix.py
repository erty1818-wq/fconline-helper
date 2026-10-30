"""MV-14: blending different models, and telling in advance which cards the estimate can be trusted on. Pooled model with
the per-group models it replaced (those were better on top cards without traits) and with a boosted tree. Then a per-card error
band: predicted from what is known before seeing the price, checked on unseen cards. Same half split as the rest of MV-14.
    python tools/market/mv14_mix.py --out artifacts/mv14_mix.txt
"""
import argparse, math, os, sys
from collections import Counter, defaultdict
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import numpy as np
from mv12 import GROUPS, base_block, design, fit_predict, fold_of, floor_of, load, sample
from mv12_pool import unique_cards, pooled_block, colour_block, compat_block, stats_line, paired
from mv12_search import rare_block
from mv14 import match_usage, usage_block
from mv14_fit import curve_block, fit


def group_predictions(m, g, cut):
    """The per-group models, each card averaged over the groups that list it."""
    preds = defaultdict(list)
    for group in GROUPS:
        if group == "GK": continue
        data = sample(m, group, cut, g)
        if len(data) < 80: continue
        y = np.log([c["prices"][g] for c in data])
        cens = np.array([c["prices"][g] <= floor_of(c["prices"], g) * 1.007 for c in data])
        floors = np.array([floor_of(c["prices"], g) for c in data])
        players = np.array([c["player"] for c in data]); ovr = np.array([c["ovr1"] for c in data], float)
        folds = np.array([fold_of(c["sp_id"]) for c in data])
        _, X, pen = design([base_block(data, group)])
        for f in range(5):
            tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
            p, _, _ = fit_predict(X, pen, y, cens, players, tr, te, floors, ovr=ovr, near=3.0)
            for i, v in zip(te, p): preds[data[i]["sp_id"]].append(v)
    return {k: float(np.mean(v)) for k, v in preds.items()}


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
    lf = np.log(np.maximum(floors, 1))
    cens = np.array([c["prices"][g] <= floor_of(c["prices"], g) * 1.007 for c in data])
    players = np.array([c["player"] for c in data]); ovr = np.array([c["ovr1"] for c in data], float)
    folds = np.array([fold_of(c["sp_id"]) for c in data])
    hold = np.array([(c["player"] * 2654435761) % 2**32 % 2 == 0 for c in data])
    top = ovr >= 119; free = ~cens; everyone = np.ones(len(y), bool)
    usage, _ = match_usage(a.db)
    base = [pooled_block(data, dict(statz=300)), colour_block(data, m), rare_block(data), compat_block(data), usage_block(data, usage, False), curve_block(data)]
    _, X, pen = design(base)
    def row(name, p, ref):
        s = f"  {name:<30} 전체 {stats_line(p, y, everyone)[0]:.1%} · 상위 {stats_line(p, y, top)[0]:.1%} · 상위(하한 제외) {stats_line(p, y, top & free)[0]:.1%}"
        if ref is not None:
            for lab, mk in (("선택", ~hold), ("확인", hold)):
                d, lo, hi = paired(p, ref, y, players, mk, rng)
                s += f" | {lab} {d:+.3f} ({lo:+.3f}~{hi:+.3f})"
        out.append(s); save()
    p0 = np.full(len(y), np.nan)
    for f in range(5):
        tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
        p0[te] = fit(X, pen, y, cens, players, tr, te, floors, ovr, data, local=5.0)
    out.append("## 모델 섞기")
    row("통합 + 사용량 + 곡선 + 시즌 잔차", p0, None)
    gp = group_predictions(m, g, 115)
    pg = np.array([gp.get(c["sp_id"], np.nan) for c in data])
    pg = np.where(np.isnan(pg), p0, pg)
    row("포지션별 모델 단독", pg, p0)
    for w in (0.2, 0.35, 0.5):
        row(f"포지션별 {w:.0%} 섞기", np.maximum((1 - w) * p0 + w * pg, lf), p0)
    try:
        import lightgbm as lgb
    except ImportError:
        lgb = None
    pb = None
    if lgb is not None:
        pb = np.full(len(y), np.nan)
        for f in range(5):
            tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
            # The footballer's other cards as one feature, inside the fold: leave-one-out for training rows.
            spec = y - 0.45 * ovr
            sums, counts = Counter(), Counter()
            for i in tr: sums[players[i]] += spec[i]; counts[players[i]] += 1
            s_tr = np.array([(sums[players[i]] - spec[i]) / (counts[players[i]] - 1) if counts[players[i]] > 1 else np.nan for i in tr])
            s_te = np.array([sums[players[i]] / counts[players[i]] if counts[players[i]] else np.nan for i in te])
            params = dict(objective="huber", alpha=0.7, learning_rate=0.03, num_leaves=15, min_data_in_leaf=10, bagging_fraction=0.8, bagging_freq=1,
                          feature_fraction=0.3, lambda_l2=5, verbosity=-1, seed=7, num_threads=4)
            model = lgb.train(params, lgb.Dataset(np.column_stack([X[tr], s_tr]), y[tr]), num_boost_round=600)
            pb[te] = np.maximum(model.predict(np.column_stack([X[te], s_te])), lf[te])
        row("LightGBM 단독", pb, p0)
        for w in (0.2, 0.35, 0.5):
            row(f"LightGBM {w:.0%} 섞기", np.maximum((1 - w) * p0 + w * pb, lf), p0)
        for w, v in ((0.2, 0.2), (0.3, 0.2), (0.25, 0.25)):
            row(f"포지션별 {w:.0%} + LightGBM {v:.0%}", np.maximum((1 - w - v) * p0 + w * pg + v * pb, lf), p0)
    # Which cards can be trusted: predicted from what is known without the card's own price.
    out.append("\n## 카드별 오차 범위: 시세를 보기 전에 맞히기 어려운 카드를 가릴 수 있나")
    best = np.maximum(0.8 * p0 + 0.2 * pg, lf)
    r = y - best
    siblings = Counter(c["player"] for c in data)
    season_n = Counter(c["season"] for c in data)
    seen = np.array([usage[c["sp_id"]]["n"] if c["sp_id"] in usage else 0 for c in data], float)
    agree = np.abs(p0 - pg)
    feats = np.column_stack([np.log1p([siblings[c["player"]] - 1 for c in data]), [siblings[c["player"]] == 1 for c in data], np.log1p(seen), ovr - ovr.mean(), best - best.mean(), np.maximum(0, 12 - best),
                             best - lf, [len(c["traits"]) for c in data], np.log1p([season_n[c["season"]] for c in data]), agree])
    pred_abs = np.full(len(y), np.nan)
    season_mad = np.zeros(len(y))
    for f in range(5):
        tr, te = np.where((folds != f) & free)[0], np.where(folds == f)[0]
        mads = defaultdict(list)
        for i in tr: mads[data[i]["season"]].append(abs(r[i]))
        pool = float(np.median(np.abs(r[tr])))
        for i in range(len(y)):
            v = mads.get(data[i]["season"], [])
            season_mad[i] = (sum(v) + 10 * pool) / (len(v) + 10)
        F = np.column_stack([np.ones(len(y)), feats, season_mad])
        ta = np.log(np.abs(r[tr]) + .03)
        A = F[tr]
        beta = np.linalg.solve(A.T @ A + 1e-3 * np.eye(A.shape[1]), A.T @ ta)
        pred_abs[te] = F[te] @ beta
    cuts = np.quantile(pred_abs[free], [.25, .5, .75])
    tier = np.digitize(pred_abs, cuts)
    names = ["가장 믿을 만한 25%", "그다음 25%", "그다음 25%", "가장 못 믿을 25%"]
    for t in range(4):
        mk = tier == t
        def line(sub):
            e = np.abs(np.exp(best[sub] - y[sub]) - 1)
            lo, hi = np.quantile(r[sub], [.1, .9])
            return f"{sub.sum():,}장 오차 {np.median(e):.1%} · 80% 범위 {math.exp(lo) - 1:+.0%}~{math.exp(hi) - 1:+.0%}"
        out.append(f"  {names[t]}: 선택 {line(mk & free & ~hold)} | 확인 {line(mk & free & hold)}")
    save()
    out.append("\n## 그 카드 시세 이력을 쓰면 (모델이 아니라 시세 자체의 추적, 가치 판단엔 못 씀)")
    for s in sorted(m["prev"])[-2:]:
        old = np.array([m["prev"][s].get((c["grp"], c["sp_id"]), {}).get(g, 0) for c in data], float)
        ok = (old > 1200) & free
        e = np.abs(old[ok] / np.exp(y[ok]) - 1)
        out.append(f"  스냅샷 {s} 시세를 그대로 쓰면 오차 {np.median(e):.1%} ({ok.sum():,}장)")
    save()


if __name__ == "__main__":
    sys.exit(main())
