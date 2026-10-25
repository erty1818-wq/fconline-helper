"""Which card properties set the price *consistently*? (MV-07)

    python -I tools/market/determinants.py docs/market-value/data/market-export-20261024.jsonl.gz \
        --graphs docs/market-value/data/price-graphs.jsonl --out report.json

Needs numpy and pandas only. Model per position group:

    ln(+8 price) ~ OVR + OVR² + salary + season  (controls, not shrunk)
                 + new traits, height, weight, body type, weak foot, 5-star skills, positions, team colours,
                   detailed stats measured against OVR  (factors, ridge-shrunk)

Price floor: a card's +k price can never go below a fixed multiple of its lower grade (+5 ≥ 1.5×+3, +8 ≥ 9.5×+5,
+10 ≥ 2.1×+8, +11 ≥ 1.55×+10; read off the 10/24 export, where about a quarter of the +8 prices sit exactly on it).
Such a price says only "worth at most this", so it enters as a censored value (Tobit, fitted by EM).

Rating and rating count are left out on purpose: they follow from how many people buy a card, so they are the
price's echo, not its cause, and would soak up the factors' own effect.

A factor counts as "확실" only when every check agrees:
  1. across footballers: footballer cluster bootstrap (1,000 draws) 95% interval excludes 0;
  2. on footballers the fit never saw: 5-fold by footballer, the model without the factor leaves a residual that
     the factor explains in the held-out fifth (footballer-clustered slope, p < 0.05; binary: most holder
     footballers on the effect's side; continuous: footballer-level rank correlation agrees);
  3. that held-out p survives Benjamini-Hochberg over all factors of the group (q < 0.05); without it: "유력";
  4. over time since the 9/30 rebalance: the same specification re-fitted on 10/4, 10/6 and the daily graphs of
     10/10, 10/17 and 10/24 keeps the sign (pre-patch months are reported, not required);
  5. at +5 and +10 (own floors) the sign holds;
  6. dropping any single holder footballer neither flips the sign nor halves the effect.
Across groups, the held-out slopes of one factor are combined (Stouffer) to find what holds in most positions.
On synthetic markets with a known truth, 29 of 30 "확실" calls were right and "유력" was right about half the time.
A coefficient reads "same position group, OVR, salary and season; factor present (or +1) → price ×(1+x%)".
Association in the market [추정]; it does not say NEXON set prices this way.
"""
import argparse, gzip, json, math
from collections import defaultdict
from datetime import date, timedelta

import numpy as np
import pandas as pd

FLOOR = 1200
FLOOR_RATIO = {5: (3, 1.5), 8: (5, 9.5), 10: (8, 2.1), 11: (10, 1.55)}  # grade -> (lower grade, minimum multiple)
FLOOR_TOL = 1.007  # prices are rounded to three significant digits
PRICE_OUTLIERS = ("호날두", "호나우두", "굴리트")
REF_SEASON = "PTG"  # in every position group with 17+ cards, so season premiums compare across groups
POSITION_GROUP = {
    "ST": "ST", "LS": "ST", "RS": "ST", "CF": "CF", "LF": "CF", "RF": "CF", "LW": "W", "RW": "W",
    "LM": "SM", "RM": "SM", "CAM": "CAM", "LAM": "CAM", "RAM": "CAM", "CM": "CM", "LCM": "CM", "RCM": "CM",
    "CDM": "CDM", "LDM": "CDM", "RDM": "CDM", "CB": "CB", "LCB": "CB", "RCB": "CB", "SW": "CB",
    "LB": "FB", "RB": "FB", "LWB": "FB", "RWB": "FB", "GK": "GK",
}
GROUP_KO = {"ST": "스트라이커", "CF": "중앙 공격수", "W": "윙어", "SM": "측면 미드", "CAM": "공격형 미드", "CM": "중앙 미드",
            "CDM": "수비형 미드", "CB": "센터백", "FB": "풀백", "GK": "골키퍼"}
STAT_KO = {
    "sprintspeed": "속력", "acceleration": "가속력", "strength": "몸싸움", "finishing": "골 결정력", "dribbling": "드리블",
    "agility": "민첩성", "shotpower": "슛 파워", "composure": "침착성", "crossing": "크로스", "stamina": "스태미너",
    "shortpassing": "짧은 패스", "longpassing": "긴 패스", "vision": "시야", "longshots": "중거리 슛",
    "interceptions": "가로채기", "standingtackle": "태클", "marking": "대인 수비", "headingaccuracy": "헤더",
    "jumping": "점프", "gkdiving": "GK 다이빙", "gkhandling": "GK 핸들링", "gkreflexes": "GK 반응속도",
    "gkpositioning": "GK 위치 선정", "gkkicking": "GK 킥", "reactions": "반응 속도", "balance": "밸런스",
    "volleys": "발리슛", "curve": "커브", "ballcontrol": "볼 컨트롤", "positioning": "위치 선정", "aggression": "적극성",
    "slidingtackle": "슬라이딩 태클", "penalties": "페널티 킥", "freekickaccuracy": "프리킥 정확도",
}
GK_STATS = {"gkdiving", "gkhandling", "gkreflexes", "gkpositioning", "gkkicking"}
HEIGHT = [(0, 169, "169cm 이하"), (170, 174, "170~174cm"), (175, 179, "175~179cm"), (180, 184, "180~184cm"),
          (185, 189, "185~189cm"), (190, 999, "190cm 이상")]
