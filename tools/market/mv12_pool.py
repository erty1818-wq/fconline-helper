"""MV-12: one pooled model over every field card (a card has one price, whatever groups list it) against the app's
per-group models. Paired comparison on the same unseen cards, footballer-resampled interval.
    python tools/market/mv12_pool.py --out artifacts/mv12_pool.txt [--grade 11]
Uses tools/market/mv12.py. Every number is a market association [추정].
"""
import argparse, json, math, os, sys
from collections import Counter, defaultdict
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import numpy as np
import mv12
from mv12 import Block, FIELD_STATS, design, fit_predict, fold_of, floor_of, load, sample, base_block

FAMILY = {**{p: "ST" for p in ("ST", "LS", "RS")}, **{p: "CF" for p in ("CF", "LF", "RF")}, **{p: "W" for p in ("LW", "RW")},
          **{p: "CAM" for p in ("CAM", "LAM", "RAM")}, **{p: "SM" for p in ("LM", "RM")}, **{p: "CM" for p in ("CM", "LCM", "RCM")},
          **{p: "CDM" for p in ("CDM", "LDM", "RDM")}, **{p: "FB" for p in ("LB", "RB", "LWB", "RWB")}, **{p: "CB" for p in ("CB", "LCB", "RCB", "SW")}}
GROUP_OF = {"ST": "ATT", "CF": "ATT", "W": "ATT", "SM": "ATT", "CAM": "MID", "CM": "MID", "CDM": "MID", "FB": "DEF", "CB": "DEF"}


def unique_cards(m, grade, cut):
    """One row per field card: its stats and positions merged over the groups that list it."""
    rows = {}
    for c in m["cards"]:
        if c["grp"] == "GK" or c["ovr1"] < cut or not c["traded"] or c["prices"].get(grade, 0) <= 1200: continue
        if any(n in c["name"] for n in mv12.OUTLIERS): continue
        r = rows.setdefault(c["sp_id"], dict(c, groups=set(), positions=dict(c["positions"]), stats=dict(c["stats"]), traits=set(c["traits"])))
        r["groups"].add(c["grp"]); r["positions"].update(c["positions"]); r["stats"].update(c["stats"]); r["traits"] |= c["traits"]
    data = list(rows.values())
    for c in data:
        best = max(c["positions"].items(), key=lambda kv: kv[1], default=(None, 0))[0]
        c["family"] = FAMILY.get(best) or sorted(c["groups"])[0]
        c["line"] = GROUP_OF.get(c["family"], "MID")
    return data


def pooled_block(data, opts):
    b = Block()
    ovr = np.array([c["ovr1"] for c in data], float); d = ovr - ovr.mean()
    pay = np.array([c["pay"] for c in data], float)
    fams = sorted({c["family"] for c in data})
    fam = {f: np.array([c["family"] == f for c in data], float) for f in fams}
    b.add("ovr", d, z=1e-3); b.add("ovr2", d * d, z=1e-3); b.add("pay", pay, z=1e-3)
    b.add("wf5", [c["weak_foot"] >= 5 for c in data], z=30); b.add("wf4", [c["weak_foot"] == 4 for c in data], z=30)
    for f in fams[1:]:
        b.add("fam:" + f, fam[f], z=1e-3)
    for f in fams:
        b.add("ovr×" + f, fam[f] * d, z=opts.get("famz", 3)); b.add("pay×" + f, fam[f] * (pay - pay.mean()), z=opts.get("famz", 3))
    seasons = Counter(c["season"] for c in data)
    base = seasons.most_common(1)[0][0]
    for s, n in seasons.items():
        if s != base and n >= 5: b.add("season:" + s, [c["season"] == s for c in data], z=1e-3)
    for s, n in seasons.items():
        if n >= 25:
            h = np.array([c["season"] == s for c in data], float)
            b.add("season×ovr:" + s, h * d, z=opts.get("sovr", 30))
            for f in ("ATT", "MID", "DEF"):
                b.add(f"season:{s}×{f}", h * np.array([c["line"] == f for c in data], float), z=opts.get("sline", 30))
    for s in FIELD_STATS:
        col = np.array([c["stats"].get(s, c["ovr1"]) - c["ovr1"] for c in data], float)
        b.add("stat:" + s, col, z=30)
        for f in fams:
            b.add(f"stat:{s}×{f}", fam[f] * col, z=opts.get("statz", 100))
    traits = Counter(t for c in data for t in c["traits"])
    for t, n in traits.items():
        if n < 8: continue
        h = np.array([t in c["traits"] for c in data], float)
        hinge = h * np.maximum(0, ovr - opts.get("hinge", 115))
        b.add("trait:" + t, h, raw=2); b.add("hinge:" + t, hinge, raw=5)
        for f in ("ATT", "MID", "DEF"):
            l = np.array([c["line"] == f for c in data], float)
            if (h * l).sum() >= 8:
                b.add(f"trait:{t}×{f}", h * l, raw=2); b.add(f"hinge:{t}×{f}", hinge * l, raw=5)
        for f in fams:
            if (h * fam[f]).sum() >= 12:
                b.add(f"trait:{t}×{f}", h * fam[f], raw=4); b.add(f"hinge:{t}×{f}", hinge * fam[f], raw=10)
    return b


