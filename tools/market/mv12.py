"""MV-12: where does the +11 price error come from? Adds one block of candidate factors at a time to the app's
model and measures unseen cards. Python 3.10+, numpy; lightgbm is optional (non-linear benchmark). Reads the app
DB read-only, no API key or network.
    python tools/market/mv12.py --out artifacts/mv12.txt [--grade 11] [--snapshot N] [--gbm]
Every number is a market association [추정], not a game mechanic or a trade price.
"""
import argparse
from collections import Counter, defaultdict
import json
import math
import os
from pathlib import Path
import sqlite3
import sys

os.environ.setdefault("OPENBLAS_NUM_THREADS", "1")
import numpy as np

GROUPS = ["ST", "CF", "W", "CAM", "CM", "CDM", "FB", "CB", "GK"]
FLOORS = {8: (5, 9.5), 10: (8, 2.1), 11: (10, 1.55)}
OUTLIERS = ["호날두", "호나우두", "굴리트"]
BONUS = {1: 3, 8: 18, 10: 22, 11: 24}
FIELD_STATS = ["sprintspeed", "acceleration", "agility", "balance", "ballcontrol", "dribbling", "reactions", "composure", "positioning",
               "finishing", "shotpower", "longshots", "volleys", "penalties", "curve", "freekickaccuracy", "headingaccuracy", "jumping", "strength", "stamina",
               "aggression", "shortpassing", "longpassing", "vision", "crossing", "interceptions", "marking", "standingtackle", "slidingtackle"]
GK_STATS = ["gkdiving", "gkhandling", "gkkicking", "gkreflexes", "gkpositioning", "reactions", "jumping", "sprintspeed", "acceleration", "strength", "shortpassing", "longpassing", "balance", "agility"]
# What the user and the traits' names suggest; each is tested, none is assumed.
COMPAT = {"라인 브레이커": ["sprintspeed", "acceleration", "agility", "positioning", "finishing", "dribbling"],
          "크로스 포쳐": ["headingaccuracy", "jumping", "strength", "height", "positioning", "finishing"],
          "스피드스터": ["sprintspeed", "acceleration", "agility", "balance", "dribbling"],
          "트릭스터": ["dribbling", "ballcontrol", "agility", "balance", "finishing", "curve"],
          "아크로바틱 피니셔": ["volleys", "agility", "jumping", "finishing", "balance", "composure"],
          "2개의 심장": ["stamina", "shortpassing", "vision", "interceptions", "standingtackle", "aggression"],
          "체이서": ["sprintspeed", "acceleration", "interceptions", "standingtackle", "marking", "stamina"],
          "파이터": ["strength", "aggression", "standingtackle", "marking", "jumping"],
          "타이탄": ["strength", "height", "jumping", "headingaccuracy", "marking"],
          "커맨더": ["vision", "shortpassing", "longpassing", "composure", "interceptions"],
          "와일드 태클러": ["slidingtackle", "standingtackle", "aggression", "sprintspeed", "strength"],
          "블로커": ["marking", "strength", "jumping", "interceptions", "height"]}


def floor_of(p, grade):
    lower, ratio = FLOORS[grade]
    return p.get(lower, 0) * ratio