WEIGHT = [(0, 64, "64kg 이하"), (65, 69, "65~69kg"), (70, 74, "70~74kg"), (75, 79, "75~79kg"), (80, 84, "80~84kg"),
          (85, 999, "85kg 이상")]
MIN_PLAYERS = 10      # a binary factor needs this many footballers on each side to be estimated
RIDGE = 300.0         # estimation strength: on synthetic data with this market's size and noise it recovers known
                      # effects within about 20% (shrunk toward 0, so reported values lean small)
CV_LAMBDAS = (30, 100, 300, 1000, 3000)  # diagnostic only: the strength that predicts unseen footballers best
BOOT = 1000          # at 200 the 97.5% sign share moved by about ±1% between seeds and flipped borderline verdicts
PATCH_DAY = "2026-09-30"
POST_GRAPH_DAYS = ("2026-10-10", "2026-10-17", "2026-10-24")
PRE_GRAPH_DAYS = ("2026-05-15", "2026-06-15", "2026-07-15", "2026-08-15", "2026-09-15")


# ── data ───────────────────────────────────────────────────────────────────

def load(path):
    meta, cards, colours = None, [], []
    with gzip.open(path, "rt", encoding="utf-8") as f:
        for line in f:
            o = json.loads(line)
            if o["t"] == "meta": meta = o
            elif o["t"] == "card": cards.append(o)
            elif o["t"] == "teamColor": colours.append(o)
    return meta, cards, colours


def trait_tags(tags):
    return tuple(sorted(t[6:] for t in tags if t.startswith("trait:")))


def frame(cards, meta, colours):
    latest = meta["latestSnapshot"]
    rows = {}
    for c in cards:
        if c["snapshot"] != latest: continue
        pos = c.get("positions") or {}
        main = next(iter(pos), None)
        group = POSITION_GROUP.get(main) if main else None
        if group is None or group not in c["groups"]:
            group = c["groups"][0] if c["groups"] else None
        tags = set(c["tags"])
        rows[c["spId"]] = dict(
            spId=c["spId"], player=c["spId"] % 1_000_000, name=c["name"], season=c["season"], group=group, ovr=c["ovr1"],
            pay=c["pay"], wf=c["weakFoot"], npos=len(pos), stats=c.get("stats") or {}, traits=trait_tags(tags),
            skill5="skill:5" in tags, thin="body:thin" in tags, heavy="body:heavy" in tags,
            outlier=any(n in c["name"] for n in PRICE_OUTLIERS), prices={int(k): v for k, v in c["prices"].items()})
    df = pd.DataFrame.from_dict(rows, orient="index")
    started = {s["id"]: s["startedAt"] for s in meta["snapshots"]}
    other = {}  # snapshot -> (KST day, {spId: (prices, ovr, traits)})
    for c in cards:
        if c["snapshot"] == latest: continue
        if c["snapshot"] not in other:
            day = (pd.Timestamp(started[c["snapshot"]]) + pd.Timedelta(hours=9)).date().isoformat()
            other[c["snapshot"]] = (day, {})
        other[c["snapshot"]][1][c["spId"]] = ({int(k): v for k, v in c["prices"].items()}, c["ovr1"], trait_tags(c["tags"]))
    member = defaultdict(set)
    for t in colours:
        if t["kind"] not in ("Affiliation", "Feature"): continue
        key = ("소속" if t["kind"] == "Affiliation" else "특성") + " 팀컬러: " + t["name"]
        for m in t["members"]: member[m].add(key)
    df["colours"] = [tuple(sorted(member.get(sp, ()))) for sp in df.index]
    return df, other


def traded(prices):
    vals = [v for v in prices.values() if v]
    return len(set(vals)) > 1


def at_floor(prices, grade):
    """True when the +grade price sits on its floor (a fixed multiple of the lower grade's price)."""
    if grade not in FLOOR_RATIO: return False
    lower, ratio = FLOOR_RATIO[grade]
    p, q = prices.get(grade), prices.get(lower)
    return bool(p and q and p <= ratio * q * FLOOR_TOL)


def load_graph(path, hist_end="2026-10-24"):
    rows = {}
    end = date.fromisoformat(hist_end)  # verified alignment: the graph ends the day before the KST fetch day
    with open(path, encoding="utf-8") as f:
        for line in f:
            try:
                o = json.loads(line)
            except json.JSONDecodeError:
                continue
            if o.get("grade") != 8 or not o.get("values"): continue
            vals = o["values"]
            rows[o["spId"]] = {(end - timedelta(days=len(vals) - 1 - i)).isoformat(): (v if v and v > FLOOR else np.nan)
                               for i, v in enumerate(vals)}
    return pd.DataFrame.from_dict(rows, orient="index")


# ── features ───────────────────────────────────────────────────────────────

