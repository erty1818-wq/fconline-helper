"""MV-18: how much the 2개의 심장 trait moved, on the same cards, against cards like them.

Read-only on a copy of the app's market DB. For each base day it compares every card that exists on both days with
the same OVR and season and a real (non-floor) price at the grade, and takes the log price change net of its
position-group x season x five-point-OVR stratum (strata of 3+ cards). New cards (first listed within 28 days of the
latest snapshot) are left out: their launch prices are not evidence (user rule 2026-11-06).
Intervals are 95% bootstraps over footballers. The other key traits are shown as the usual range of a trait's move.

    python -I tools/market/two_hearts.py <market.db> [--grades 11,8] [--out docs/market-value/MV-18-output.txt]
"""
import json
import re
import math
import random
import sqlite3
import sys
from collections import defaultdict
from datetime import datetime, timedelta

HEART = "trait:2개의 심장"
SCOPES = [("공격", ["ST", "CF", "W", "SM", "CAM"]), ("중앙 미드", ["CM", "CDM"]), ("수비", ["CB", "FB"])]
OUTLIERS = ["호날두", "호나우두", "굴리트"]
LIMITED = re.compile(r"TOTS|TOTY|TOTN|ICON|^TKI|^\d{2}IM_")  # MarketGroups.IsLimitedSeason: supply sets their price (MV-19)
FLOOR = 1200
NEW_DAYS = 28
TRAITS = ["2개의 심장", "트릭스터", "라인 브레이커", "스피드스터", "아크로바틱 피니셔", "크로스 포쳐", "체이서", "파이터",
          "커맨더", "와일드 태클러", "블로커", "타이탄", "레이저 슈터", "프레데터"]


def iso(s):
    return datetime.strptime(s[:19], "%Y-%m-%dT%H:%M:%S")


def load(db, snapshot):
    tags = defaultdict(set)
    for grp, sp, tag in db.execute("SELECT grp, sp_id, tag FROM market_tag WHERE snapshot = ?", (snapshot,)):
        tags[sp].add(tag)
    cards = {}
    for grp, sp, name, season, ovr, prices in db.execute(
            "SELECT grp, sp_id, name, season, ovr1, prices FROM market_card WHERE snapshot = ?", (snapshot,)):
        cards[(grp, sp)] = dict(grp=grp, sp=sp, name=name, season=season, ovr=ovr, prices=json.loads(prices))
    return cards, tags


def history(db, day):
    return {(g, sp): json.loads(p) for g, sp, p in db.execute("SELECT grp, sp_id, prices FROM price_history WHERE day = ?", (day,))}


def demeaned(rows):
    """rows: (key, stratum, change) -> {key: change - stratum mean} for strata of 3+."""
    by = defaultdict(list)
    for key, stratum, change in rows:
        by[stratum].append((key, change))
    out = {}
    for items in by.values():
        if len(items) < 3:
            continue
        mean = sum(c for _, c in items) / len(items)
        for key, c in items:
            out[key] = c - mean
    return out


def estimate(values, players, draws=2000, seed=7):
    """Mean of values (log), bootstrapped over footballers. Returns (pct, low, high, cards, players)."""
    if len(values) < 5:
        return None
    by = defaultdict(list)
    for v, p in zip(values, players):
        by[p].append(v)
    groups = list(by.values())
    rnd = random.Random(seed)
    mean = sum(values) / len(values)
    boots = []
    for _ in range(draws):
        pick = [groups[rnd.randrange(len(groups))] for _ in groups]
        flat = [v for g in pick for v in g]
        boots.append(sum(flat) / len(flat))
    boots.sort()
    lo, hi = boots[int(0.025 * draws)], boots[int(0.975 * draws) - 1]
    pct = lambda x: (math.exp(x) - 1) * 100
    return pct(mean), pct(lo), pct(hi), len(values), len(groups)


def fmt(e):
    if e is None:
        return "표본 부족"
    p, lo, hi, n, k = e
    clear = "" if (lo > 0 or hi < 0) and k >= 10 else " (불확실)"
    return f"{p:+.0f}% ({lo:+.0f}~{hi:+.0f}%) · {n}장 · 선수 {k}명{clear}"