def load(db, snapshot=None):
    conn = sqlite3.connect(Path(db).resolve().as_uri() + "?mode=ro", uri=True)
    conn.row_factory = sqlite3.Row
    snaps = [r["id"] for r in conn.execute("SELECT id FROM market_snapshot WHERE finished_at IS NOT NULL ORDER BY id")]
    snapshot = snapshot or snaps[-1]
    tags = defaultdict(set)
    for r in conn.execute("SELECT grp,sp_id,tag FROM market_tag WHERE snapshot=?", (snapshot,)):
        tags[r["grp"], r["sp_id"]].add(r["tag"])
    kinds = {r["id"]: (r["kind"], r["name"]) for r in conn.execute("SELECT id,name,kind FROM team_color")}
    member = defaultdict(list)
    for r in conn.execute("SELECT team_color,sp_id FROM team_color_member"):
        if kinds.get(r["team_color"], ("", ""))[0] == "Affiliation":
            member[r["sp_id"]].append(r["team_color"])
    first_seen = {r["name"]: r["first_seen"] for r in conn.execute("SELECT name,first_seen FROM market_season")}
    cards = []
    for r in conn.execute("SELECT * FROM market_card WHERE snapshot=? ORDER BY grp,sp_id", (snapshot,)):
        c = dict(r)
        c["prices"] = {int(k): v for k, v in json.loads(c["prices"]).items()}
        c["stats"] = json.loads(c["stats"])
        c["positions"] = json.loads(c["positions"] or "{}")
        c["player"] = c["sp_id"] % 1_000_000
        t = tags[c["grp"], c["sp_id"]]
        c["traits"] = {x[6:] for x in t if x.startswith("trait:")}
        c["body"] = next((x[5:] for x in t if x.startswith("body:")), "normal")
        c["skill5"] = "skill:5" in t
        c["colours"] = member.get(c["sp_id"], [])
        c["traded"] = len(set(c["prices"].values())) > 1
        cards.append(c)
    # Ranker usage: per card and per footballer over every stored chart; team colours' share of rankers.
    use_card, use_player, colour_share, charts = Counter(), Counter(), Counter(), 0
    for r in conn.execute("SELECT json FROM ranker_chart"):
        j = json.loads(r["json"])
        charts += 1
        for p in j["Picks"]:
            use_card[p["SpId"]] += p["Users"]
            use_player[p["PlayerId"]] += p["Users"]
        for t in j.get("TeamColors", []):
            colour_share[t["Id"]] += t["Share"]
    prev = {}
    for s in snaps[:-1]:
        for r in conn.execute("SELECT grp,sp_id,prices FROM market_card WHERE snapshot=?", (s,)):
            prev.setdefault(s, {})[r["grp"], r["sp_id"]] = {int(k): v for k, v in json.loads(r["prices"]).items()}
    conn.close()
    return dict(cards=cards, use_card=use_card, use_player=use_player, colour_share={k: v / max(charts, 1) for k, v in colour_share.items()},
                first_seen=first_seen, snapshot=snapshot, prev=prev, snaps=snaps, kinds=kinds)


def sample(m, group, cut, grade):
    return [c for c in m["cards"] if c["grp"] == group and c["ovr1"] >= cut and c["traded"] and c["prices"].get(grade, 0) > 1200
            and not any(n in c["name"] for n in OUTLIERS)]


def mills(a):
    from math import erfc
    cdf = 0.5 * np.vectorize(erfc)(-a / math.sqrt(2))
    return np.where(cdf > 1e-300, np.exp(-a * a / 2) / math.sqrt(2 * math.pi) / np.maximum(cdf, 1e-300), -a)


class Block:
    """Columns with a ridge per column: 'z' on standardised columns, 'raw' in the column's own units."""
    def __init__(self): self.names, self.cols, self.pen = [], [], []
    def add(self, name, col, z=None, raw=None):
        self.names.append(name); self.cols.append(np.asarray(col, float)); self.pen.append(("z", z) if z is not None else ("raw", raw))


def base_block(data, group, mean=None):
    b = Block()
    ovr = np.array([c["ovr1"] for c in data], float)
    d = ovr - ovr.mean()
    b.add("ovr", d, z=1e-3); b.add("ovr2", d * d, z=1e-3); b.add("pay", [c["pay"] for c in data], z=1e-3)
    b.add("wf5", [c["weak_foot"] >= 5 for c in data], z=300); b.add("wf4", [c["weak_foot"] == 4 for c in data], z=300)
    for s in (GK_STATS if group == "GK" else FIELD_STATS):
        col = np.array([c["stats"].get(s, c["ovr1"]) - c["ovr1"] for c in data], float)
        if col.std() > 0: b.add("stat:" + s, col, z=300)
    seasons = Counter(c["season"] for c in data)
    base = seasons.most_common(1)[0][0]
    for s, n in seasons.items():
        if s != base and n >= 5: b.add("season:" + s, [c["season"] == s for c in data], z=1e-3)
    b.add("season:rare", [seasons[c["season"]] < 5 for c in data], z=1e-3)
    players = defaultdict(set)
    for c in data:
        for t in c["traits"]: players[t].add(c["player"])
    for t, ps in players.items():
        if len(ps) >= 8:
            h = np.array([t in c["traits"] for c in data], float)
            b.add("trait:" + t, h, raw=2); b.add("hinge:" + t, h * np.maximum(0, ovr - 115), raw=5)
    return b