def features(g, spec=None):
    """Feature matrix for one position group. `spec` (from the main fit) fixes the columns so every other fit
    estimates the same factors; without it the columns are chosen from this sample."""
    if spec is None:
        spec = {}
        is_gk = (g.group == "GK").all()
        stats = sorted({s for st in g.stats for s in st} - {"height", "weight"})
        stats = [s for s in stats if (s in GK_STATS or s == "reactions")] if is_gk else [s for s in stats if s not in GK_STATS]
        spec["stats"] = [s for s in stats if g.stats.map(lambda d: s in d).mean() >= 0.9]
        spec["ovr_mean"] = float(g.ovr.mean())

        def ref_bucket(stat, buckets):
            vals = g.stats.map(lambda d: d.get(stat))
            counts = [(label, int(((vals >= lo) & (vals <= hi)).sum())) for lo, hi, label in buckets]
            return max(counts, key=lambda x: x[1])[0]
        spec["height_ref"] = ref_bucket("height", HEIGHT)
        spec["weight_ref"] = ref_bucket("weight", WEIGHT)

        binaries = {}
        for t in sorted({t for tr in g.traits for t in tr}):
            binaries["특성: " + t] = ("trait", t)
        for lo, hi, label in HEIGHT:
            if label != spec["height_ref"]: binaries[f"키 {label} (기준 {spec['height_ref']})"] = ("height", lo, hi)
        for lo, hi, label in WEIGHT:
            if label != spec["weight_ref"]: binaries[f"몸무게 {label} (기준 {spec['weight_ref']})"] = ("weight", lo, hi)
        binaries["체형 마름"] = ("thin",)
        binaries["체형 건장"] = ("heavy",)
        binaries["약발 4"] = ("wf", 4)
        binaries["약발 5 (양발)"] = ("wf", 5)
        binaries["개인기 5성"] = ("skill5",)
        binaries["포지션 2개 이상"] = ("npos",)
        for col in sorted({c for cs in g.colours for c in cs}):
            binaries[col] = ("colour", col)
        keep = {}
        for name, rule in binaries.items():
            m = binary_column(g, rule).astype(bool)
            if g.player[m].nunique() >= MIN_PLAYERS and g.player[~m].nunique() >= MIN_PLAYERS: keep[name] = rule
        # Too many colours drown the rest: keep the 25 of each kind with the most footballers.
        colours = sorted((n for n, r in keep.items() if r[0] == "colour"),
                         key=lambda n: -g.player[binary_column(g, keep[n]).astype(bool)].nunique())
        for kind in ("소속", "특성"):
            for n in [c for c in colours if c.startswith(kind)][25:]: keep.pop(n)
        seasons = g.season.value_counts()
        spec["season_ref"] = REF_SEASON if seasons.get(REF_SEASON, 0) >= 5 else seasons.index[0]
        spec["seasons"] = [s for s, n in seasons.items() if n >= 5 and s != spec["season_ref"]]
        # A colour that is one season under another name (25DP, WG, CH ...) is the season control already.
        for name in [n for n, r in keep.items() if r[0] == "colour"]:
            m = binary_column(g, keep[name]).astype(bool)
            if g.season[m].value_counts(normalize=True).iloc[0] >= 0.9: keep.pop(name)
        spec["binaries"] = keep

    cols = {}
    d = g.ovr - spec["ovr_mean"]
    cols["OVR +1"] = d
    cols["OVR²"] = d * d
    cols["급여 +1"] = g.pay.astype(float)
    for s in spec["stats"]:
        cols[f"스탯 {STAT_KO.get(s, s)} +1 (같은 OVR)"] = [st.get(s, o) - o for st, o in zip(g.stats, g.ovr)]
    for name, rule in spec["binaries"].items():
        cols[name] = binary_column(g, rule)
    for s in spec["seasons"]:
        cols["시즌: " + s] = (g.season == s).astype(float)
    cols["시즌: 기타"] = (~g.season.isin(spec["seasons"] + [spec["season_ref"]])).astype(float)
    X = pd.DataFrame(cols, index=g.index).astype(float)
    return X, spec


def binary_column(g, rule):
    kind = rule[0]
    if kind == "trait": return g.traits.map(lambda t: rule[1] in t).astype(float)
    if kind in ("height", "weight"):
        v = g.stats.map(lambda d: d.get(kind, -1))
        return ((v >= rule[1]) & (v <= rule[2])).astype(float)
    if kind == "thin": return g.thin.astype(float)
    if kind == "heavy": return g.heavy.astype(float)
    if kind == "wf": return (g.wf == rule[1]).astype(float)
    if kind == "skill5": return g.skill5.astype(float)
    if kind == "npos": return (g.npos >= 2).astype(float)
    if kind == "colour": return g.colours.map(lambda c: rule[1] in c).astype(float)
    raise ValueError(kind)


def block_of(name):
    if name.startswith("특성:"): return "신규 특성"
    if name.startswith(("키 ", "몸무게 ", "체형")): return "신체 (키·몸무게·체형)"
    if name.startswith(("약발", "개인기")): return "약발·개인기"
    if name.startswith("스탯"): return "세부 능력치"
    if "팀컬러" in name: return "팀컬러"
    if name.startswith("시즌"): return "시즌(클래스)"
    if name.startswith("포지션"): return "포지션 수"
    return "기본 (OVR·급여)"


def is_control(name):
    return block_of(name) in ("기본 (OVR·급여)", "시즌(클래스)")


# ── estimation ─────────────────────────────────────────────────────────────

