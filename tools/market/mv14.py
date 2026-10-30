"""MV-14: can demand data lower the error? Usage in the official matches already stored, whether a price moves at all,
team-colour scarcity, and a non-linear correction of the pooled model. Same test as MV-12: chosen on half the footballers,
confirmed on the other half. Needs brotli; lightgbm is optional. Read-only.
    python tools/market/mv14.py --out artifacts/mv14.txt
"""
import argparse, json, math, os, sqlite3, sys
from collections import Counter, defaultdict
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import numpy as np
from mv12 import Block, design, fit_predict, fold_of, floor_of, load
from mv12_pool import unique_cards, pooled_block, colour_block, compat_block, stats_line, paired
from mv12_search import rare_block


def match_usage(db):
    import brotli
    c = sqlite3.connect(Path(db).resolve().as_uri() + "?mode=ro", uri=True)
    card = defaultdict(lambda: dict(n=0, n11=0, rate=0.0, goals=0, assists=0, shots=0, wins=0))
    matches = 0
    for (blob,) in c.execute("SELECT json FROM matches"):
        d = json.loads(brotli.decompress(bytes(blob)))
        matches += 1
        for m in d.get("matchInfo", []):
            won = (m.get("matchDetail") or {}).get("matchResult") == "승"
            for p in m.get("player") or []:
                if p.get("spPosition") == 28: continue  # substitutes did not play
                st = p.get("status") or {}
                u = card[p.get("spId")]
                u["n"] += 1
                u["n11"] += p.get("spGrade", 0) >= 11
                u["rate"] += st.get("spRating") or 0
                u["goals"] += st.get("goal") or 0
                u["assists"] += st.get("assist") or 0
                u["shots"] += st.get("shoot") or 0
                u["wins"] += won
    return card, matches


def usage_block(data, usage, performance, z=30):
    b = Block()
    per_player = Counter()
    for sp, u in usage.items():
        per_player[sp % 1_000_000] += u["n"]
    n = np.array([usage[c["sp_id"]]["n"] if c["sp_id"] in usage else 0 for c in data], float)
    b.add("use_card", np.log1p(n), z=z)
    b.add("use_any", n > 0, z=z)
    b.add("use_player", np.log1p([per_player[c["player"]] for c in data]), z=z)
    if performance:
        # Shrunk toward the mean: ten matches say little.
        rated = [u for u in usage.values() if u["n"] > 0]
        mean = sum(u["rate"] for u in rated) / sum(u["n"] for u in rated)
        rate = [((usage[c["sp_id"]]["rate"] + 10 * mean) / (usage[c["sp_id"]]["n"] + 10) - mean) if c["sp_id"] in usage else 0 for c in data]
        b.add("avg_rating", rate, z=z)
        ga = [((usage[c["sp_id"]]["goals"] + usage[c["sp_id"]]["assists"]) / (usage[c["sp_id"]]["n"] + 10)) if c["sp_id"] in usage else 0 for c in data]
        b.add("goal_assist", ga, z=z)
        wr = [((usage[c["sp_id"]]["wins"] + 5) / (usage[c["sp_id"]]["n"] + 10) - 0.5) if c["sp_id"] in usage else 0 for c in data]
        b.add("win_rate", wr, z=z)
    return b