def extra_blocks(data, group, m):
    ovr = np.array([c["ovr1"] for c in data], float)
    out = {}
    # 1. trait × stat compatibility (stat relative to the card's own OVR; height in cm around the group).
    b = Block()
    for t, stats in COMPAT.items():
        h = np.array([t in c["traits"] for c in data], float)
        if h.sum() < 12: continue
        for s in stats:
            v = np.array([c["stats"].get(s, 0) - (0 if s == "height" else c["ovr1"]) for c in data], float)
            v = v - v.mean()
            b.add(f"{t}×{s}", h * v, z=30)
    out["trait×stat"] = b
    # 2. Ranker usage of the card and of the footballer (any grade), last stored charts.
    b = Block()
    b.add("rankers_card", [math.log1p(m["use_card"][c["sp_id"]]) for c in data], z=3)
    b.add("rankers_player", [math.log1p(m["use_player"][c["player"]]) for c in data], z=3)
    b.add("ranker_any", [m["use_card"][c["sp_id"]] > 0 for c in data], z=3)
    out["랭커 사용"] = b
    # 3. Team colours: how popular the card's best colour is among rankers, and its depth rank there.
    share = m["colour_share"]
    best = [max((share.get(t, 0) for t in c["colours"]), default=0) for c in data]
    b = Block()
    b.add("colour_share", best, z=3)
    b.add("colour_share2", np.array(best) ** 2, z=3)
    b.add("colour_count", [len(c["colours"]) for c in data], z=30)
    top = {}
    for c in data:
        for t in c["colours"]:
            top.setdefault(t, []).append(c["ovr1"])
    rank = []
    for c in data:
        ts = [t for t in c["colours"] if share.get(t, 0) > 0.005]
        rank.append(min((sum(o > c["ovr1"] for o in top[t]) for t in ts), default=50))
    b.add("colour_rank1", [r == 0 for r in rank], z=3); b.add("colour_rank3", [r < 3 for r in rank], z=3); b.add("colour_rank_log", np.log1p(rank), z=3)
    out["팀컬러 인기·깊이"] = b
    # 4. Salary against OVR, non-linear.
    pay = np.array([c["pay"] for c in data], float)
    fit = np.polyval(np.polyfit(ovr, pay, 1), ovr)
    b = Block()
    b.add("pay_excess", pay - fit, z=30); b.add("pay_high", np.maximum(0, pay - fit), z=30); b.add("pay2", (pay - pay.mean()) ** 2, z=30)
    b.add("pay33", pay >= 33, z=30); b.add("pay×ovr", (pay - pay.mean()) * (ovr - ovr.mean()), z=30)
    out["급여 비선형"] = b
    # 5. Stats in absolute level, thresholds (a 135 speed may matter differently from 125).
    b = Block()
    if group != "GK":
        for s in ["sprintspeed", "acceleration", "agility", "balance", "dribbling", "finishing", "strength", "headingaccuracy", "jumping", "shortpassing", "marking"]:
            v = np.array([c["stats"].get(s, 0) for c in data], float)
            med = np.median(v)
            b.add(f"{s}_high", np.maximum(0, v - med), z=30); b.add(f"{s}_top", np.maximum(0, v - np.percentile(v, 85)), z=30)
    h = np.array([c["stats"].get("height", 180) for c in data], float)
    w = np.array([c["stats"].get("weight", 75) for c in data], float)
    b.add("height", h - h.mean(), z=30); b.add("height2", (h - h.mean()) ** 2, z=30); b.add("weight", w - w.mean(), z=30); b.add("bmi", w / (h / 100) ** 2 - 23, z=30)
    out["능력치 수준·키"] = b
    # 6. Season × OVR slope and new-season age.
    b = Block()
    for s, n in Counter(c["season"] for c in data).items():
        if n >= 15: b.add(f"season×ovr:{s}", np.array([c["season"] == s for c in data], float) * (ovr - ovr.mean()), z=30)
    out["시즌×OVR"] = b
    # 7. Body and skill moves again (left out in MV-08).
    b = Block()
    b.add("thin", [c["body"] == "thin" for c in data], z=30); b.add("heavy", [c["body"] == "heavy" for c in data], z=30); b.add("skill5", [c["skill5"] for c in data], z=30)
    out["체형·개인기"] = b
    # 8. Positions: how many, and whether this group is the card's best.
    b = Block()
    b.add("n_positions", [len(c["positions"]) for c in data], z=30)
    b.add("pos_gap", [c["ovr1"] - max(c["positions"].values(), default=c["ovr1"]) for c in data], z=30)
    out["포지션 폭"] = b
    # 9. Community rating (maybe a consequence of price; kept apart from the rest).
    b = Block()
    r = np.array([c["rating"] or 0 for c in data], float); n = np.array([c["rating_count"] or 0 for c in data], float)
    b.add("rating", np.where(n > 0, r - r[n > 0].mean(), 0), z=3); b.add("rated", n > 0, z=3); b.add("log_ratings", np.log1p(n), z=3)
    out["평점·평점 수"] = b
    return out