_ERFC = (-1.26551223, 1.00002368, 0.37409196, 0.09678418, -0.18628806, 0.27886807, -1.13520398, 1.48851587,
         -0.82215223, 0.17087277)


def mills(a):
    """phi(a) / Phi(a), stable for very negative a (Numerical Recipes erfc, relative error < 1.2e-7)."""
    a = np.asarray(a, float)
    u = -a / math.sqrt(2)
    z = np.abs(u)
    t = 1 / (1 + 0.5 * z)
    poly = np.zeros_like(t)
    for c in reversed(_ERFC): poly = poly * t + c
    out = np.empty_like(a)
    neg = u >= 0  # a <= 0: Phi(a) = 0.5 t exp(-u² + poly), phi(a) = exp(-u²)/sqrt(2 pi)
    out[neg] = 2 / (math.sqrt(2 * math.pi) * t[neg] * np.exp(poly[neg]))
    pos = ~neg
    big_phi = 1 - 0.5 * t[pos] * np.exp(-z[pos] ** 2 + poly[pos])
    out[pos] = np.exp(-a[pos] ** 2 / 2) / math.sqrt(2 * math.pi) / big_phi
    return out


def fit(X, y, cens=None, lam=30.0, iters=60):
    """Ridge on standardised columns (controls nearly unpenalised), with censored rows (true value <= observed)
    handled by Tobit EM. Returns coefficients on the original scale."""
    Xv = X.to_numpy(float)
    mu, sd = Xv.mean(axis=0), Xv.std(axis=0)
    sd[sd == 0] = 1
    Z = (Xv - mu) / sd
    pen = np.array([1e-3 if is_control(c) else lam for c in X.columns])
    H = np.linalg.solve(Z.T @ Z + np.diag(pen), Z.T)
    yv = np.asarray(y, float)
    cv = np.zeros(len(yv), bool) if cens is None else np.asarray(cens, bool)
    yi = yv.copy()
    bz = H @ (yi - yi.mean())
    sigma = None
    if cv.any():
        for _ in range(iters):
            mhat = Z @ bz + yi.mean()
            if sigma is None: sigma = max(float(np.std((yv - mhat)[~cv])), 0.05)
            a = (yv[cv] - mhat[cv]) / sigma
            m = mills(a)
            yi[cv] = mhat[cv] - sigma * m
            var_c = sigma ** 2 * np.clip(1 - a * m - m * m, 1e-6, None)
            new = H @ (yi - yi.mean())
            e = yi - (Z @ new + yi.mean())
            sigma = max(math.sqrt((np.sum(e ** 2) + np.sum(var_c)) / len(yv)), 0.05)
            done = np.max(np.abs(new - bz)) < 1e-6
            bz = new
            if done: break
    ym = yi.mean()
    if sigma is None: sigma = max(float(np.std(yv - (Z @ bz + ym))), 0.05)
    return dict(beta=pd.Series(bz / sd, index=X.columns), intercept=ym - (mu / sd) @ bz, sigma=sigma)


def predict(f, X):
    return X.to_numpy(float) @ f["beta"].reindex(X.columns).fillna(0).to_numpy() + f["intercept"]


def residual(f, X, y, cens):
    """Observed minus predicted; for a floor price, the expected residual given that the true value is at most it."""
    yv, pr = np.asarray(y, float), predict(f, X)
    r = yv - pr
    c = np.asarray(cens, bool)
    if c.any(): r[c] = -f["sigma"] * mills(r[c] / f["sigma"])
    return r


def abs_error(f, X, y, cens):
    """Out-of-sample error: |y - pred|, and for a floor price only the part of the prediction above the floor."""
    yv, pr = np.asarray(y, float), predict(f, X)
    e = np.abs(yv - pr)
    c = np.asarray(cens, bool)
    e[c] = np.clip(pr[c] - yv[c], 0, None)
    return e


def fold_of(player):
    return (player * 2654435761) % 2**32 % 5


def pct(b): return (math.exp(max(min(b, 20), -20)) - 1) * 100


def normal_cdf(x):
    return 0.5 * math.erfc(-x / math.sqrt(2))


def spearman(x, y):
    rx, ry = pd.Series(x).rank().to_numpy(), pd.Series(y).rank().to_numpy()
    n = len(x)
    r = float(np.corrcoef(rx, ry)[0, 1]) if n > 2 else 0.0
    tval = r * math.sqrt(max(n - 2, 1) / max(1 - r * r, 1e-12))
    return r, 2 * (1 - normal_cdf(abs(tval)))


def binom_p(k, n):
    """Two-sided sign test p-value (exact up to 25, normal approximation with continuity correction above)."""
    if n == 0: return 1.0
    if n <= 25:
        tail = sum(math.comb(n, i) for i in range(max(k, n - k), n + 1)) / 2 ** n
        return min(1.0, 2 * tail)
    z = (abs(k - n / 2) - 0.5) / math.sqrt(n / 4)
    return 2 * (1 - normal_cdf(z))


# ── analysis ───────────────────────────────────────────────────────────────

