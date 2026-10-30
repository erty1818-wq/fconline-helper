"""MV-12: which cards rankers field per team colour and position, and how cards they use are priced against the model.
Official ranker chart and the ranker squads the app already stored; read-only. Nicknames are not read or written.
    python tools/market/mv12_rankers.py --out artifacts/mv12_rankers.txt
"""
import argparse, json, math, os, sqlite3, sys
from collections import Counter, defaultdict
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import numpy as np
from mv12 import design, fit_predict, fold_of, floor_of, load
from mv12_pool import unique_cards, pooled_block, colour_block
from mv12_search import rare_block


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=os.path.expandvars(r"%LOCALAPPDATA%\FcHelper\fchelper.db"))
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    m = load(a.db); g = 11
    conn = sqlite3.connect(Path(a.db).resolve().as_uri() + "?mode=ro", uri=True)
    out = []
    chart = json.loads(conn.execute("SELECT json FROM ranker_chart ORDER BY chart_date DESC LIMIT 1").fetchone()[0])
    out.append(f"## 랭커 팀컬러 점유율 (공식 차트 {chart['ChartDate']})")
    out.append("  " + " · ".join(f"{t['Name']} {t['Share']:.1%}" for t in chart["TeamColors"][:14]))
    out.append("  포메이션: " + " · ".join(f"{f['Formation']} {f['Share']:.0%}" for f in chart["Formations"][:5]))
    by = defaultdict(list)
    for p in chart["Picks"]: by[p["Position"]].append(p)
    out.append("\n## 포지션별 랭커 사용 상위 (강화 포함)")
    for pos, rows in by.items():
        out.append(f"  {pos}: " + " · ".join(f"{p['Name']} +{p['Grade']} {p['Users']}명" for p in sorted(rows, key=lambda r: -r["Users"])[:4]))
    names = {c["sp_id"]: (c["name"], c["season"]) for c in m["cards"]}
    row = conn.execute("SELECT value FROM kv_market WHERE key='ranker.squads'").fetchone()
    colours = {}
    for tc, sp in conn.execute("SELECT team_color,sp_id FROM team_color_member"):
        if m["kinds"].get(tc, ("", ""))[0] == "Affiliation": colours.setdefault(tc, set()).add(sp)
    if row:
        squads = json.loads(row[0])
        out.append(f"\n## 랭커 스쿼드 표본 {len(squads)}팀: 소속 팀컬러별 많이 쓴 카드")
        samples = defaultdict(list)
        for s in squads:
            starters = s.get("Starters") or []
            if len(starters) != 11: continue
            for tc, members in colours.items():
                if all(p.get("SpId") in members for p in starters):
                    samples[tc].append(starters); break
        for tc, teams in sorted(samples.items(), key=lambda kv: -len(kv[1]))[:10]:
            count = defaultdict(Counter)
            for team in teams:
                for p in team:
                    count[p.get("Position", "?")][p.get("SpId"), p.get("Grade")] += 1
            parts = []
            for pos in sorted(count):
                (sp, grade), n = count[pos].most_common(1)[0]
                nm = names.get(sp, ("?", "?"))
                parts.append(f"{pos} {nm[0]} {nm[1]} +{grade}({n})")
            out.append(f"  {m['kinds'][tc][1]} {len(teams)}팀: " + " · ".join(parts))
    conn.close()
    # Used against unused cards: model price without any ranker information, on unseen cards.
    data = unique_cards(m, g, 115)
    y = np.log([c["prices"][g] for c in data])
    cens = np.array([c["prices"][g] <= floor_of(c["prices"], g) * 1.007 for c in data])
    floors = np.array([floor_of(c["prices"], g) for c in data])
    players = np.array([c["player"] for c in data]); ovr = np.array([c["ovr1"] for c in data], float)
    folds = np.array([fold_of(c["sp_id"]) for c in data])
    _, X, pen = design([pooled_block(data, dict(statz=300)), colour_block(data, m), rare_block(data)])
    p = np.full(len(y), np.nan)
    for f in range(5):
        tr, te = np.where(folds != f)[0], np.where(folds == f)[0]
        p[te], _, _ = fit_predict(X, pen, y, cens, players, tr, te, floors, ovr=ovr, near=3.0)
    res = y - p
    used = np.array([m["use_card"][c["sp_id"]] > 0 for c in data])
    free = ~cens
    out.append("\n## 랭커 차트에 나온 카드와 아닌 카드 (+11, 하한 제외, 랭커 정보 없이 만든 예상가 대비 실제)")
    for lab, mk in (("차트에 나옴", used & free), ("안 나옴", ~used & free)):
        out.append(f"  {lab}: {mk.sum():,}장 · 시세 중앙 {np.exp(np.median(y[mk])) / 1e8:.1f}억 · 실제/예상 중앙 {math.exp(np.median(res[mk])):.2f}배")
    Path(a.out).write_text("\n".join(out), encoding="utf-8")


if __name__ == "__main__":
    sys.exit(main())
