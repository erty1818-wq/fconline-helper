"""MV-10: reproducible +8/+10/+11 diagnostics, within-footballer Tobit and card CV.

Python 3.10+, numpy. No API key, network or writes to the source database.
    python tools/market/mv10.py --out artifacts/mv10/analysis.json --prepare-db artifacts/mv10/market.db
The optional copy contains market tables only; use it for CLI commands that open a writable MarketStore.
All OVR cuts are at +1. Prices in reports are in eok (100,000,000 BP).
"""
import argparse
from collections import defaultdict
import json
import math
import os
from pathlib import Path
import sqlite3

# Many small regressions are faster and reproducible with one BLAS worker.
os.environ.setdefault("OPENBLAS_NUM_THREADS", "1")
os.environ.setdefault("MKL_NUM_THREADS", "1")
import numpy as np

GROUPS = ["ST", "CF", "W", "CAM", "CM", "CDM", "FB", "CB", "GK"]
FLOORS = {5: (3, 1.5), 8: (5, 9.5), 10: (8, 2.1), 11: (10, 1.55)}
SHAPES = ["constant", "linear", "hinge115", "hinge118", "hinge120", "hinge122"]
FOCUS = {"ST": ["크로스 포쳐", "라인 브레이커"], "CF": ["크로스 포쳐", "라인 브레이커"],
         "W": ["라인 브레이커", "크로스 포쳐", "트릭스터", "스피드스터"]}


def at_floor(prices, grade):
    lower, ratio = FLOORS[grade]
    return prices.get(lower, 0) > 0 and 0 < prices.get(grade, 0) <= prices[lower] * ratio * 1.007


def floor_at(card, grade):
    lower, ratio = FLOORS[grade]
    return round(card["prices"].get(lower, 0) * ratio)


def fold_of(spid):
    """Same unsigned 64-bit Murmur finalizer as PriceModel.Fold: cards, not footballers."""
    mask = (1 << 64) - 1
    x = int(spid) & mask
    x ^= x >> 33
    x = (x * 0xff51afd7ed558ccd) & mask
    x ^= x >> 33
    x = (x * 0xc4ceb9fe1a85ec53) & mask
    x ^= x >> 33
    return x % 5