def sample(df, group, grade=8, prices=None, flat=None):
    """Cards of one position group with ln price and floor flags. `prices` maps spId -> price dict of another day;
    `flat` maps spId -> a single +8 value (daily graph; floors unknown there)."""
    g = df[(df.group == group) & ~df.outlier].copy()
    if flat is not None:
        p = flat.reindex(g.index)
        cens = pd.Series(False, index=g.index)
    else:
        dicts = g.prices if prices is None else prices.reindex(g.index)
        have = dicts.notna().to_numpy()
        g, dicts = g[have], dicts[have]
        p = dicts.map(lambda d: d.get(grade)).where(dicts.map(traded).astype(bool))
        cens = dicts.map(lambda d: at_floor(d, grade)).astype(bool)
    ok = (p.notna() & (p > FLOOR)).to_numpy()
    g = g[ok]
    g.attrs["group"] = group
    return g, np.log(p[ok].astype(float)), cens[ok].to_numpy()


def rows_by_player(g):
    idx = defaultdict(list)
    for i, p in enumerate(g.player.to_numpy()): idx[p].append(i)
    return {p: np.array(v) for p, v in idx.items()}


def cv_ridge(X, y, cens, folds):
    """Ridge strength that predicts held-out footballers best (mean absolute log error, floor-aware). A large value
    means the factors add little to prediction beyond OVR, salary and season."""
    best = None
    for lam in CV_LAMBDAS:
        errs = []
        for f in range(5):
            t = folds == f
            errs.append(abs_error(fit(X[~t], y[~t], cens[~t], lam=lam), X[t], y[t], cens[t]))
        e = float(np.mean(np.concatenate(errs)))
        if best is None or e < best[1]: best = (lam, e)
    return best[0]


def held_out(X, y, cens, g, folds, lam, factors):
    """For each factor: fit without it on four fifths of the footballers, then ask whether it explains the
    held-out fifth's residuals (Frisch-Waugh: residual on the factor's part not explained by the other columns).
    The slope's error is clustered by footballer."""
    out = {}
    players = g.player.to_numpy()
    for c in factors:
        rest = [k for k in X.columns if k != c]
        r_all, x_all = np.zeros(len(y)), np.zeros(len(y))
        for f in range(5):
            t = folds == f
            m = fit(X.loc[~t, rest], y[~t], cens[~t], lam=lam)
            r_all[t] = residual(m, X.loc[t, rest], y[t], cens[t])
            xm = fit(X.loc[~t, rest], X.loc[~t, c], None, lam=0.1)  # near-OLS, so the part left is orthogonal to every other column
            x_all[t] = X.loc[t, c].to_numpy() - predict(xm, X.loc[t, rest])
        sxx = max(float(np.sum(x_all ** 2)), 1e-12)
        slope = float(np.sum(x_all * r_all) / sxx)
        score = pd.Series(x_all * (r_all - slope * x_all)).groupby(players).sum().to_numpy()
        G = len(score)
        se = math.sqrt(float(np.sum(score ** 2)) * G / max(G - 1, 1)) / sxx
        res = dict(slope=slope, se=se, p=2 * (1 - normal_cdf(abs(slope) / se)) if se > 0 else 1.0)
        if c.startswith(("OVR", "급여", "스탯")):
            per = pd.DataFrame(dict(p=players, r=r_all, x=x_all)).groupby("p").mean()
            res["rho"] = spearman(per.x.to_numpy(), per.r.to_numpy())[0]
        else:
            # Holder footballers: their held-out residual on holder cards against the non-holders' median.
            hold = pd.DataFrame(dict(p=players, r=r_all, h=X[c].to_numpy() > 0.5))
            hr = hold[hold.h].groupby("p").r.mean()
            nr = hold[~hold.h].groupby("p").r.mean()
            base = float(nr.median()) if len(nr) else 0.0
            res.update(holders=len(hr), above=int((hr > base).sum()), typical=float(hr.median() - base))
        out[c] = res
    return out