def design(blocks):
    names, cols, pen = [], [], []
    for b in blocks:
        names += b.names; cols += b.cols; pen += b.pen
    return names, np.column_stack(cols), pen


def fit_predict(X, pen, y, cens, players, train, test, floors, fit_ridge=0.05, ovr=None, near=None):
    """App-style fit on train rows: ridge Tobit with near-free footballer effects, then a leave-one-out effect for the test rows."""
    x = X[train]
    mu, sd = x.mean(0), x.std(0)
    sd[sd < 1e-12] = 1
    z = (x - mu) / sd
    z = np.column_stack([np.ones(len(z)), z])
    k = z.shape[1]
    pz = np.array([1e-9] + [p[1] if p[0] == "z" else p[1] / (s * s) for p, s in zip(pen, sd)])
    ids = players[train]
    uniq, inv, counts = np.unique(ids, return_inverse=True, return_counts=True)
    several = counts >= 2
    a = z.T @ z + np.diag(pz)
    bsum = np.zeros((len(uniq), k))
    np.add.at(bsum, inv, z)
    d = counts + fit_ridge
    a -= (bsum[several] / d[several, None]).T @ bsum[several]
    ainv = np.linalg.inv(a)
    w = z.copy()
    m = several[inv]
    w[m] -= bsum[inv[m]] / d[inv[m], None]
    yt, ct = y[train], cens[train]
    yi = yt.copy()
    def solve(target):
        b = ainv @ (w.T @ target)
        r = target - z @ b
        e = np.bincount(inv, weights=r, minlength=len(uniq)) / d
        e[~several] = 0
        return b, e[inv]
    b, e = solve(yi)
    sigma = max(float(np.std((yt - z @ b - e)[~ct])), .05) if (~ct).any() else .05
    for _ in range(40 if ct.any() else 0):
        p = z @ b + e
        t = (yt[ct] - p[ct]) / sigma
        ml = mills(t)
        yi[ct] = p[ct] - sigma * ml
        var = sigma * sigma * np.maximum(1 - t * ml - ml * ml, 1e-6)
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
    effect = dict(zip(uniq.tolist(), (sums / (counts + shrink)).tolist()))
    zt = np.column_stack([np.ones(len(test)), (X[test] - mu) / sd])
    if near is None:
        pred = zt @ b + np.array([effect.get(p, 0.0) for p in players[test]])
    else:
        # A footballer's other cards count more the closer they are in OVR.
        rows = defaultdict(list)
        for i, r in zip(train, residual): rows[players[i]].append((ovr[i], r))
        eff = []
        for i in test:
            v = rows.get(players[i], [])
            w = [math.exp(-abs(o - ovr[i]) / near) for o, _ in v]
            eff.append(sum(a * r for a, (_, r) in zip(w, v)) / (sum(w) + shrink) if v else 0.0)
        pred = zt @ b + np.array(eff)
    return np.maximum(pred, np.log(np.maximum(floors[test], 1))), b, sd