def colour_block(data, m, z=30):
    share = m["colour_share"]
    best = np.array([max((share.get(t, 0) for t in c["colours"]), default=0) for c in data])
    b = Block()
    b.add("colour_share", best, z=z); b.add("colour_share2", best ** 2, z=z)
    b.add("colour_count", [len(c["colours"]) for c in data], z=z)
    by = defaultdict(list)
    for c in data:
        for t in c["colours"]: by[t, c["family"]].append(c["ovr1"])
    rank = np.array([min((sum(o > c["ovr1"] for o in by[t, c["family"]]) for t in c["colours"] if share.get(t, 0) > .005), default=50) for c in data])
    b.add("colour_rank1", rank == 0, z=z); b.add("colour_rank3", rank < 3, z=z); b.add("colour_rank_log", np.log1p(rank), z=z)
    return b


def ranker_block(data, m, z=30):
    b = Block()
    b.add("rankers_card", [math.log1p(m["use_card"][c["sp_id"]]) for c in data], z=z)
    b.add("ranker_any", [m["use_card"][c["sp_id"]] > 0 for c in data], z=z)
    return b


def compat_block(data, z=100):
    b = Block()
    for t, stats in mv12.COMPAT.items():
        h = np.array([t in c["traits"] for c in data], float)
        if h.sum() < 15: continue
        for s in stats:
            v = np.array([c["stats"].get(s, 0) - (0 if s == "height" else c["ovr1"]) for c in data], float)
            b.add(f"{t}×{s}", h * (v - v.mean()), z=z)
    return b


def level_block(data, z=30):
    b = Block()
    h = np.array([c["stats"].get("height", 180) for c in data], float); w = np.array([c["stats"].get("weight", 75) for c in data], float)
    b.add("height", h - h.mean(), z=z); b.add("height2", (h - h.mean()) ** 2, z=z); b.add("weight", w - w.mean(), z=z)
    for s in ["sprintspeed", "acceleration", "agility", "balance", "dribbling", "finishing", "strength", "headingaccuracy", "shortpassing", "marking"]:
        v = np.array([c["stats"].get(s, 0) for c in data], float)
        b.add(f"{s}_top", np.maximum(0, v - np.percentile(v, 75)), z=z)
    return b


def rating_block(data, z=30):
    b = Block()
    r = np.array([c["rating"] or 0 for c in data], float); n = np.array([c["rating_count"] or 0 for c in data], float)
    b.add("rating", np.where(n > 0, r - r[n > 0].mean(), 0), z=z); b.add("log_ratings", np.log1p(n), z=z)
    return b


def cv(X, pen, y, cens, players, folds, floors):
    pred = np.full(len(y), np.nan)
    for f in range(5):
        tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
        pred[te], _, _ = fit_predict(X, pen, y, cens, players, tr, te, floors)
    return pred


def stats_line(pred, y, mask):
    e = np.abs(np.exp(pred[mask] - y[mask]) - 1)
    return float(np.median(e)), float(np.mean(np.abs(pred[mask] - y[mask])))


