"""MV-12: diagnostics and answers from the chosen pooled model: where the error stays, what each factor is worth.
Intervals resample footballers. Everything is a market association [추정].
    python tools/market/mv12_report.py --out artifacts/mv12_report.txt
"""
import argparse, math, os, sys
from collections import Counter, defaultdict
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import numpy as np
import mv12
from mv12 import Block, design, fit_predict, fold_of, floor_of, load
from mv12_pool import unique_cards, pooled_block, colour_block, compat_block, level_block, ranker_block
from mv12_search import rare_block

NAMES = dict(sprintspeed="속력", acceleration="가속력", agility="민첩성", balance="밸런스", ballcontrol="볼 컨트롤", dribbling="드리블", reactions="반응 속도", composure="침착성", positioning="위치 선정",
             finishing="골 결정력", shotpower="슛 파워", longshots="중거리 슛", volleys="발리 슛", penalties="페널티 킥", curve="감아차기", freekickaccuracy="프리킥", headingaccuracy="헤더", jumping="점프", strength="몸싸움", stamina="스태미너",
             aggression="적극성", shortpassing="짧은 패스", longpassing="긴 패스", vision="시야", crossing="크로스", interceptions="가로채기", marking="대인 수비", standingtackle="태클", slidingtackle="슬라이딩 태클", height="키", weight="몸무게")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=os.path.expandvars(r"%LOCALAPPDATA%\FcHelper\fchelper.db"))
    ap.add_argument("--out", required=True)
    ap.add_argument("--draws", type=int, default=120)
    a = ap.parse_args()
    m = load(a.db); g = 11
    rng = np.random.default_rng(11)
    data = unique_cards(m, g, 115)
    y = np.log([c["prices"][g] for c in data])
    cens = np.array([c["prices"][g] <= floor_of(c["prices"], g) * 1.007 for c in data])
    floors = np.array([floor_of(c["prices"], g) for c in data])
    players = np.array([c["player"] for c in data]); ovr = np.array([c["ovr1"] for c in data], float)
    folds = np.array([fold_of(c["sp_id"]) for c in data])
    blocks = [pooled_block(data, dict(statz=300)), colour_block(data, m), rare_block(data), compat_block(data), ranker_block(data, m)]
    names, X, pen = design(blocks)
    p = np.full(len(y), np.nan)
    for f in range(5):
        tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
        p[te], _, _ = fit_predict(X, pen, y, cens, players, tr, te, floors, ovr=ovr, near=3.0)
    err = np.abs(np.exp(p - y) - 1); res = y - p
    out = [f"카드 {len(y):,}장 · 하한 {cens.mean():.0%} · 5묶음 중앙오차 {np.median(err):.1%}"]
    sib = Counter(players.tolist())
    out.append("\n## 같은 선수 다른 카드 수별 오차")
    for lo, hi, lab in [(1, 1, "없음"), (2, 2, "1장"), (3, 4, "2~3장"), (5, 99, "4장+")]:
        mk = np.array([lo <= sib[x] <= hi for x in players.tolist()])
        if mk.any(): out.append(f"  다른 카드 {lab}: {mk.sum():,}장 · {np.median(err[mk]):.1%}")
    out.append("\n## 시세 구간별 오차")
    for lo, hi in [(0, .5), (.5, 2), (2, 10), (10, 50), (50, 300), (300, 1e9)]:
        mk = (np.exp(y) / 1e8 >= lo) & (np.exp(y) / 1e8 < hi)
        if mk.sum() >= 10: out.append(f"  {lo}~{hi}억: {mk.sum():,}장 · 오차 {np.median(err[mk]):.1%} · 실제−예상 중앙 {np.median(res[mk]):+.2f}")
    out.append("\n## 시즌별 오차 (20장 이상, 큰 순)")
    seas = defaultdict(list)
    for i, c in enumerate(data): seas[c["season"]].append(i)
    for s, ix in sorted(seas.items(), key=lambda kv: -np.median(err[kv[1]])):
        if len(ix) >= 20: out.append(f"  {s}: {len(ix)}장 · {np.median(err[ix]):.1%}")
    out.append("\n## 선수 몫과 카드 몫 (log 표준편차)")
    _, bz, sd = fit_predict(X, pen, y, cens, players, np.arange(len(y)), np.arange(1), floors)
    mu = X.mean(0)
    spec = np.column_stack([np.ones(len(y)), (X - mu) / sd]) @ bz
    r = y - spec
    per = defaultdict(list)
    for i, v in enumerate(r): per[players[i]].append(v)
    multi = [v for v in per.values() if len(v) >= 2]
    within = np.sqrt(np.mean([np.var(v, ddof=1) for v in multi])); between = np.std([np.mean(v) for v in multi])
    out.append(f"  스펙 설명 뒤 선수 사이 {between:.2f} · 같은 선수 카드 사이 {within:.2f} (카드 사이가 곧 설명 안 되는 몫)")
    out.append("\n## 크게 빗나간 카드 (실제 ≫ 예상 / 실제 ≪ 예상, 하한 제외, +1 OVR 119+)")
    idx = [i for i in np.argsort(res) if not cens[i] and ovr[i] >= 119]
    for i in idx[-14:][::-1] + idx[:14]:
        c = data[i]
        out.append(f"  {c['name']} {c['season']} OVR{c['ovr1']} 급여{c['pay']} {c['family']} 시세 {np.exp(y[i]) / 1e8:.2f}억 예상 {np.exp(p[i]) / 1e8:.2f}억 {','.join(c['traits'])}")
    # Bootstrapped coefficients on the full data.
    ids = np.unique(players); ix = {v: np.where(players == v)[0] for v in ids}
    def coef(rows):
        Xr = X[rows]
        _, b, s = fit_predict(X, pen, y, cens, players, rows, rows[:1], floors)
        return b[1:] / s
    full = coef(np.arange(len(y)))
    boots = []
    for _ in range(a.draws):
        pick = np.concatenate([ix[v] for v in rng.choice(ids, len(ids))])
        boots.append(coef(pick))
    boots = np.array(boots)
    lo, hi = np.percentile(boots, [2.5, 97.5], axis=0)
    at = {n: i for i, n in enumerate(names)}
    def pct(v): return (math.exp(v) - 1) * 100
    def show(i, unit=1.0):
        mark = "확실" if lo[i] > 0 or hi[i] < 0 else "불확실"
        return f"{pct(full[i] * unit):+.1f}% ({pct(lo[i] * unit):+.1f}~{pct(hi[i] * unit):+.1f}) {mark}"
    out.append("\n## 특성값: 포지션별 +1 OVR 122 카드 (기본+라인+포지션 항 합산, 재표집 95%)")
    families = sorted({c["family"] for c in data})
    lines = {"ST": "ATT", "CF": "ATT", "W": "ATT", "SM": "ATT", "CAM": "MID", "CM": "MID", "CDM": "MID", "FB": "DEF", "CB": "DEF"}
    traits = sorted({t for c in data for t in c["traits"]})
    for f in families:
        rows = []
        for t in traits:
            n = sum(t in c["traits"] and c["family"] == f for c in data)
            if n < 8 or f"trait:{t}" not in at: continue
            w = np.zeros(len(names))
            for nm, u in [(f"trait:{t}", 1), (f"hinge:{t}", 7), (f"trait:{t}×{lines[f]}", 1), (f"hinge:{t}×{lines[f]}", 7), (f"trait:{t}×{f}", 1), (f"hinge:{t}×{f}", 7)]:
                if nm in at: w[at[nm]] = u
            v, bb = float(w @ full), boots @ w
            l, h = np.percentile(bb, [2.5, 97.5])
            rows.append((v, f"{t} {pct(v):+.0f}% ({pct(l):+.0f}~{pct(h):+.0f}){'*' if l > 0 or h < 0 else ''} n={n}"))
        if rows: out.append(f"  {f}: " + " · ".join(r for _, r in sorted(rows, reverse=True)))
    out.append("\n## 특성×능력치 궁합 (특성 카드에서 그 능력치 +1이 더해 주는 몫, 큰 순)")
    rows = [(abs(full[at[n]]), n) for n in names if "×" in n and n.split("×")[0] in mv12.COMPAT and n.split("×")[1] in NAMES]
    for _, n in sorted(rows, reverse=True)[:25]:
        t, s = n.split("×")
        out.append(f"  {t} × {NAMES[s]}: {show(at[n])}")
    out.append("\n## 급여 +1의 가격 영향 (같은 OVR)")
    for f in families:
        nm = f"pay×{f}"
        if nm in at:
            w = np.zeros(len(names)); w[at["pay"]] = 1; w[at[nm]] = 1
            v, bb = float(w @ full), boots @ w
            l, h = np.percentile(bb, [2.5, 97.5])
            out.append(f"  {f}: {pct(v):+.1f}% ({pct(l):+.1f}~{pct(h):+.1f}){' 확실' if l > 0 or h < 0 else ''}")
    out.append("\n## OVR +1의 가격 영향 (평균 OVR 카드)")
    for f in families:
        nm = f"ovr×{f}"
        if nm in at:
            w = np.zeros(len(names)); w[at["ovr"]] = 1; w[at[nm]] = 1
            out.append(f"  {f}: {pct(float(w @ full)):+.0f}%")
    out.append("\n## 랭커·팀컬러")
    for n, u, lab in [("rankers_card", math.log(2), "그 카드 랭커 사용 수 2배"), ("ranker_any", 1, "랭커 차트 등장 여부"), ("colour_share", .05, "최고 소속 팀컬러 랭커 점유율 +5%p"), ("colour_rank1", 1, "그 팀컬러 그 포지션 OVR 1위"), ("colour_rank3", 1, "그 팀컬러 그 포지션 OVR 3위 안"), ("colour_count", 1, "소속 팀컬러 개수 +1")]:
        out.append(f"  {lab}: {show(at[n], u)}")
    out.append("\n## 능력치 +1 (같은 OVR, 포지션별, 재표집 95%가 0 밖인 것 중 큰 순)")
    for f in families:
        rows = []
        for s, ko in NAMES.items():
            if f"stat:{s}" not in at: continue
            w = np.zeros(len(names)); w[at[f"stat:{s}"]] = 1
            if f"stat:{s}×{f}" in at: w[at[f"stat:{s}×{f}"]] = 1
            v, bb = float(w @ full), boots @ w
            l, h = np.percentile(bb, [2.5, 97.5])
            if l > 0 or h < 0: rows.append((abs(v), f"{ko} {pct(v):+.1f}%"))
        out.append(f"  {f}: " + (" · ".join(r for _, r in sorted(rows, reverse=True)[:9]) or "없음"))
    Path(a.out).write_text("\n".join(out), encoding="utf-8")


if __name__ == "__main__":
    sys.exit(main())