def fold_of(sp):
    mask = (1 << 64) - 1
    x = sp & mask
    x ^= x >> 33; x = (x * 0xff51afd7ed558ccd) & mask; x ^= x >> 33; x = (x * 0xc4ceb9fe1a85ec53) & mask; x ^= x >> 33
    return x % 5


def cv(X, pen, y, cens, players, folds, floors):
    pred = np.full(len(y), np.nan)
    for f in range(5):
        tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
        if len(tr) >= 50 and len(te):
            pred[te], _, _ = fit_predict(X, pen, y, cens, players, tr, te, floors)
    return pred


def err(pred, y, mask):
    return float(np.median(np.abs(np.exp(pred[mask] - y[mask]) - 1))) if mask.any() else float("nan")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=os.path.expandvars(r"%LOCALAPPDATA%\FcHelper\fchelper.db"))
    ap.add_argument("--out", required=True)
    ap.add_argument("--grade", type=int, default=11)
    ap.add_argument("--cut", type=int, default=115)
    ap.add_argument("--snapshot", type=int)
    ap.add_argument("--gbm", action="store_true")
    a = ap.parse_args()
    m = load(a.db, a.snapshot)
    g = a.grade
    out = [f"스냅샷 {m['snapshot']} · +{g} · +1 OVR {a.cut}+ · 5묶음(카드) 중앙오차. 선택용 선수 80%/확인용 선수 20%로 나눔"]
    # 1. Noise floor: how much the same card's shown price moves between snapshots.
    now = {(c["grp"], c["sp_id"]): c for c in m["cards"]}
    for s, prices in m["prev"].items():
        moves = [abs(c["prices"][g] / prices[k][g] - 1) for k, c in now.items()
                 if k in prices and c["ovr1"] >= 119 and c["traded"] and c["prices"].get(g, 0) > 1200 and prices[k].get(g, 0) > 1200]
        if moves: out.append(f"시세 자체의 흔들림: 스냅샷 {s}→{m['snapshot']} 같은 카드 +{g} 변화 중앙값 {np.median(moves):.1%} ({len(moves):,}장, +1 OVR 119+)")
    rows = defaultdict(dict)
    summary = Counter()
    for group in GROUPS:
        data = sample(m, group, a.cut, g)
        if len(data) < 80: continue
        y = np.log([c["prices"][g] for c in data])
        cens = np.array([c["prices"][g] <= floor_of(c["prices"], g) * 1.007 for c in data])
        floors = np.array([floor_of(c["prices"], g) for c in data])
        players = np.array([c["player"] for c in data])
        folds = np.array([fold_of(c["sp_id"]) for c in data])
        hold = np.array([(c["player"] * 2654435761) % 2**32 % 5 == 0 for c in data])  # never used to choose blocks
        top = np.array([c["ovr1"] >= 119 for c in data])
        free = ~cens
        base = base_block(data, group)
        _, X0, pen0 = design([base])
        p0 = cv(X0, pen0, y, cens, players, folds, floors)
        res = dict(n=len(data), floor=float(cens.mean()), base=dict(sel=err(p0, y, ~hold), hold=err(p0, y, hold), top=err(p0, y, top), topfree=err(p0, y, top & free)))
        blocks = extra_blocks(data, group, m)
        res["add"] = {}
        for name, b in blocks.items():
            if not b.names: continue
            _, X1, pen1 = design([base, b])
            p1 = cv(X1, pen1, y, cens, players, folds, floors)
            res["add"][name] = dict(sel=err(p1, y, ~hold), hold=err(p1, y, hold), top=err(p1, y, top), topfree=err(p1, y, top & free))
        # Greedy: keep the blocks that lower the selection error, then report on the held-out footballers.
        chosen, best = [], res["base"]["sel"]
        for name, v in sorted(res["add"].items(), key=lambda kv: kv[1]["sel"]):
            if name == "평점·평점 수": continue
            _, X1, pen1 = design([base] + [blocks[n] for n in chosen] + [blocks[name]])
            p1 = cv(X1, pen1, y, cens, players, folds, floors)
            e = err(p1, y, ~hold)
            if e < best - 0.002: chosen.append(name); best = e; pbest = p1
        if chosen:
            res["greedy"] = dict(blocks=chosen, sel=best, hold=err(pbest, y, hold), top=err(pbest, y, top), topfree=err(pbest, y, top & free))
        # Diagnostic only: the card's own +10 price (the market already priced this card; cannot be used for value finding).
        own = np.log(np.maximum([c["prices"].get(10, 1) for c in data], 1))
        b = Block(); b.add("own+10", own - own.mean(), z=1e-3)
        _, X2, pen2 = design([base, b])
        p2 = cv(X2, pen2, y, cens, players, folds, floors)
        res["own10"] = dict(sel=err(p2, y, ~hold), top=err(p2, y, top), topfree=err(p2, y, top & free))
        rows[group] = res
        out.append(f"\n## {group}: {len(data)}장 · 하한 {cens.mean():.0%} · 기본 오차 선택 {res['base']['sel']:.1%} · 확인 {res['base']['hold']:.1%} · 상위 {res['base']['top']:.1%} · 상위(하한 제외) {res['base']['topfree']:.1%}")
        for name, v in sorted(res["add"].items(), key=lambda kv: kv[1]["sel"]):
            out.append(f"  {name:<14} 선택 {v['sel']:.1%} ({(v['sel'] - res['base']['sel']) * 100:+.1f}) · 확인 {v['hold']:.1%} ({(v['hold'] - res['base']['hold']) * 100:+.1f}) · 상위 {v['top']:.1%} ({(v['top'] - res['base']['top']) * 100:+.1f})")
        if "greedy" in res:
            v = res["greedy"]
            out.append(f"  ▶ 선택된 묶음 {v['blocks']}: 선택 {v['sel']:.1%} · 확인 {v['hold']:.1%} ({(v['hold'] - res['base']['hold']) * 100:+.1f}) · 상위 {v['top']:.1%} · 상위(하한 제외) {v['topfree']:.1%}")
        out.append(f"  (진단) 자기 +10 시세를 알면: 선택 {res['own10']['sel']:.1%} · 상위 {res['own10']['top']:.1%} · 상위(하한 제외) {res['own10']['topfree']:.1%}")
        if a.gbm:
            try:
                import lightgbm as lgb
            except ImportError:
                out.append("  (lightgbm 없음)")
            else:
                allb = [base] + [blocks[n] for n in blocks if n != "평점·평점 수"]
                _, Xg, _ = design(allb)
                pg = np.full(len(y), np.nan)
                for f in range(5):
                    tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
                    # The footballer's other cards as a feature, computed inside the fold.
                    sums = defaultdict(list)
                    for i in tr: sums[players[i]].append(y[i] - 0.45 * data[i]["ovr1"])
                    feat = np.array([np.mean(sums[players[i]]) if players[i] in sums else np.nan for i in range(len(y))])
                    fe_tr = feat.copy()
                    for i in tr:
                        rest = [v for v in sums[players[i]]]
                        v = y[i] - 0.45 * data[i]["ovr1"]
                        fe_tr[i] = (sum(rest) - v) / (len(rest) - 1) if len(rest) > 1 else np.nan
                    Xa = np.column_stack([Xg, fe_tr]); Xb = np.column_stack([Xg, feat])
                    params = dict(objective="regression", learning_rate=0.03, num_leaves=15, min_data_in_leaf=8, bagging_fraction=0.8, bagging_freq=1, feature_fraction=0.5, verbosity=-1, seed=7, num_threads=4)
                    model = lgb.train(params, lgb.Dataset(Xa[tr], y[tr]), num_boost_round=400)
                    pg[te] = np.maximum(model.predict(Xb[te]), np.log(np.maximum(floors[te], 1)))
                out.append(f"  (비선형 벤치마크 LightGBM, 평점 제외) 선택 {err(pg, y, ~hold):.1%} · 확인 {err(pg, y, hold):.1%} · 상위 {err(pg, y, top):.1%} · 상위(하한 제외) {err(pg, y, top & free):.1%}")
    Path(a.out).parent.mkdir(parents=True, exist_ok=True)
    Path(a.out).write_text("\n".join(out), encoding="utf-8")
    Path(a.out + ".json").write_text(json.dumps(rows, ensure_ascii=False, indent=1), encoding="utf-8")


if __name__ == "__main__":
    sys.exit(main())
