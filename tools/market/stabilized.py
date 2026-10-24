"""Post-patch market analysis on a `fch market-export` file: has the market settled, and where did it settle?

    python -I tools/market/stabilized.py market-export-20261024.jsonl.gz [--grade 8] [--out report.json]
        [--old-audit docs/market-value/AUDIT-0.10.3.json --new-audit AUDIT-new.json]

Needs numpy and pandas (pip install numpy pandas). Everything is association in the market [추정], not a causal effect.

What it computes (attack / mid / defence / GK families, one grade):
  1. Market diagnostics per history day: typical daily move (median |Δln price| per day elapsed) against the
     pre-patch days, and a market index (median Δln price since the pre-patch day) on cards that existed before.
  2. Event study: for every history day, ln(price_day) − ln(price_pre-patch) of the same card regressed on trait dummies
     with family × season × OVR-band fixed effects (the common trend of similar cards), footballer-clustered errors and
     t(G−1) intervals. Days before the pre-patch day are the pre-trend check.
  3. Settling tests per trait: change over the last 7 days and from the peak day to now, with the same design.
  4. Ranker usage versus price: does a change in rankers fielding a card precede its price move (lags 1-5 days)?
  5. Grade ratios (+10/+8, +5/+8) over time, for watched seasons (default 24EP, CU: the EVO class of 2026-10-29
     takes their +8 and higher cards) apart from the rest.
  6. Season-wide moves against same-family, same-OVR-band cards of other seasons (the event study holds these fixed).
  7. Optional: compare two valuation-audit JSONs (current price levels) factor by factor.
"""
import argparse, gzip, json, math
from collections import defaultdict
from datetime import date

import numpy as np
import pandas as pd

FLOOR = 1200
PRICE_OUTLIERS = ("호날두", "호나우두", "굴리트")  # name-priced cards, as MarketGroups.PriceOutliers
FAMILY = {"ST": "attack", "CF": "attack", "W": "attack", "SM": "attack", "CAM": "attack",
          "CM": "mid", "CDM": "mid", "CB": "defence", "FB": "defence", "GK": "gk"}
FAMILY_KO = {"attack": "공격", "mid": "중앙 미드", "defence": "수비", "gk": "골키퍼"}
PATCH_TRAITS = {"스피드스터", "아크로바틱 피니셔", "2개의 심장", "크로스 포쳐"}  # 2026-09-30 rebalance
MIN_PLAYERS = 10       # below this a trait gets no direction (same rule as the price model)
MIN_CELL = 3           # fixed-effect cells with fewer cards are dropped


# ── loading ────────────────────────────────────────────────────────────────

def load(path):
    meta, cards, series, colours, rankers = None, [], [], [], []
    with gzip.open(path, "rt", encoding="utf-8") as f:
        for line in f:
            o = json.loads(line)
            t = o["t"]
            if t == "meta": meta = o
            elif t == "card": cards.append(o)
            elif t == "series": series.append(o)
            elif t == "teamColor": colours.append(o)
            elif t == "ranker": rankers.append(o)
    if meta is None:
        raise SystemExit("not a market-export file (no meta line)")
    return meta, cards, series, colours, rankers


def family_of(groups):
    fams = [FAMILY.get(g) for g in groups if g in FAMILY]
    if not fams: return None
    order = ["attack", "mid", "defence", "gk"]
    return max(set(fams), key=lambda x: (fams.count(x), -order.index(x)))


def card_frame(cards, snapshot):
    rows = []
    for c in cards:
        if c["snapshot"] != snapshot: continue
        traits = sorted(t[6:] for t in c["tags"] if t.startswith("trait:"))
        rows.append(dict(spId=c["spId"], player=c["spId"] % 1_000_000, name=c["name"], season=c["season"], ovr=c["ovr1"],
                         pay=c["pay"], family=family_of(c["groups"]), traits=tuple(traits),
                         outlier=any(n in c["name"] for n in PRICE_OUTLIERS),
                         ratingCount=c.get("ratingCount")))
    return pd.DataFrame(rows).set_index("spId")


def price_panel(series, grade):
    """Wide frame: rows spId, columns history day, values ln(price) (NaN at/below the floor or missing)."""
    data = {}
    for s in series:
        p = s["p"].get(str(grade))
        if p is None: continue
        data[s["spId"]] = {d: (math.log(v) if v is not None and v > FLOOR else np.nan) for d, v in zip(s["days"], p)}
    panel = pd.DataFrame.from_dict(data, orient="index")
    return panel.reindex(sorted(panel.columns), axis=1)


