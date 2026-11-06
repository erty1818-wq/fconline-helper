"""MV-19: pair two `fch market-validate --rows` runs card by card on the cards both scored.
Columns: grade, sp_id, player, ovr1, price, expected, at_floor, traits, starts. Reports median error per run and the
mean change in |log(price/expected)| with a 95% bootstrap over footballers, by segment (used = started in stored matches).
    python -I tools/market/mv19_pair.py <base.csv> <new.csv> <market.db>
Ranker use per card = users in every stored daily chart + starts in the stored ranker squads (top 96, team-colour rankers).
"""
import csv, math, random, sys
from collections import defaultdict


def read(path):
    rows = {}
    for r in csv.reader(open(path, encoding="utf-8")):
        g, sp, pl, ovr, price, exp, floor, traits, starts = r
        rows[(int(g), int(sp))] = dict(player=int(pl), ovr=int(ovr), price=float(price), exp=max(float(exp), 1), floor=floor == "1",
                                       traits=int(traits), starts=int(starts))
    return rows


def err(r):
    return abs(math.log(r["price"] / r["exp"]))


def med(xs):
    xs = sorted(xs)
    return (xs[(len(xs) - 1) // 2] + xs[len(xs) // 2]) / 2 if xs else float("nan")


def ranker_use(path):
    import json, sqlite3
    from collections import Counter
    db = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    use = Counter()
    for (j,) in db.execute("SELECT json FROM ranker_chart"):
        for p in json.loads(j)["Picks"]:
            use[p["SpId"]] += p["Users"]
    for (v,) in db.execute("SELECT value FROM kv_market WHERE key = 'ranker.squads' OR key LIKE 'official.colorsquads.%'"):
        for squad in json.loads(v):
            for s in squad["Starters"]:
                use[s["SpId"]] += 1
    return use


def main():
    a, b = read(sys.argv[1]), read(sys.argv[2])
    use = ranker_use(sys.argv[3])
    keys = [k for k in a if k in b]
    used = sorted((k for k in keys if a[k]["starts"] > 0), key=lambda k: -a[k]["starts"])
    segments = [
        ("전체(공통 카드)", keys),
        ("실사용(경기 선발 1회+)", used),
        ("많이 쓰인 300장", used[:300]),
        ("많이 쓰인 100장", used[:100]),
        ("+11 140+(+1 119+)·하한 제외", [k for k in keys if a[k]["ovr"] >= 119 and not a[k]["floor"]]),
        ("실사용·+11 140+·하한 제외", [k for k in used if a[k]["ovr"] >= 119 and not a[k]["floor"]]),
        ("안 쓰인 카드", [k for k in keys if a[k]["starts"] == 0]),
        ("랭커 실사용", [k for k in keys if use[k[1]] > 0]),
        ("랭커 실사용 상위 100", sorted((k for k in keys if use[k[1]] > 0), key=lambda k: -use[k[1]])[:100]),
        ("랭커 실사용·하한 제외", [k for k in keys if use[k[1]] > 0 and not a[k]["floor"]]),
        ("랭커 미사용", [k for k in keys if use[k[1]] == 0]),
    ]
    rnd = random.Random(19)
    print(f"공통 카드 {len(keys)}장 · 기준 {len(a)} · 새 {len(b)}")
    print("| 구간 | 카드 | 기준 오차 중앙 | 새 오차 중앙 | 평균 |로그 오차| 차이 (95%) |")
    print("|---|---|---|---|---|")
    for name, ks in segments:
        if len(ks) < 10:
            continue
        by = defaultdict(list)
        for k in ks:
            by[a[k]["player"]].append(err(b[k]) - err(a[k]))
        groups = list(by.values())
        mean = sum(sum(g) for g in groups) / len(ks)
        boots = []
        for _ in range(2000):
            pick = [groups[rnd.randrange(len(groups))] for _ in groups]
            n = sum(len(g) for g in pick)
            boots.append(sum(sum(g) for g in pick) / n)
        boots.sort()
        e = lambda rows, k: abs(math.exp(-math.log(rows[k]["price"] / rows[k]["exp"])) - 1)
        print(f"| {name} | {len(ks)} | {med([e(a, k) for k in ks]):.1%} | {med([e(b, k) for k in ks]):.1%} | {mean:+.3f} ({boots[50]:+.3f}~{boots[1949]:+.3f}) |")


if __name__ == "__main__":
    main()