def analyse_group(df, other, graph, group, rng):
    g, y, cens = sample(df, group)
    if len(g) < 120 or g.player.nunique() < 60: return None
    X, spec = features(g)
    folds = g.player.map(fold_of).to_numpy()
    lam = RIDGE
    main = fit(X, y, cens, lam=lam)
    factors = [c for c in X.columns if c != "OVR²" and not c.startswith("시즌:")]

    # 1. Across footballers: cluster bootstrap (resample whole footballers with all their cards).
    by_p = rows_by_player(g)
    players = np.array(list(by_p))
    boot = []
    for _ in range(BOOT):
        pick = rng.choice(players, size=len(players), replace=True)
        rows = np.concatenate([by_p[p] for p in pick])
        Xb = X.iloc[rows]
        keep = Xb.std() > 0
        boot.append(fit(Xb.loc[:, keep], y.iloc[rows], cens[rows], lam=lam)["beta"].reindex(X.columns))
    boot = pd.DataFrame(boot)

    # 2. Footballers the fit never saw.
    ho = held_out(X, y, cens, g, folds, lam, factors)

    # 3. Over time: other snapshots and daily graphs (same cards, same columns).
    post, pre = {}, {}
    for snap, (day, data) in sorted(other.items()):
        p = pd.Series({sp: v[0] for sp, v in data.items() if sp in g.index and v[1] == g.ovr[sp] and v[2] == g.traits[sp]})
        gs, ys, cs = sample(df, group, prices=p)
        if len(gs) >= 120:
            (post if day >= PATCH_DAY else pre)[f"{day[5:]} 스냅샷"] = fit(features(gs, spec)[0], ys, cs, lam=lam)["beta"]
    if graph is not None:
        for day in POST_GRAPH_DAYS + PRE_GRAPH_DAYS:
            if day not in graph.columns: continue
            gs, ys, cs = sample(df, group, flat=graph[day].dropna())
            if len(gs) >= 120 and gs.player.nunique() >= 60:
                (post if day >= PATCH_DAY else pre)[f"{day[5:]} 그래프"] = fit(features(gs, spec)[0], ys, cs, lam=lam)["beta"]

    # 4. Other grades, each with its own floor.
    grade_fits = {}
    for grade in (5, 10):
        gs, ys, cs = sample(df, group, grade=grade)
        if len(gs) >= 120: grade_fits[f"+{grade}"] = fit(features(gs, spec)[0], ys, cs, lam=lam)["beta"]

    # 5. Out of sample by block: OVR·salary·season alone, then plus each block.
    base_cols = [c for c in X.columns if is_control(c)]
    blocks = sorted({block_of(c) for c in X.columns if not is_control(c)})
    variants = {"OVR·급여·시즌": base_cols, "전체": list(X.columns)}
    for b in blocks: variants["+ " + b] = base_cols + [c for c in X.columns if block_of(c) == b]
    oos = {}
    for name, cols in variants.items():
        errs = [abs_error(fit(X.loc[folds != f, cols], y[folds != f], cens[folds != f], lam=lam),
                          X.loc[folds == f, cols], y[folds == f], cens[folds == f]) for f in range(5)]
        e = np.concatenate(errs)
        oos[name] = dict(mean=float(e.mean()), median=float(np.median(e)))

    # 6. Name value: does the rest of a footballer's cards predict this card's residual?
    resid = residual(main, X, y, cens)
    byp = pd.DataFrame(dict(r=resid, p=g.player.to_numpy()))
    agg = byp.groupby("p").r.agg(["sum", "count"])
    s_, n_ = agg.loc[byp.p, "sum"].to_numpy(), agg.loc[byp.p, "count"].to_numpy()
    multi = n_ >= 2
    name_value = None
    if multi.sum() > 30:
        r_ = resid[multi]
        loo = (s_[multi] - r_) / (n_[multi] - 1)
        name_value = dict(cards=int(multi.sum()), corr=float(np.corrcoef(r_, loo)[0, 1]),
                          spread=float(np.median(np.abs(resid))))

    rows = []
    for c in factors:
        b = main["beta"][c]
        bs = boot[c].dropna()
        lo, hi = np.quantile(bs, [0.025, 0.975]) if len(bs) > 20 else (np.nan, np.nan)
        sign = np.sign(b)

        def agree(d):
            v = [fb[c] for fb in d.values() if c in fb.index and not np.isnan(fb[c])]
            return (float(np.mean([np.sign(x) == sign for x in v])) if v else None, [pct(x) for x in v])
        post_agree, post_vals = agree(post)
        pre_agree, pre_vals = agree(pre)
        grade_agree, grade_vals = agree(grade_fits)
        mask = X[c].to_numpy() > 0.5 if c in spec["binaries"] else None
        rows.append(dict(factor=c, block=block_of(c), pct=pct(b), lo=pct(lo), hi=pct(hi),
                         signProb=float(np.mean(np.sign(bs) == sign)) if len(bs) else None,
                         holders=int(g.player[mask].nunique()) if mask is not None else None,
                         holderCards=int(mask.sum()) if mask is not None else None,
                         heldOut={**ho[c], "slopePct": pct(ho[c]["slope"])},
                         postAgree=post_agree, postVals=post_vals, preAgree=pre_agree, preVals=pre_vals,
                         gradeAgree=grade_agree, gradeVals=grade_vals))

    # 7. Does a binary factor rest on one footballer? Drop each holder in turn (only for factors still standing).
    for f in rows:
        f["dependsOn"] = None
        if f["holders"] is None or not passes_before_drop(f): continue
        c = f["factor"]
        worst = None
        for p in g.player[X[c].to_numpy() > 0.5].unique():
            keep = (g.player != p).to_numpy()
            Xk = X[keep]
            Xk = Xk.loc[:, Xk.std() > 0]
            v = pct(fit(Xk, y[keep], cens[keep], lam=lam)["beta"].get(c, 0.0))
            flipped = bool(np.sign(v) != np.sign(f["pct"]))
            if worst is None or (flipped and not worst[2]) or (flipped == worst[2] and abs(v) < abs(worst[0])):
                worst = (v, p, flipped)
        if worst is not None:
            f["dependsOn"] = dict(withoutPct=worst[0], footballer=df.loc[g.index[(g.player == worst[1]).to_numpy()][0], "name"])
    # Many factors are tested per group: Benjamini-Hochberg q-values on the held-out p-values.
    order = sorted(range(len(rows)), key=lambda i: rows[i]["heldOut"]["p"])
    q = 1.0
    for rank in range(len(order), 0, -1):
        i = order[rank - 1]
        q = min(q, rows[i]["heldOut"]["p"] * len(rows) / rank)
        rows[i]["heldOut"]["q"] = q
    for f in rows:
        f["verdict"] = verdict(f)

    return dict(group=group, cards=len(g), floorCards=int(cens.sum()), players=int(g.player.nunique()), ridge=lam,
                cvRidge=cv_ridge(X, y, cens, folds), nameValue=name_value, postFits=list(post), preFits=list(pre), gradeFits=list(grade_fits),
                seasonRef=spec["season_ref"], heightRef=spec["height_ref"], weightRef=spec["weight_ref"],
                factors=rows, seasons={c[4:]: pct(main["beta"][c]) for c in X.columns if c.startswith("시즌:")},
                seasonCards={k: int(v) for k, v in g.season.value_counts().items()}, outOfSample=oos)