def read_market(path, prepare=None):
    conn = sqlite3.connect(Path(path).resolve().as_uri() + "?mode=ro", uri=True)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA query_only=ON")
    conn.execute("BEGIN")
    snapshots = [dict(r) for r in conn.execute("SELECT * FROM market_snapshot WHERE finished_at IS NOT NULL ORDER BY id")]
    latest = snapshots[-1]["id"]
    tags = defaultdict(set)
    for r in conn.execute("SELECT grp,sp_id,tag FROM market_tag WHERE snapshot=? ORDER BY grp,sp_id,tag", (latest,)):
        tags[r["grp"], r["sp_id"]].add(r["tag"])
    cards = []
    for r in conn.execute("SELECT * FROM market_card WHERE snapshot=? ORDER BY grp,sp_id", (latest,)):
        c = dict(r)
        c["prices"] = {int(k): v for k, v in json.loads(c["prices"]).items()}
        c["stats"] = json.loads(c["stats"])
        c["player"] = c["sp_id"] % 1_000_000
        c["traits"] = {t[6:] for t in tags[c["grp"], c["sp_id"]] if t.startswith("trait:")}
        c["traded_proxy"] = len(set(c["prices"].values())) > 1
        cards.append(c)
    history = [dict(r) for r in conn.execute("SELECT day,grp,sp_id,prices FROM price_history ORDER BY day,grp,sp_id")]
    for r in history:
        r["prices"] = {int(k): v for k, v in json.loads(r["prices"]).items()}
    snapshot_prices = defaultdict(dict)
    for r in conn.execute("SELECT snapshot,grp,sp_id,prices FROM market_card ORDER BY snapshot,grp,sp_id"):
        snapshot_prices[r["snapshot"]][r["grp"], r["sp_id"]] = {int(k): v for k, v in json.loads(r["prices"]).items()}
    if prepare:
        target = Path(prepare)
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists():
            raise FileExistsError(f"Refusing to replace {target}")
        dest = sqlite3.connect(target)
        names = {r[0] for r in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        for name in ["market_snapshot", "market_card", "market_tag", "market_done", "market_season", "price_history",
                     "market_patch_baseline", "ranker_chart", "team_color", "team_color_member", "kv_market"]:
            if name not in names:
                continue
            sql = conn.execute("SELECT sql FROM sqlite_master WHERE name=?", (name,)).fetchone()[0]
            dest.execute(sql)
            rows = conn.execute(f'SELECT * FROM "{name}"').fetchall()
            if rows:
                dest.executemany(f'INSERT INTO "{name}" VALUES ({",".join("?" for _ in rows[0])})', rows)
        dest.commit()
        dest.close()
    conn.close()
    return cards, history, snapshots, snapshot_prices


def quantiles(values):
    return dict(zip(["p05", "p25", "p50", "p75", "p95"], map(float, np.quantile(values, [.05, .25, .5, .75, .95])))) if len(values) else None


def movement(series):
    """At least two observations per card; a varying quote is not a completed trade."""
    changes, observed, steps = [], 0, []
    for prices in series.values():
        if len(prices) < 2:
            continue
        observed += 1
        changes.append(len(set(prices)) > 1)
        steps.extend(abs(math.log(b / a)) for a, b in zip(prices, prices[1:]) if a > 0 and b > 0)
    return {"cards_two_observations": observed, "share_changed": float(np.mean(changes)) if changes else None,
            "median_abs_log_step": float(np.median(steps)) if steps else None}


def liquidity(cards, history, snapshots, snapshot_prices):
    result = []
    for group in GROUPS:
        data = [c for c in cards if c["grp"] == group and c["ovr1"] >= 115]
        row = {"group": group, "cards": len(data), "grades": {}}
        for grade in [8, 10, 11]:
            usable = [c for c in data if c["prices"].get(grade, 0) > 1200]
            lower, _ = FLOORS[grade]
            ratios = [c["prices"][grade] / c["prices"][lower] for c in usable if c["prices"].get(lower, 0) > 0]
            keys = {(c["grp"], c["sp_id"]) for c in usable}
            daily, snap, recent, ratios_recent = defaultdict(list), defaultdict(list), defaultdict(list), defaultdict(list)
            days = set()
            for r in history:
                if (r["grp"], r["sp_id"]) in keys and r["prices"].get(grade, 0) > 1200:
                    daily[r["sp_id"]].append(r["prices"][grade])
                    days.add(r["day"])
            for s in snapshots:
                for key in sorted(keys):
                    p = snapshot_prices[s["id"]].get(key, {}).get(grade, 0)
                    if p > 1200:
                        snap[key].append(p)
                        if s["id"] in {v["id"] for v in snapshots[-2:]}:
                            recent[key].append(p)
                            lower_price = snapshot_prices[s["id"]].get(key, {}).get(lower, 0)
                            if lower_price > 0:
                                ratios_recent[key].append(p/lower_price)
            row["grades"][str(grade)] = {"cards": len(usable),
                "nonflat_price_proxy_cards": sum(c["traded_proxy"] for c in usable),
                "floor_share_nonflat": float(np.mean([at_floor(c["prices"], grade) for c in usable if c["traded_proxy"]])),
                "ratio_to_lower_nonflat": quantiles([c["prices"][grade]/c["prices"][lower] for c in usable if c["traded_proxy"] and c["prices"].get(lower,0)>0]),
                "floor_share": float(np.mean([at_floor(c["prices"], grade) for c in usable])) if usable else None,
                "ratio_to_lower": quantiles(ratios), "daily_days": sorted(days), "daily": movement(daily), "snapshots": movement(snap),
                "latest_two_snapshots": movement(recent), "latest_two_ratios": movement(ratios_recent)}
        result.append(row)
    return result


def sample(cards, group, cut, grade):
    return [c for c in cards if c["grp"] == group and c["ovr1"] >= cut and c["traded_proxy"]
            and c["prices"].get(grade, 0) > 1200 and not any(n in c["name"] for n in ["호날두", "호나우두", "굴리트"])]


def design(data, shape="hinge115", spec=None, labels=None):
    if spec is None:
        players = {c["player"] for c in data}
        traits = sorted({t for c in data for t in c["traits"]})
        traits = [t for t in traits if len({c["player"] for c in data if t in c["traits"]}) >= 8
                  and len({c["player"] for c in data if t not in c["traits"]}) >= 8]
        seasons = sorted({c["season"] for c in data})
        spec = dict(mean=float(np.mean([c["ovr1"] for c in data])), traits=traits, seasons=seasons,
                    players=len(players), shape=shape)
    d = np.array([c["ovr1"] - spec["mean"] for c in data])
    columns = [np.ones(len(data)), d, d*d, np.array([c["pay"] for c in data]),
               np.array([c["weak_foot"] >= 5 for c in data]), np.array([c["weak_foot"] == 4 for c in data])]
    # Exact season fixed effects (even small seasons); tiny numerical stabilizer only.
    columns += [np.array([c["season"] == s for c in data]) for s in spec["seasons"][1:]]
    controls = len(columns)
    tags = labels if labels is not None else [c["traits"] for c in data]
    trait_cols = [np.array([t in ts for ts in tags], float) for t in spec["traits"]]
    columns += trait_cols
    if spec["shape"] != "constant":
        ovr = np.array([c["ovr1"] for c in data])
        z = ovr - 118 if spec["shape"] == "linear" else np.maximum(0, ovr - int(spec["shape"][5:]))
        columns += [t*z for t in trait_cols]
    penalty = np.array([0] + [1e-3]*(controls-1) + [2]*len(trait_cols)
                       + ([] if spec["shape"] == "constant" else [5]*len(trait_cols)))
    return np.column_stack(columns), penalty, controls, spec


def mills(a):
    # Stable Numerical Recipes phi/Phi, same approximation as the application.
    u = -np.asarray(a, float) / math.sqrt(2)
    t = 1 / (1 + 0.5 * np.abs(u))
    poly = np.zeros_like(t)
    for v in reversed([-1.26551223, 1.00002368, .37409196, .09678418, -.18628806, .27886807,
                       -1.13520398, 1.48851587, -.82215223, .17087277]):
        poly = poly*t + v
    return np.where(u >= 0, 2 / (math.sqrt(2*math.pi)*t*np.exp(poly)),
                    np.exp(-np.asarray(a)**2/2) / math.sqrt(2*math.pi) / np.maximum(1 - .5*t*np.exp(-u*u + poly), 1e-300))


def fit(data, grade, shape="hinge115", labels=None):
    x, penalty, control_count, spec = design(data, shape, labels=labels)
    n, k = x.shape
    mu, sd = x.mean(axis=0), x.std(axis=0)
    mu[0], sd[0] = 0, 1
    sd[sd < 1e-12] = 1
    z = (x-mu)/sd
    # Exact footballer fixed effects, including single-card footballers: their rows contain no within-player
    # identifying information. Leave one reference effect out for the intercept, then centre all effects below.
    # This deliberately differs from the app's .05 ridge on repeated players only.
    ids = np.array([c["player"] for c in data])
    players, inv, counts = np.unique(ids, return_inverse=True, return_counts=True)
    several = counts >= 2
    absorbed = np.ones(len(players), bool)
    absorbed[int(np.argmax(counts))] = False
    effect_ids = np.flatnonzero(absorbed)
    bsum = np.zeros((len(players), k))
    np.add.at(bsum, inv, z)
    b = bsum[effect_ids].T
    d = counts[effect_ids]
    a = z.T@z + np.diag(penalty/(sd*sd)) - (b/d)@b.T
    ainv = np.linalg.inv(a)
    w = z.copy()
    w[absorbed[inv]] -= bsum[inv[absorbed[inv]]] / counts[inv[absorbed[inv]], None]
    h = ainv@w.T
    y = np.log([c["prices"][grade] for c in data])
    cens = np.array([at_floor(c["prices"], grade) for c in data])
    def solve(target):
        beta = h@target
        sums = np.bincount(inv, weights=target-z@beta, minlength=len(players))
        effects = np.where(absorbed, sums/counts, 0)
        shift = effects@counts/n
        beta[0] += shift
        effects -= shift
        return beta, effects[inv]
    yi = y.copy()
    beta, effect = solve(yi)
    sigma = max(float(np.std((y-z@beta-effect)[~cens])), .05) if (~cens).any() else .05
    for _ in range(60 if cens.any() else 0):
        pred = z@beta+effect
        t = (y[cens]-pred[cens])/sigma
        m = mills(t)
        yi[cens] = pred[cens]-sigma*m
        variance = sigma*sigma*np.maximum(1-t*m-m*m, 1e-6)
        next_beta, next_effect = solve(yi)
        e = yi-z@next_beta-next_effect
        sigma = max(math.sqrt((e@e+variance.sum())/n), .05)
        done = np.max(np.abs(next_beta-beta)) < 1e-6
        beta, effect = next_beta, next_effect
        if done:
            break
    if not cens.any():
        sigma = max(float(np.sqrt(np.mean((yi-z@beta-effect)**2))), .05)
    residual = yi-z@beta
    sums = np.bincount(inv, weights=residual, minlength=len(players))
    tau2 = float(np.mean((sums[several]/counts[several])**2-sigma*sigma/counts[several])) if several.sum() >= 10 else 0
    shrink = float(np.clip(sigma*sigma/tau2, .1, 10)) if tau2 > 1e-4 else 10
    scores = np.zeros((len(players), k))
    np.add.at(scores, inv, (yi-z@beta-effect)[:, None]*w)
    cr1 = len(players)/max(len(players)-1, 1)*(n-1)/max(n-k-len(effect_ids), 1)
    influence = (scores@ainv.T)/sd * math.sqrt(cr1)
    raw = beta/sd
    raw[0] = beta[0]-mu@raw
    return dict(beta=raw, influence=influence, spec=spec, controls=control_count, sigma=sigma,
                effects={int(p): float(s/(count+shrink)) for p,s,count in zip(players,sums,counts)},
                own={c["sp_id"]: float((sums[inv[i]]-residual[i])/(counts[inv[i]]-1+shrink)) for i,c in enumerate(data)})


def predict(model, data, grade):
    x, _, _, _ = design(data, spec=model["spec"])
    effect = np.array([model["own"].get(c["sp_id"], model["effects"].get(c["player"], 0)) for c in data])
    return np.maximum(x@model["beta"]+effect, np.log(np.maximum(1, [floor_at(c, grade) for c in data])))


def effects(model, data, at_ovr=122, critical=3):
    out = []
    spec = model["spec"]
    for j,t in enumerate(spec["traits"]):
        weights = np.zeros(len(model["beta"]))
        weights[model["controls"]+j] = 1
        if spec["shape"] != "constant":
            z = at_ovr-118 if spec["shape"] == "linear" else max(0, at_ovr-int(spec["shape"][5:]))
            weights[model["controls"]+len(spec["traits"])+j] = z
        b = float(weights@model["beta"])
        se = float(np.linalg.norm(model["influence"]@weights))
        comparing = sum(any(t in c["traits"] for c in cards) and any(t not in c["traits"] for c in cards)
                        for cards in group_by_player(data).values())
        out.append(dict(trait=t, log_premium=b, se=se, t=b/se if se > 1e-12 else None, comparisons=comparing,
                        percent=100*math.expm1(np.clip(b, -20, 20)),
                        low=100*math.expm1(np.clip(b-critical*se, -20, 20)), high=100*math.expm1(np.clip(b+critical*se, -20, 20)),
                        clear=comparing >= 8 and abs(b) > critical*se))
    return out


def group_by_player(data):
    groups = defaultdict(list)
    for c in data:
        groups[c["player"]].append(c)
    return groups


def cv(data, grade, shape, eval_cut=None):
    residual = np.full(len(data), np.nan)
    folds = np.array([fold_of(c["sp_id"]) for c in data])
    for fold in range(5):
        train = [c for i,c in enumerate(data) if folds[i] != fold]
        test = [c for i,c in enumerate(data) if folds[i] == fold]
        if len(train) < 50 or not test:
            continue
        model = fit(train, grade, shape)
        residual[folds == fold] = np.log([c["prices"][grade] for c in test])-predict(model, test, grade)
    selected = np.isfinite(residual)
    if eval_cut is not None:
        selected &= np.array([c["ovr1"] >= eval_cut for c in data])
    free_top = selected & np.array([c["ovr1"] >= 120 and not at_floor(c["prices"], grade) for c in data])
    has = np.array([bool(c["traits"]) for c in data])
    result = {"n": int(selected.sum()), "median_error": float(np.median(abs(np.expm1(-residual[selected])))) if selected.any() else None,
              "top_nonfloor": {}}
    for label, mask in [("with_trait", has), ("without_trait", ~has)]:
        m = mask & free_top
        result["top_nonfloor"][label] = {"n": int(m.sum()), "median_log_actual_expected": float(np.median(residual[m])) if m.any() else None}
    return result


def placebo(data, grade, draws, seed):
    rng = np.random.default_rng(seed)
    seasons = defaultdict(list)
    for i,c in enumerate(data):
        seasons[c["season"]].append(i)
    values = defaultdict(list)
    labels = [c["traits"] for c in data]
    # Shuffle complete trait bundles among cards of the SAME season; keep season prevalence and co-occurrence.
    for _ in range(draws):
        shuffled = labels.copy()
        for idx in seasons.values():
            for dst,src in zip(idx, rng.permutation(idx)):
                shuffled[dst] = labels[src]
        shuffled_data = [dict(c, traits=shuffled[i]) for i,c in enumerate(data)]
        model = fit(shuffled_data, grade)
        for e in effects(model, shuffled_data):
            if e["t"] is not None:
                values[e["trait"]].append(abs(e["t"]))
    def summary(v):
        return {"n": len(v), "abs_t_p95": float(np.quantile(v, .95)),
                "false_positive_1_96": float(np.mean(np.array(v) > 1.96)), "false_positive_3": float(np.mean(np.array(v) > 3)),
                "false_positive_3_5": float(np.mean(np.array(v) > 3.5))}
    return {"draws": draws, "seed": seed, "pooled": summary([v for vs in values.values() for v in vs]) if values else None,
            "by_trait": {t: summary(v) for t,v in values.items()}}


def write(path, result):
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_text(json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False)+"\n", encoding="utf-8")


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--db", default=str(Path(os.environ.get("LOCALAPPDATA", "."))/"FcHelper/fchelper.db"))
    ap.add_argument("--out", required=True)
    ap.add_argument("--prepare-db")
    ap.add_argument("--groups", nargs="+", default=GROUPS)
    ap.add_argument("--grades", nargs="+", type=int, default=[8,10,11])
    ap.add_argument("--cuts", nargs="+", type=int, default=[110,115,120])
    ap.add_argument("--phase", choices=["liquidity","traits","shapes","margin","all"], default="all")
    ap.add_argument("--draws", type=int, default=60)
    args = ap.parse_args()
    if args.draws < 40:
        ap.error("At least 40 placebo draws are required")
    cards, history, snaps, prices = read_market(args.db, args.prepare_db)
    result = {"snapshot": snaps[-1], "specification": "log price ~ exact player FE (including singletons; reference-centred, no ridge) + exact season FE (numerical stabilizer .001) + OVR + OVR^2 + salary + weak foot + traits(ridge2) + trait shape(ridge5); censored Tobit EM60; CR1 by footballer; EB player prediction; five card folds",
              "reference_ovr1": 122, "trait_shape": "hinge115", "source_read_only": True, "liquidity": liquidity(cards,history,snaps,prices), "fits": [], "margins": []}
    write(args.out, result)
    if args.phase == "liquidity":
        return
    for group in args.groups:
        for grade in args.grades:
            if args.phase in ["traits","shapes","all"]:
                for cut in args.cuts:
                    data = sample(cards,group,cut,grade)
                    if len(data) < 60:
                        continue
                    row = dict(group=group,grade=grade,cut_ovr1=cut,cards=len(data),players=len(group_by_player(data)),
                               floor_share=float(np.mean([at_floor(c["prices"],grade) for c in data])))
                    model = fit(data,grade)
                    row["traits"] = effects(model,data)
                    if args.phase in ["traits","all"]:
                        row["placebo"] = placebo(data,grade,args.draws,20261026+grade*100+cut*10+GROUPS.index(group))
                    if args.phase in ["shapes","all"]:
                        row["shapes"] = {shape:cv(data,grade,shape) for shape in SHAPES}
                    result["fits"].append(row)
                    write(args.out,result)
                    print(f"{group} +{grade} OVR1>={cut}: {len(data)} cards",flush=True)
            if args.phase in ["margin","all"]:
                # Search +11 OVR140 (=OVR1 119); keep a common evaluation population for every margin.
                target = 119
                for margin in [0,3,4,5,6,8,9]:
                    data = sample(cards,group,target-margin,grade)
                    if len(data) < 60:
                        continue
                    result["margins"].append(dict(group=group,grade=grade,margin=margin,train_cut_ovr1=target-margin,
                                                  evaluation_cut_ovr1=target,**cv(data,grade,"hinge115",eval_cut=target)))
                write(args.out,result)
                print(f"{group} +{grade} margins done",flush=True)


if __name__ == "__main__":
    main()