def load_graphs(path, grade):
    """Lines written by `fch market-backfill`: {spId, grade, fetchedAt, values (daily, oldest first)}."""
    out = []
    opener = gzip.open if path.endswith(".gz") else open
    with opener(path, "rt", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line: continue
            try:
                o = json.loads(line)
            except json.JSONDecodeError:
                continue  # a line cut off by an interrupted run
            if o.get("grade") == grade and o.get("values"):
                out.append(o)
    return out


def align_graphs(graphs, history):
    """The graph has no dates. Its last value is taken as the fetch day (KST) plus an offset (−3..+1 days). Every card
    fetched on the same day shares that offset, so it is the one most cards' own refresh prices agree with (per-card
    choices would add noise: neighbouring days are similar). Returns (panel of ln price, alignment stats)."""
    from datetime import datetime, timedelta
    hist_days = {d: day_of(d) for d in history.columns}
    prepared = []
    votes = defaultdict(list)
    for g in graphs:
        fetched = datetime.fromisoformat(g["fetchedAt"].replace("Z", "+00:00")) + timedelta(hours=9)
        end0 = fetched.date()
        vals = [math.log(v) if v and v > FLOOR else np.nan for v in g["values"]]
        L, best = len(vals), None
        if g["spId"] in history.index:
            known = history.loc[g["spId"]]
            for off in range(-3, 2):
                end = end0 + timedelta(days=off)
                diffs = [abs(vals[L - 1 - (end - dd).days] - known[d]) for d, dd in hist_days.items()
                         if 0 <= L - 1 - (end - dd).days < L and not np.isnan(vals[L - 1 - (end - dd).days]) and not np.isnan(known[d])]
                if len(diffs) >= 3:
                    err = float(np.median(diffs))
                    if best is None or err < best[1]: best = (off, err)
        if best is not None: votes[end0].append(best)
        prepared.append((g["spId"], end0, vals))
    offsets, agree, errs = {}, [], []
    for end0, vs in votes.items():
        offs = [o for o, _ in vs]
        offsets[end0] = max(set(offs), key=offs.count)
        agree.append(offs.count(offsets[end0]) / len(offs))
        errs += [e for o, e in vs if o == offsets[end0]]
    rows = {}
    for sp, end0, vals in prepared:
        end = end0 + timedelta(days=offsets.get(end0, 0))
        rows[sp] = {(end - timedelta(days=len(vals) - 1 - i)).isoformat(): v for i, v in enumerate(vals)}
    panel = pd.DataFrame.from_dict(rows, orient="index")
    panel = panel.reindex(sorted(panel.columns), axis=1)
    return panel, dict(cards=len(prepared), fetchDays=len(votes), offsets={str(k): v for k, v in offsets.items()},
                       agreement=float(np.mean(agree)) if agree else None,
                       medianAbsLogGapToRefreshPrice=float(np.median(errs)) if errs else None,
                       firstDay=panel.columns[0] if len(panel.columns) else None,
                       lastDay=panel.columns[-1] if len(panel.columns) else None)


# ── statistics ─────────────────────────────────────────────────────────────

def t_quantile(df, p=0.975):
    """Student t quantile (Cornish-Fisher); exact enough for df >= 5."""
    z = 1.959963984540054 if p == 0.975 else math_ndtri(p)
    if df <= 0: return float("nan")
    return z + (z**3 + z) / (4 * df) + (5 * z**5 + 16 * z**3 + 3 * z) / (96 * df**2)


def math_ndtri(p):
    # Acklam's approximation, only for other confidence levels.
    a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00]
    b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01]
    c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00]
    d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00]
    q = min(p, 1 - p)
    if q < 0.02425:
        r = math.sqrt(-2 * math.log(q))
        x = (((((c[0]*r+c[1])*r+c[2])*r+c[3])*r+c[4])*r+c[5]) / ((((d[0]*r+d[1])*r+d[2])*r+d[3])*r+1)
    else:
        r = q - 0.5; s = r * r
        x = (((((a[0]*s+a[1])*s+a[2])*s+a[3])*s+a[4])*s+a[5])*r / (((((b[0]*s+b[1])*s+b[2])*s+b[3])*s+b[4])*s+1)
    return -x if p > 0.5 else x