def scarcity_block(data, m, z=30):
    share = m["colour_share"]
    by = defaultdict(list)
    for c in data:
        for t in c["colours"]:
            by[t, c["family"]].append(c["ovr1"])
    b = Block()
    alts = []
    for c in data:
        ts = [t for t in c["colours"] if share.get(t, 0) > 0.01]
        alts.append(min((sum(o >= c["ovr1"] - 2 for o in by[t, c["family"]]) for t in ts), default=30))
    alts = np.array(alts, float)
    b.add("alts_log", np.log1p(alts), z=z)
    b.add("alts_few", alts <= 3, z=z)
    best = np.array([max((share.get(t, 0) for t in c["colours"]), default=0) for c in data])
    b.add("share×alts", best * np.log1p(alts), z=z)
    return b


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
    cens = np.array([c["prices"][g] <= floor_of(c["prices"], g) * 1.007 for c in data])
    floors = np.array([floor_of(c["prices"], g) for c in data])
    players = np.array([c["player"] for c in data]); ovr = np.array([c["ovr1"] for c in data], float)
    folds = np.array([fold_of(c["sp_id"]) for c in data])
    hold = np.array([(c["player"] * 2654435761) % 2**32 % 2 == 0 for c in data])
    top = ovr >= 119; free = ~cens
    out = []
    usage, matches = match_usage(a.db)
    seen = np.array([c["sp_id"] in usage for c in data])
    out.append(f"경기 {matches:,} · 경기 기록에 나온 카드 {len(usage):,}장 · 분석 카드 {len(y):,}장 중 경기에 나온 카드 {seen.sum():,}장 ({seen.mean():.0%}), 상위 {seen[top].mean():.0%}")
    def run(blocks, **kw):
        _, X, pen = design(blocks)
        p = np.full(len(y), np.nan)
        for f in range(5):
            tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
            p[te], _, _ = fit_predict(X, pen, y, cens, players, tr, te, floors, ovr=ovr, near=3.0, **kw)
        return p
    def row(name, p, ref):
        s = f"  {name:<30} 전체 {stats_line(p, y, np.ones(len(y), bool))[0]:.1%} · 상위 {stats_line(p, y, top)[0]:.1%} · 상위(하한 제외) {stats_line(p, y, top & free)[0]:.1%}"
        if ref is not None:
            for lab, mk in (("선택", ~hold), ("확인", hold)):
                d, lo, hi = paired(p, ref, y, players, mk, rng)
                s += f" | {lab} {d:+.3f} ({lo:+.3f}~{hi:+.3f})"
        out.append(s)
    base = [pooled_block(data, dict(statz=300)), colour_block(data, m), rare_block(data), compat_block(data)]
    p0 = run(base)
    out.append("\n## 앱과 같은 통합 모델에 하나씩 더하기")
    row("기준 (지금 앱)", p0, None)
    use = usage_block(data, usage, False); perf = usage_block(data, usage, True)
    p_use = run(base + [use]); row("+ 경기 사용량", p_use, p0)
    p_perf = run(base + [perf]); row("+ 사용량·평점·공격포인트·승률", p_perf, p0)
    row("+ 팀컬러 안 희소성", run(base + [scarcity_block(data, m)]), p0)
    row("+ 사용량 z=3 (덜 줄임)", run(base + [usage_block(data, usage, False, z=3)]), p0)
    row("+ 사용량·성과 z=3", run(base + [usage_block(data, usage, True, z=3)]), p0)
    out.append("\n## 호가만 걸린 카드가 오차를 키우나 (+11 시세가 스냅샷 사이에 움직였는지)")
    moved = np.zeros(len(y), bool)
    snaps = list(m["prev"].items())[-2:]
    for s, prices in snaps:
        for i, c in enumerate(data):
            old = prices.get((c["grp"], c["sp_id"]), {}).get(g)
            if old and old != c["prices"][g]: moved[i] = True
    for lab, mk in (("움직인 카드", moved), ("안 움직인 카드", ~moved)):
        out.append(f"  {lab}: {mk.sum():,}장 · 하한 {cens[mk].mean():.0%} · 오차(하한 제외) {stats_line(p0, y, mk & free)[0]:.1%} · 상위(하한 제외) {stats_line(p0, y, mk & free & top)[0]:.1%}")
    # Non-linear correction of the residual, each fold on its own training cards.
    try:
        import lightgbm as lgb
    except ImportError:
        lgb = None
    if lgb is None:
        out.append("\n(lightgbm 없음: 비선형 보정 생략)")
    else:
        out.append("\n## 비선형 보정 (통합 모델 잔차를 LightGBM으로, 같은 묶음 안에서만 학습)")
        _, X, pen = design(base + [perf])
        names, Xall, _ = design(base + [perf])
        keep = [j for j, n in enumerate(names) if not n.startswith(("season×", "stat:")) or "×" not in n]
        for rounds, leaves in [(150, 7), (300, 15)]:
            p = np.full(len(y), np.nan)
            for f in range(5):
                tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
                # Out-of-fold linear predictions inside the training set, so the residuals are honest.
                inner = np.full(len(tr), np.nan)
                ifold = np.array([fold_of(data[i]["sp_id"] * 7 + 3) for i in tr])
                for k in range(5):
                    a1, a2 = tr[ifold != k], tr[ifold == k]
                    inner[ifold == k], _, _ = fit_predict(X, pen, y, cens, players, a1, a2, floors, ovr=ovr, near=3.0)
                resid = y[tr] - inner
                params = dict(objective="huber", alpha=0.5, learning_rate=0.03, num_leaves=leaves, min_data_in_leaf=25, bagging_fraction=0.8, bagging_freq=1,
                              feature_fraction=0.3, lambda_l2=10, verbosity=-1, seed=7, num_threads=4)
                mask = ~cens[tr]
                model = lgb.train(params, lgb.Dataset(Xall[tr][mask][:, keep], resid[mask]), num_boost_round=rounds)
                lin, _, _ = fit_predict(X, pen, y, cens, players, tr, te, floors, ovr=ovr, near=3.0)
                p[te] = np.maximum(lin + model.predict(Xall[te][:, keep]), np.log(np.maximum(floors[te], 1)))
            row(f"+ 비선형 {rounds}회 잎{leaves}", p, p_perf)
    out.append("\n## 경기 사용량 값 (전체 자료, 같은 선수 효과 포함)")
    names, X, pen = design(base + [perf])
    _, b, sd = fit_predict(X, pen, y, cens, players, np.arange(len(y)), np.arange(1), floors)
    full = b[1:] / sd
    at = {n: i for i, n in enumerate(names)}
    for n, u, lab in [("use_card", math.log(2), "그 카드 사용 경기 2배"), ("use_any", 1, "경기 기록에 있음"), ("use_player", math.log(2), "그 선수 사용 경기 2배"), ("avg_rating", 0.1, "평균 평점 +0.1"), ("goal_assist", 0.1, "경기당 공격포인트 +0.1"), ("win_rate", 0.05, "승률 +5%p")]:
        out.append(f"  {lab}: {(math.exp(full[at[n]] * u) - 1) * 100:+.1f}%")
    Path(a.out).parent.mkdir(parents=True, exist_ok=True)
    Path(a.out).write_text("\n".join(out), encoding="utf-8")


if __name__ == "__main__":
    sys.exit(main())