def held_out_ok(f):
    """Replicates on unseen footballers: clustered slope significant with the same sign, and (binary) most holder
    footballers on the effect's side or (continuous) the footballer-level rank correlation agreeing."""
    h = f["heldOut"]
    if np.sign(h["slope"]) != np.sign(f["pct"]) or h["p"] >= 0.05: return False
    if "holders" in h:
        return side(f) > h["holders"] / 2
    return np.sign(h["rho"]) == np.sign(f["pct"])


def side(f):
    h = f["heldOut"]
    return h["above"] if f["pct"] >= 0 else h["holders"] - h["above"]


def passes_before_drop(f):
    return (f["signProb"] or 0) >= 0.975 and held_out_ok(f)


def verdict(f):
    """확실: every check agrees and the held-out test survives the false discovery correction (q < 0.05).
    유력: every check agrees but only before that correction. Weaker tiers say which check failed first."""
    across = (f["signProb"] or 0) >= 0.975
    unseen = held_out_ok(f)
    unseen_sign = np.sign(f["heldOut"]["slope"]) == np.sign(f["pct"])
    time_ok = f["postAgree"] is None or f["postAgree"] >= 0.8
    grade_ok = f["gradeAgree"] is None or f["gradeAgree"] >= 1.0
    single = f["dependsOn"] is not None and (np.sign(f["dependsOn"]["withoutPct"]) != np.sign(f["pct"])
                                              or abs(f["dependsOn"]["withoutPct"]) < abs(f["pct"]) / 2)
    if across and unseen and time_ok and grade_ok and not single:
        return "확실" if f["heldOut"]["q"] < 0.05 else "유력 (다중 검정 보정 전)"
    if across and unseen and single: return "특정 선수 의존"
    if across and unseen: return "새 선수 재현, 시점·강화 불안정"
    if across and unseen_sign: return "평균 효과, 새 선수 재현 약함"
    if across: return "특정 선수 효과 (새 선수에서 반대)"
    if (f["signProb"] or 0) >= 0.9: return "약한 경향"
    return "일관성 없음"


# ── report ─────────────────────────────────────────────────────────────────

ORDER = ["확실", "유력 (다중 검정 보정 전)", "특정 선수 의존", "새 선수 재현, 시점·강화 불안정", "평균 효과, 새 선수 재현 약함", "특정 선수 효과 (새 선수에서 반대)",
         "약한 경향", "일관성 없음"]


def describe(f):
    h = f["heldOut"]
    unseen = f"새 선수 {h['slopePct']:+.1f}% (p={h['p']:.3f}, q={h['q']:.3f})"
    if "holders" in h:
        unseen += f" · 보유 {h['holders']}명 중 {side(f)}명 같은 방향"
    post = f"패치 후 {f['postAgree']:.0%}" if f["postAgree"] is not None else "패치 후 -"
    pre = f" · 패치 전 {f['preAgree']:.0%}" if f["preAgree"] is not None else ""
    grade = " · 강화 " + "/".join(f"{v:+.0f}%" for v in f["gradeVals"]) if f["gradeVals"] else ""
    dep = f" · {f['dependsOn']['footballer']} 빼면 {f['dependsOn']['withoutPct']:+.0f}%" if f["dependsOn"] else ""
    return f"{f['factor']}: {f['pct']:+.1f}% (재표집 {f['lo']:+.0f}~{f['hi']:+.0f}) · {unseen} · {post}{pre}{grade}{dep}"


def pooled(r):
    """One factor across position groups (same name, so the same reference bucket): the held-out slopes combined
    (Stouffer z, inverse-variance mean), with Benjamini-Hochberg q-values over all pooled factors."""
    by = defaultdict(list)
    for g in r["groups"]:
        for f in g["factors"]:
            h = f["heldOut"]
            if h["se"] > 0: by[f["factor"]].append((g["group"], h["slope"], h["se"], f["pct"], f["verdict"]))
    out = []
    for name, v in by.items():
        if len(v) < 3: continue
        z = sum(s / se for _, s, se, _, _ in v) / math.sqrt(len(v))
        w = [1 / se ** 2 for _, _, se, _, _ in v]
        mean = sum(wi * s for wi, (_, s, _, _, _) in zip(w, v)) / sum(w)
        same = sum(np.sign(s) == np.sign(z) for _, s, _, _, _ in v)
        model_same = sum(np.sign(m) == np.sign(z) for _, _, _, m, _ in v)
        out.append(dict(factor=name, groups=[x[0] for x in v], z=z, p=2 * (1 - normal_cdf(abs(z))), heldOutPct=pct(mean),
                        sameSign=int(same), modelSameSign=int(model_same), modelPct={x[0]: x[3] for x in v},
                        common=bool(same >= 0.8 * len(v) and model_same >= 0.8 * len(v))))
    order = sorted(range(len(out)), key=lambda i: out[i]["p"])
    q = 1.0
    for rank in range(len(order), 0, -1):
        i = order[rank - 1]
        q = min(q, out[i]["p"] * len(out) / rank)
        out[i]["q"] = q
    return sorted(out, key=lambda x: x["p"])