def fe_cluster_ols(y, X, cells, clusters, level=0.95):
    """OLS of y on X after removing cell means (fixed effects), CR1 errors clustered by footballer, t(G−1) intervals.
    Returns dict name -> (beta, lo, hi, se) in log points, plus n, G, cells."""
    df = pd.DataFrame(X).assign(_y=y, _cell=cells, _g=clusters).dropna()
    size = df.groupby("_cell")["_y"].transform("size")
    df = df[size >= MIN_CELL]
    if len(df) < 30: return None
    cols = [c for c in X.columns if df[c].std() > 0]
    if not cols: return None
    dm = df[cols + ["_y"]] - df.groupby("_cell")[cols + ["_y"]].transform("mean")
    Xm, ym = dm[cols].to_numpy(float), dm["_y"].to_numpy(float)
    xtx = Xm.T @ Xm
    try:
        inv = np.linalg.inv(xtx)
    except np.linalg.LinAlgError:
        inv = np.linalg.pinv(xtx)
    beta = inv @ Xm.T @ ym
    e = ym - Xm @ beta
    scores = pd.DataFrame(Xm * e[:, None]).groupby(df["_g"].to_numpy()).sum().to_numpy()
    G, n, k, C = len(scores), len(df), len(cols), df["_cell"].nunique()
    meat = scores.T @ scores
    factor = G / max(G - 1, 1) * (n - 1) / max(n - k - C, 1)
    V = factor * inv @ meat @ inv
    se = np.sqrt(np.clip(np.diag(V), 0, None))
    tc = t_quantile(G - 1, 1 - (1 - level) / 2)
    out = {c: (beta[i], beta[i] - tc * se[i], beta[i] + tc * se[i], se[i]) for i, c in enumerate(cols)}
    players = {c: int(df.loc[df[c] != 0, "_g"].nunique()) for c in cols}
    cards = {c: int((df[c] != 0).sum()) for c in cols}
    return dict(coef=out, n=n, G=G, cells=C, players=players, cards=cards)


def pct(b): return (math.exp(b) - 1) * 100


def verdict(lo, hi, players):
    if players < MIN_PLAYERS: return "표본 부족"
    if lo > 0: return "상승"
    if hi < 0: return "하락"
    return "불확실"


# ── analyses ───────────────────────────────────────────────────────────────

def day_of(s): return date.fromisoformat(s)


def diagnostics(panel, base_cards, base_day):
    days = list(panel.columns)
    old = panel.loc[panel.index.intersection(base_cards.index)]
    out = []
    for prev, cur in zip(days, days[1:]):
        gap = (day_of(cur) - day_of(prev)).days
        r = (old[cur] - old[prev]).dropna()
        out.append(dict(day=cur, gapDays=gap, cards=len(r),
                        typicalDailyMovePct=float(np.median(np.abs(r)) / gap * 100) if len(r) else None,
                        index=float(np.median((old[cur] - old[base_day]).dropna()) * 100) if base_day in old else None))
    return out