def paired(p1, p0, y, players, mask, rng, draws=400):
    """Mean |log error| difference (new − old) with an interval from resampling footballers."""
    d = np.abs(p1[mask] - y[mask]) - np.abs(p0[mask] - y[mask])
    ids = players[mask]
    u, inv = np.unique(ids, return_inverse=True)
    sums, counts = np.bincount(inv, d), np.bincount(inv)
    boots = [sums[pk].sum() / counts[pk].sum() for pk in (rng.integers(0, len(u), len(u)) for _ in range(draws))]
    lo, hi = np.percentile(boots, [2.5, 97.5])
    return float(d.mean()), float(lo), float(hi)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=os.path.expandvars(r"%LOCALAPPDATA%\FcHelper\fchelper.db"))
    ap.add_argument("--out", required=True)
    ap.add_argument("--grade", type=int, default=11)
    ap.add_argument("--cut", type=int, default=115)
    a = ap.parse_args()
    m = load(a.db)
    g = a.grade
    rng = np.random.default_rng(3)
    data = unique_cards(m, g, a.cut)
    y = np.log([c["prices"][g] for c in data])
    cens = np.array([c["prices"][g] <= floor_of(c["prices"], g) * 1.007 for c in data])
    floors = np.array([floor_of(c["prices"], g) for c in data])
    players = np.array([c["player"] for c in data])
    folds = np.array([fold_of(c["sp_id"]) for c in data])
    hold = np.array([(c["player"] * 2654435761) % 2**32 % 2 == 0 for c in data])  # half for choosing, half for confirming
    top = np.array([c["ovr1"] >= 119 for c in data]); free = ~cens
    out = [f"필드 카드 {len(data):,}장, 선수 {len(set(players)):,}명, 하한 {cens.mean():.0%} · +{g} · +1 OVR {a.cut}+"]
    # Baseline: the app's per-group model, each card scored in its own family's group.
    p0 = np.full(len(y), np.nan)
    index = {c["sp_id"]: i for i, c in enumerate(data)}
    for group in ["ST", "CF", "W", "SM", "CAM", "CM", "CDM", "FB", "CB"]:
        gd = sample(m, group, a.cut, g)
        if len(gd) < 60: continue
        gy = np.log([c["prices"][g] for c in gd]); gc = np.array([c["prices"][g] <= floor_of(c["prices"], g) * 1.007 for c in gd])
        gf = np.array([floor_of(c["prices"], g) for c in gd]); gp = np.array([c["player"] for c in gd]); gfo = np.array([fold_of(c["sp_id"]) for c in gd])
        _, X, pen = design([base_block(gd, group)])
        pg = cv(X, pen, gy, gc, gp, gfo, gf)
        for c, v in zip(gd, pg):
            i = index.get(c["sp_id"])
            if i is not None and data[i]["family"] == group: p0[i] = v
    ok = np.isfinite(p0)
    out.append(f"포지션별 모델로 평가된 카드 {ok.sum():,}장 (같은 카드로만 비교)")
    def row(name, p, ref=None):
        a1 = stats_line(p, y, ok); a2 = stats_line(p, y, ok & top); a3 = stats_line(p, y, ok & top & free)
        s = f"  {name:<32} 전체 {a1[0]:.1%} · 상위 {a2[0]:.1%} · 상위(하한 제외) {a3[0]:.1%}"
        if ref is not None:
            for lab, mask in (("선택", ok & ~hold), ("확인", ok & hold)):
                d, lo, hi = paired(p, ref, y, players, mask, rng)
                s += f" | {lab} {d:+.3f} ({lo:+.3f}~{hi:+.3f})"
        out.append(s)
    row("포지션별 (현재 앱)", p0)
    base = pooled_block(data, {})
    _, X, pen = design([base])
    p1 = cv(X, pen, y, cens, players, folds, floors)
    row("통합 모델", p1, p0)
    cands = {"팀컬러 인기·깊이": colour_block(data, m), "랭커 사용": ranker_block(data, m), "특성×능력치 궁합": compat_block(data), "능력치 수준·키": level_block(data), "평점·평점 수": rating_block(data)}
    preds = {}
    for name, b in cands.items():
        _, X, pen = design([base, b])
        preds[name] = cv(X, pen, y, cens, players, folds, floors)
        row("통합 + " + name, preds[name], p1)
    for opt, val in [("hinge", 118), ("hinge", 112), ("statz", 30), ("statz", 300), ("sline", 3), ("sovr", 3), ("famz", 30)]:
        _, X, pen = design([pooled_block(data, {opt: val})])
        row(f"통합 {opt}={val}", cv(X, pen, y, cens, players, folds, floors), p1)
    out.append("\n가족별 (전체 중앙오차): 포지션별 → 통합")
    for f in sorted({c["family"] for c in data}):
        mk = ok & np.array([c["family"] == f for c in data])
        if mk.sum() >= 20: out.append(f"  {f}: {mk.sum()}장 {stats_line(p0, y, mk)[0]:.1%} → {stats_line(p1, y, mk)[0]:.1%}")
    Path(a.out).parent.mkdir(parents=True, exist_ok=True)
    Path(a.out).write_text("\n".join(out), encoding="utf-8")


if __name__ == "__main__":
    sys.exit(main())