def print_report(r):
    for g in r["groups"]:
        print(f"\n## {GROUP_KO[g['group']]} ({g['cards']}장, 하한가 {g['floorCards']}장 · {g['players']}명 · 릿지 {g['ridge']:.0f}"
              f" (예측 최적 {g['cvRidge']}) · 패치 후 {len(g['postFits'])}개 · 패치 전 {len(g['preFits'])}개 · 강화 {', '.join(g['gradeFits'])})")
        o = g["outOfSample"]
        base = o["OVR·급여·시즌"]["mean"]
        print(f"  처음 보는 선수 평균 오차: OVR·급여·시즌 {pct(base):.0f}% (중앙 {pct(o['OVR·급여·시즌']['median']):.0f}%) · "
              f"전체 {pct(o['전체']['mean']):.0f}% · "
              + ", ".join(f"{k} {pct(v['mean']) - pct(base):+.1f}%p" for k, v in o.items() if k.startswith("+ ")))
        if g["nameValue"]:
            n = g["nameValue"]
            print(f"  남은 차이: 카드 중앙 ±{pct(n['spread']):.0f}% · 같은 선수 다른 카드와의 상관 {n['corr']:+.2f} ({n['cards']}장)")
        for f in sorted(g["factors"], key=lambda f: (ORDER.index(f["verdict"]), -abs(f["pct"]))):
            if ORDER.index(f["verdict"]) <= 5:
                print(f"  [{f['verdict']}] {describe(f)}")
    print("\n## 확실 판정 요약 (포지션별)")
    table = defaultdict(dict)
    for g in r["groups"]:
        for f in g["factors"]:
            if f["verdict"] == "확실": table[f["factor"]][g["group"]] = f["pct"]
    for name, by in sorted(table.items(), key=lambda kv: (-len(kv[1]), kv[0])):
        print(f"  {name}: " + ", ".join(f"{GROUP_KO[k]} {v:+.0f}%" for k, v in by.items()))
    print("\n## 여러 포지션을 합친 새 선수 검정 (3개 포지션 이상, q < 0.05; [공통] = 새 선수·모델 모두 80% 이상 포지션에서 같은 방향)")
    for x in r.get("pooled") or []:
        if x["q"] >= 0.05: continue
        model = ", ".join(f"{GROUP_KO[k]} {v:+.0f}%" for k, v in x["modelPct"].items())
        print(f"  [{'공통' if x['common'] else '대체로'}] {x['factor']}: 새 선수 {x['heldOutPct']:+.1f}% (q={x['q']:.4f}) · {len(x['groups'])}개 중 {x['sameSign']}개 같은 방향"
              f" · 모델 {x['modelSameSign']}개 같은 방향 [{model}]")
    print(f"\n## 시즌 프리미엄 (기준 {REF_SEASON}, 같은 OVR·급여·요인)")
    seasons = defaultdict(dict)
    for g in r["groups"]:
        if g["seasonRef"] != REF_SEASON: continue
        for s, v in g["seasons"].items():
            if s != "기타": seasons[s][g["group"]] = v
    rows = []
    for s, by in seasons.items():
        if len(by) < 5: continue
        vals = np.array(list(by.values()))
        same = max((vals > 0).mean(), (vals < 0).mean())
        rows.append((float(np.median(vals)), s, len(by), same, vals.min(), vals.max()))
    for med, s, n, same, lo, hi in sorted(rows, reverse=True):
        print(f"  {s}: 중앙 {med:+.0f}% · {n}개 포지션 중 같은 방향 {same:.0%} · 범위 {lo:+.0f}~{hi:+.0f}%")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("export")
    ap.add_argument("--graphs")
    ap.add_argument("--out")
    ap.add_argument("--groups", help="comma-separated position groups (default: all)")
    ap.add_argument("--seed", type=int, default=20261025, help="bootstrap seed; a verdict that changes with it is borderline")
    args = ap.parse_args()
    meta, cards, colours = load(args.export)
    df, other = frame(cards, meta, colours)
    graph = load_graph(args.graphs) if args.graphs else None
    report = dict(snapshot=meta["latestSnapshot"], refSeason=REF_SEASON, seed=args.seed, groups=[])
    for group in (args.groups.split(",") if args.groups else GROUP_KO):
        # One seed per group, so a group's numbers do not depend on which other groups ran before it.
        r = analyse_group(df, other, graph, group, np.random.default_rng([args.seed, list(GROUP_KO).index(group)]))
        if r: report["groups"].append(r)
    report["pooled"] = pooled(report)
    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            json.dump(report, f, ensure_ascii=False, indent=1, default=float)
    print_report(report)


if __name__ == "__main__":
    main()