def main():
    args = sys.argv[1:]
    path = args[0]
    opt = lambda name, default: args[args.index(name) + 1] if name in args else default
    grades = [g.strip() for g in opt("--grades", "11,8").split(",")]
    rankers_only = opt("--rankers", "no") == "yes"
    out_path = opt("--out", None)
    lines = []
    say = lambda s="": (lines.append(s), print(s))

    db = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    snaps = db.execute("SELECT id, started_at FROM market_snapshot WHERE finished_at IS NOT NULL ORDER BY id").fetchall()
    latest_id, latest_at = snaps[-1]
    latest, tags = load(db, latest_id)
    seen = dict(db.execute("SELECT sp_id, first_seen FROM market_card_seen"))
    cutoff = (iso(latest_at) - timedelta(days=NEW_DAYS)).isoformat()
    is_new = lambda sp: bool(seen.get(sp)) and seen[sp] > cutoff[:19]
    heart_cards = {sp for sp, t in tags.items() if HEART in t}
    new_cards = {c["sp"] for c in latest.values() if is_new(c["sp"])}
    if rankers_only:
        # Only cards rankers field (daily charts + sampled ranker squads, as MarketStore.RankerUse): the rest count as excluded.
        sys.path.insert(0, __import__("os").path.dirname(__file__))
        from mv19_pair import ranker_use
        use = ranker_use(path)
        new_cards |= {c["sp"] for c in latest.values() if use[c["sp"]] == 0}

    say(f"# MV-18 2개의 심장 시세 변화 (같은 카드, 비슷한 카드 대비) [계산]" + (" · 랭커 실사용 카드만" if rankers_only else ""))
    say(f"최신 스냅샷 #{latest_id} ({latest_at[:16]} UTC) · 카드 {len(latest):,}행 · 2개의 심장 카드 {len(heart_cards)}장 (신규 {len(heart_cards & new_cards)}장 제외)")
    say(f"신규 카드(처음 본 지 {NEW_DAYS}일 미만) {len(new_cards):,}장과 공급 한정 시즌(TOTS·TOTY·TOTN·ICON 계열)은 모두 비교에서 뺀다.")

    bases = [(f"스냅샷 #{sid} ({at[:10]})", load(db, sid)[0]) for sid, at in snaps[:-1]]
    days = [d for (d,) in db.execute("SELECT DISTINCT day FROM price_history ORDER BY day")]
    for d in days:
        if d < latest_at[:10]:
            bases.append((f"일별 {d}", {k: dict(prices=v) for k, v in history(db, d).items()}))

    for grade in grades:
        say()
        say(f"## +{grade} 기준")
        for label, base in sorted(bases, key=lambda b: b[0].split()[-1].strip("()")):
            rows_by_scope = {}
            for scope, groups in SCOPES:
                rows = []
                done = set()
                for (grp, sp), c in latest.items():
                    if grp not in groups or sp in done or sp in new_cards or any(n in c["name"] for n in OUTLIERS) or LIMITED.search(str(c["season"]).upper()):
                        continue
                    b = base.get((grp, sp))
                    if not b or (b.get("ovr", c["ovr"]) != c["ovr"]) or (b.get("season", c["season"]) != c["season"]):
                        continue
                    p1, p0 = c["prices"].get(grade, 0), b["prices"].get(grade, 0)
                    if p1 <= FLOOR or p0 <= FLOOR:
                        continue
                    done.add(sp)
                    rows.append((sp, f"{grp}:{c['season']}:{c['ovr'] // 5}", math.log(p1 / p0)))
                rows_by_scope[scope] = demeaned(rows)
            parts = []
            for scope, _ in SCOPES:
                d = rows_by_scope[scope]
                sel = [sp for sp in d if sp in heart_cards]
                parts.append(f"{scope} {fmt(estimate([d[s] for s in sel], [s % 1_000_000 for s in sel]))}")
            say(f"- {label} → 최신: " + " | ".join(parts))

        # The usual range: every key trait over the longest and the latest week window, same method.
        for label, base in [b for b in bases if b[0].startswith("스냅샷")][:1] + [b for b in bases if "10-30" in b[0]][:1]:
            say()
            say(f"### 특성별 같은 방법 ({label} → 최신, +{grade})")
            for scope, groups in SCOPES:
                rows, done = [], set()
                for (grp, sp), c in latest.items():
                    if grp not in groups or sp in done or sp in new_cards or any(n in c["name"] for n in OUTLIERS) or LIMITED.search(str(c["season"]).upper()):
                        continue
                    b = base.get((grp, sp))
                    if not b or b.get("ovr", c["ovr"]) != c["ovr"] or b.get("season", c["season"]) != c["season"]:
                        continue
                    p1, p0 = c["prices"].get(grade, 0), b["prices"].get(grade, 0)
                    if p1 <= FLOOR or p0 <= FLOOR:
                        continue
                    done.add(sp)
                    rows.append((sp, f"{grp}:{c['season']}:{c['ovr'] // 5}", math.log(p1 / p0)))
                d = demeaned(rows)
                for t in TRAITS:
                    sel = [sp for sp in d if f"trait:{t}" in tags.get(sp, ())]
                    e = estimate([d[s] for s in sel], [s % 1_000_000 for s in sel])
                    if e is not None:
                        say(f"  {scope:6} {t:10} {fmt(e)}")

    # Launch reference only (not evidence): EVO card vs the same footballer's base card, by the trait EVO added.
    say()
    say("## 참고: EVO 카드 출시가 / 같은 선수 원본 카드 (신규라 평균·모델에 넣지 않음)")
    by_player = defaultdict(list)
    for (grp, sp), c in latest.items():
        by_player[sp % 1_000_000].append(c)
    seasons = {sid: name for sid, name in db.execute("SELECT season_id, name FROM market_season")}
    for grade in grades:
        ratios = defaultdict(list)
        for (grp, sp), c in latest.items():
            name = seasons.get(sp // 1_000_000, c["season"])
            if not str(c["season"]).upper().startswith("EVO") and "EVO" not in str(name):
                continue
            base = [o for o in by_player[sp % 1_000_000] if o["sp"] != sp and o["ovr"] == c["ovr"] and o["sp"] not in new_cards]
            if not base:
                continue
            p1, p0 = c["prices"].get(grade, 0), base[0]["prices"].get(grade, 0)
            if p1 <= FLOOR or p0 <= FLOOR:
                continue
            added = sorted(t[6:] for t in tags.get(sp, set()) - tags.get(base[0]["sp"], set()) if t.startswith("trait:")) or ["(태그 없음)"]
            ratios[", ".join(added)].append(math.log(p1 / p0))
        say(f"- +{grade}: " + " | ".join(f"{k} ×{math.exp(sorted(v)[len(v) // 2]):.2f} (중앙, {len(v)}장)" for k, v in
                                       sorted(ratios.items(), key=lambda kv: -len(kv[1]))[:12]) if ratios else f"- +{grade}: 비교할 카드 없음")

    if out_path:
        with open(out_path, "w", encoding="utf-8") as f:
            f.write("\n".join(lines) + "\n")


if __name__ == "__main__":
    main()