def event_study(panel, base, base_day, families, min_players=8):
    """For each family: per day coefficients of the trait dummies on ln p(day) − ln p(base day)."""
    result = {}
    for fam in families:
        cards = base[(base.family == fam) & ~base.outlier]
        cards = cards.loc[cards.index.intersection(panel.index)]
        trait_players = defaultdict(set)
        for sp, row in cards.iterrows():
            for t in row.traits: trait_players[t].add(row.player)
        traits = sorted(t for t, ps in trait_players.items() if len(ps) >= min_players)
        if not traits: continue
        X = pd.DataFrame({t: [1.0 if t in tr else 0.0 for tr in cards.traits] for t in traits}, index=cards.index)
        cells = cards.season.astype(str) + "|" + (cards.ovr // 5 * 5).astype(str)
        paths = {t: [] for t in traits}
        for d in panel.columns:
            if d == base_day: continue
            y = panel.loc[cards.index, d] - panel.loc[cards.index, base_day]
            fit = fe_cluster_ols(y, X, cells, cards.player)
            if fit is None: continue
            for t in traits:
                if t in fit["coef"]:
                    b, lo, hi, se = fit["coef"][t]
                    paths[t].append(dict(day=d, pct=pct(b), lo=pct(lo), hi=pct(hi), se=se, b=b,
                                         players=fit["players"][t], cards=fit["cards"][t], n=fit["n"]))
        result[fam] = dict(traits=traits, paths=paths)
    return result


def change_between(panel, base, d0, d1, families, min_players=8):
    """Same design on ln p(d1) − ln p(d0): did trait cards keep moving relative to similar cards between two days?"""
    out = {}
    for fam in families:
        cards = base[(base.family == fam) & ~base.outlier]
        cards = cards.loc[cards.index.intersection(panel.index)]
        trait_players = defaultdict(set)
        for sp, row in cards.iterrows():
            for t in row.traits: trait_players[t].add(row.player)
        traits = sorted(t for t, ps in trait_players.items() if len(ps) >= min_players)
        if not traits: continue
        X = pd.DataFrame({t: [1.0 if t in tr else 0.0 for tr in cards.traits] for t in traits}, index=cards.index)
        cells = cards.season.astype(str) + "|" + (cards.ovr // 5 * 5).astype(str)
        y = panel.loc[cards.index, d1] - panel.loc[cards.index, d0]
        fit = fe_cluster_ols(y, X, cells, cards.player)
        if fit is None: continue
        out[fam] = {t: dict(pct=pct(b), lo=pct(lo), hi=pct(hi), players=fit["players"][t], cards=fit["cards"][t],
                            verdict=verdict(lo, hi, fit["players"][t]))
                    for t, (b, lo, hi, se) in fit["coef"].items()}
    return out


def ranker_lead_lag(rankers, panel, base, max_lag=5):
    """Pooled across cards and days: Δln(1+users) from chart day t−1 to t versus Δln price from t to t+k (day effects removed)."""
    usage = defaultdict(dict)  # spId -> {match day: users}
    for ch in rankers:
        if ch["rankFrom"] != min(c["rankFrom"] for c in rankers): continue
        day = ch["chartDate"]
        for p in ch["picks"]:
            usage[p["spId"]][day] = usage[p["spId"]].get(day, 0) + p["users"]
    if not usage: return None
    chart_days = sorted({d for u in usage.values() for d in u})
    price_days = list(panel.columns)
    rows = []
    for sp, u in usage.items():
        if sp not in panel.index: continue
        for a, b in zip(chart_days, chart_days[1:]):
            du = math.log1p(u.get(b, 0)) - math.log1p(u.get(a, 0))
            # Charts describe the previous day's matches; the price snapshot of day b is the first one after them.
            starts = [d for d in price_days if d >= b]
            if not starts: continue
            start = starts[0]
            for k in range(0, max_lag + 1):
                ends = [d for d in price_days if (day_of(d) - day_of(start)).days == k] if k > 0 else [start]
                if not ends: continue
                if k == 0:
                    prev = [d for d in price_days if d < start]
                    if not prev: continue
                    r = panel.at[sp, start] - panel.at[sp, prev[-1]]
                else:
                    r = panel.at[sp, ends[0]] - panel.at[sp, start]
                if np.isnan(r): continue
                rows.append(dict(spId=sp, player=sp % 1_000_000, day=b, lag=k, du=du, r=r))
    df = pd.DataFrame(rows)
    out = []
    for k, g in df.groupby("lag"):
        fit = fe_cluster_ols(g["r"].to_numpy(), pd.DataFrame({"du": g["du"].to_numpy()}), g["day"].to_numpy(), g["player"].to_numpy())
        if fit is None or "du" not in fit["coef"]: continue
        b, lo, hi, se = fit["coef"]["du"]
        out.append(dict(lag=int(k), pctPerDoubling=pct(b * math.log(2)), lo=pct(lo * math.log(2)), hi=pct(hi * math.log(2)), n=fit["n"]))
    return out


def watched(season, watch):
    return any(w and w in season for w in watch)


def grade_ratios(series, base, watch=()):
    """Median +10/+8 and +5/+8 price ratios per day, for the watched seasons and for everything else."""
    rows = []
    for s in series:
        p = s["p"]
        if not all(g in p for g in ("5", "8", "10")): continue
        if s["spId"] not in base.index: continue
        part = "watched" if watched(base.season[s["spId"]], watch) else "other"
        for i, d in enumerate(s["days"]):
            p5, p8, p10 = p["5"][i], p["8"][i], p["10"][i]
            if p5 and p8 and p10 and min(p5, p8, p10) > FLOOR:
                rows.append(dict(day=d, part=part, r10=math.log(p10 / p8), r5=math.log(p5 / p8)))
    if not rows: return []
    df = pd.DataFrame(rows)
    return [dict(day=d, part=part, ratio10to8=float(math.exp(g.r10.median())), ratio5to8=float(math.exp(g.r5.median())), cards=len(g))
            for (d, part), g in df.groupby(["day", "part"])]


def season_moves(panel, base, d0, d1, min_players=10):
    """Each season's price change from d0 to d1 against cards of the same family and OVR band in other seasons, traits
    held fixed (footballer-clustered). The trait event study absorbs seasons, so season-wide moves show up only here."""
    cards = base[~base.outlier].loc[base[~base.outlier].index.intersection(panel.index)]
    y = panel.loc[cards.index, d1] - panel.loc[cards.index, d0]
    cells = cards.family.astype(str) + "|" + (cards.ovr // 5 * 5).astype(str)
    # Trait moves are controlled per family, otherwise seasons rich in a rising trait look like rising seasons.
    keys = [f"{f}:{t}" for f, tr in zip(cards.family, cards.traits) for t in tr]
    counts = pd.Series(keys).value_counts() if keys else pd.Series(dtype=int)
    controls = {k: [1.0 if k.split(":", 1)[1] in tr and f == k.split(":", 1)[0] else 0.0 for f, tr in zip(cards.family, cards.traits)]
                for k in counts[counts >= 8].index}
    out = []
    for season, g in cards.groupby("season"):
        if g.player.nunique() < min_players: continue
        X = pd.DataFrame({"s": (cards.season == season).astype(float), **controls}, index=cards.index)
        # ~30 seasons are tested at once, so seasons get 99% intervals to keep chance findings rare.
        fit = fe_cluster_ols(y, X, cells, cards.player, level=0.99)
        if fit is None or "s" not in fit["coef"]: continue
        b, lo, hi, se = fit["coef"]["s"]
        out.append(dict(season=season, pct=pct(b), lo=pct(lo), hi=pct(hi), players=int(g.player.nunique()), cards=len(g),
                        verdict=verdict(lo, hi, int(g.player.nunique()))))
    return sorted(out, key=lambda x: x["pct"])


def compare_audits(old_path, new_path):
    old, new = (json.load(open(p, encoding="utf-8")) for p in (old_path, new_path))
    def traits(a): return {(t["Trait"], t["Scope"]): t for t in a.get("Traits", [])}
    o, n = traits(old), traits(new)
    rows = []
    for key in sorted(set(o) | set(n)):
        a, b = o.get(key), n.get(key)
        rows.append(dict(trait=key[0], scope=key[1],
                         old=None if a is None else (a["Percent"], a["Low"], a["High"], a.get("Players")),
                         new=None if b is None else (b["Percent"], b["Low"], b["High"], b.get("Players"))))
    return dict(oldSnapshot=old.get("Snapshot"), newSnapshot=new.get("Snapshot"), traits=rows)


# ── report ─────────────────────────────────────────────────────────────────

def analyse(panel, base_u, base_day, families, series, rankers, watch):
    """Every analysis on one price panel (refresh history or aligned graphs)."""
    days = list(panel.columns)
    last = days[-1]
    week_ago = min(days, key=lambda d: abs((day_of(last) - day_of(d)).days - 7))
    part = dict(
        days=days,
        diagnostics=diagnostics(panel, base_u, base_day),
        eventStudy=event_study(panel, base_u, base_day, families),
        lastWeek=dict(start=week_ago, end=last, change=change_between(panel, base_u, week_ago, last, families)),
        rankerLeadLag=ranker_lead_lag(rankers, panel, base_u),
        gradeRatios=grade_ratios(series, base_u, watch) if series is not None else [],
        seasonsSinceBase=season_moves(panel, base_u, base_day, last),
        seasonsLastWeek=season_moves(panel, base_u, week_ago, last),
    )
    # Peak-to-now per trait: the post-patch day with the largest |coefficient|, then the same design from there.
    peaks = {}
    for fam, es in part["eventStudy"].items():
        for t, path in es["paths"].items():
            post = [p for p in path if p["day"] > base_day]
            if not post: continue
            peak = max(post, key=lambda p: abs(p["b"]))
            if peak["day"] != last:
                ch = change_between(panel, base_u, peak["day"], last, [fam]).get(fam, {}).get(t)
                peaks.setdefault(fam, {})[t] = dict(peakDay=peak["day"], peakPct=peak["pct"], nowPct=post[-1]["pct"], sincePeak=ch)
    part["peakToNow"] = peaks
    return part


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("export")
    ap.add_argument("--grade", type=int, default=8)
    ap.add_argument("--out")
    ap.add_argument("--old-audit")
    ap.add_argument("--new-audit")
    ap.add_argument("--graphs", help="price graphs from `fch market-backfill` (fills the days the app did not refresh)")
    ap.add_argument("--watch", default="24EP,CU",
                    help="seasons to follow separately in the grade ratios (default: the EVO class inputs, 2026-10-29)")
    args = ap.parse_args()
    args.watch = [w.strip() for w in args.watch.split(",") if w.strip()]

    meta, cards, series, colours, rankers = load(args.export)
    latest = meta["latestSnapshot"]
    patch = next((p for p in meta["patches"] if p.get("snapshot") is not None), None)
    if patch is None:
        raise SystemExit("no pinned pre-patch snapshot in the export")
    base_snap, base_day = patch["snapshot"], patch["historyDay"]
    base = card_frame(cards, base_snap)
    now = card_frame(cards, latest)
    # Same card, same spec before and after: OVR and traits unchanged (a re-collected card can gain tags).
    same = base.index.intersection(now.index)
    unchanged = same[(base.loc[same, "ovr"] == now.loc[same, "ovr"]) & (base.loc[same, "traits"] == now.loc[same, "traits"])]
    base_u = base.loc[unchanged]

    hist = price_panel(series, args.grade)
    if base_day not in hist.columns:
        raise SystemExit(f"pre-patch day {base_day} missing from the price history")
    families = [f for f in ("attack", "mid", "defence", "gk") if (base_u.family == f).any()]
    report = dict(export=dict(exportedAt=meta["exportedAt"], latestSnapshot=latest, baseSnapshot=base_snap, baseDay=base_day,
                              snapshots=meta["snapshots"], grade=args.grade, cardsLatest=len(now), cardsBase=len(base),
                              unchangedCards=len(unchanged), patch=patch, watchSeasons=args.watch,
                              newSeasonsAfterPatch=[s for s in meta["seasons"] if s["firstSeen"][:10] > base_day]))
    report["history"] = analyse(hist, base_u, base_day, families, series, rankers, args.watch)
    if args.graphs:
        graph_panel, stats = align_graphs(load_graphs(args.graphs, args.grade), hist)
        report["graphAlignment"] = stats
        if base_day in graph_panel.columns:
            report["graphs"] = analyse(graph_panel, base_u, base_day, families, None, rankers, args.watch)
    if args.old_audit and args.new_audit:
        report["audits"] = compare_audits(args.old_audit, args.new_audit)

    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            json.dump(report, f, ensure_ascii=False, indent=1, default=float)
    print_report(report)


def print_report(r):
    e = r["export"]
    print(f"# 시세 안정화 분석 · +{e['grade']} · 기준일(패치 전) {e['baseDay']}")
    print("스냅샷: " + ", ".join(f"#{s['id']} {s['kind']} {s['finishedAt'][:10]}" for s in e["snapshots"]))
    print(f"최신 스냅샷 카드 {e['cardsLatest']} · 전후 스펙 동일 카드 {e['unchangedCards']}")
    if e["newSeasonsAfterPatch"]:
        print("패치 뒤 처음 보인 시즌: " + ", ".join(f"{s['name']}({s['firstSeen'][:10]})" for s in e["newSeasonsAfterPatch"]))
    if "graphAlignment" in r:
        a = r["graphAlignment"]
        offs = ", ".join(f"{d} 받음 → 끝 {o:+d}일" for d, o in a["offsets"].items())
        print(f"시세 그래프 {a['cards']}장 · {a['firstDay']} ~ {a['lastDay']} · 날짜 맞춤 {offs} (카드 일치율 {(a['agreement'] or 0):.0%})"
              f" · 갱신 가격과의 중앙 차이 {(a['medianAbsLogGapToRefreshPrice'] or 0) * 100:.1f}%")
    for key, title in (("history", "갱신 기록(앱이 받은 날만)"), ("graphs", "시세 그래프(매일)")):
        if key in r:
            print(f"\n\n======== {title} ========")
            print_part(r[key], e)
    if "audits" in r:
        print("\n## 가격 수준 비교 (valuation-audit, 이전 → 새)")
        for x in r["audits"]["traits"]:
            o, n = x["old"], x["new"]
            fmt = lambda v: "없음" if v is None else f"{v[0]:+.0f}% ({v[1]:+.0f}~{v[2]:+.0f}, {v[3]}명)"
            print(f"  {x['trait']} · {x['scope']}: {fmt(o)} → {fmt(n)}")


def print_part(r, e):
    print(f"기간 {r['days'][0]} ~ {r['days'][-1]} ({len(r['days'])}일)")
    print("\n## 시장 전체: 하루 평균 움직임 (중앙값 |Δln 가격|/일)과 지수 (패치 전 대비 중앙값)")
    for d in r["diagnostics"]:
        if d["typicalDailyMovePct"] is None: continue
        print(f"  {d['day']}  {d['typicalDailyMovePct']:5.2f}%/일  지수 {d['index']:+6.1f}%  ({d['cards']}장, {d['gapDays']}일 간격)")
    print("\n## 특성별 상대 시세 경로 (같은 포지션·시즌·OVR 구간 대비, 패치 전 대비) [추정]")
    for fam, es in r["eventStudy"].items():
        print(f"### {FAMILY_KO[fam]}")
        for t in es["traits"]:
            path = es["paths"][t]
            if not path: continue
            mark = " *9/30 대상" if t in PATCH_TRAITS else ""
            players = path[-1]["players"]
            print(f"  {t}{mark} ({players}명): " + "  ".join(f"{p['day'][5:]} {p['pct']:+.0f}" for p in path))
            last = path[-1]
            print(f"      → 최신 {last['pct']:+.1f}% ({last['lo']:+.1f}~{last['hi']:+.1f}) {verdict(math.log1p(last['lo']/100), math.log1p(last['hi']/100), players)}")
    lw = r["lastWeek"]
    print(f"\n## 최근 1주 ({lw['start']} → {lw['end']}) 상대 변화: 0이면 안정")
    for fam, traits in lw["change"].items():
        for t, c in traits.items():
            print(f"  {FAMILY_KO[fam]} {t}: {c['pct']:+.1f}% ({c['lo']:+.1f}~{c['hi']:+.1f}, {c['players']}명) {c['verdict']}")
    print("\n## 정점 대비 현재")
    for fam, traits in r["peakToNow"].items():
        for t, p in traits.items():
            s = p["sincePeak"]
            tail = "" if s is None else f" · 정점 이후 {s['pct']:+.1f}% ({s['lo']:+.1f}~{s['hi']:+.1f}) {s['verdict']}"
            print(f"  {FAMILY_KO[fam]} {t}: 정점 {p['peakDay']} {p['peakPct']:+.1f}% → 현재 {p['nowPct']:+.1f}%{tail}")
    if r["rankerLeadLag"]:
        print("\n## 랭커 사용 2배 증가 → k일 뒤 시세 (날짜 효과 제거) [추정]")
        for x in r["rankerLeadLag"]:
            print(f"  {x['lag']}일: {x['pctPerDoubling']:+.2f}% ({x['lo']:+.2f}~{x['hi']:+.2f}, n={x['n']})")
    for title, key in (("패치 전 대비", "seasonsSinceBase"), ("최근 1주", "seasonsLastWeek")):
        if r.get(key):
            print(f"\n## 시즌별 상대 시세 ({title}, 같은 포지션 가족·OVR 구간·특성의 다른 시즌 대비, 99% 범위) [추정]")
            for x in r[key]:
                print(f"  {x['season']:<10} {x['pct']:+6.1f}% ({x['lo']:+.1f}~{x['hi']:+.1f}, {x['players']}명) {x['verdict']}")
    if r["gradeRatios"]:
        watch = ",".join(e.get("watchSeasons", []))
        print(f"\n## 강화 가격 비율 (중앙값) · 관찰 시즌 {watch} vs 나머지")
        by_day = defaultdict(dict)
        for g in r["gradeRatios"]: by_day[g["day"]][g["part"]] = g
        for d, parts in sorted(by_day.items()):
            cells = [f"{name} +10/+8 {x['ratio10to8']:.2f} · +5/+8 {x['ratio5to8']:.3f} ({x['cards']}장)"
                     for name, x in (("관찰", parts.get("watched")), ("나머지", parts.get("other"))) if x]
            print(f"  {d}  " + "  |  ".join(cells))


if __name__ == "__main__":
    main()
