"""MV-15: pair two `fch market-validate --rows` runs card by card: mean |log error| difference (new − old) with footballer-resampled
intervals, on the half used for choosing and the half used for confirming, for each part of the market. Medians move by half a point
when any column is added, so they cannot judge a small change.
    python tools/market/mv15_rows.py old.csv new.csv
"""
import csv, math, random, statistics, sys


def load(path):
    out = {}
    for r in csv.reader(open(path, encoding="utf-8")):
        g, sp, player, ovr, price, expected, floor, traits, starts = r
        out[int(g), int(sp)] = dict(player=int(player), ovr=int(ovr), price=int(price), exp=max(int(expected), 1), floor=floor == "1", traits=int(traits), starts=int(starts))
    return out


def interval(pairs, draws=1000):
    by = {}
    for p, d in pairs: by.setdefault(p, []).append(d)
    keys = list(by)
    sums = {k: (sum(v), len(v)) for k, v in by.items()}
    boots = []
    rnd = random.Random(15)
    for _ in range(draws):
        s = n = 0
        for k in rnd.choices(keys, k=len(keys)):
            s += sums[k][0]; n += sums[k][1]
        boots.append(s / n)
    boots.sort()
    return sum(d for _, d in pairs) / len(pairs), boots[int(.025 * draws)], boots[int(.975 * draws)]


def main():
    old, new = load(sys.argv[1]), load(sys.argv[2])
    for g in sorted({k[0] for k in old}):
        rows = [(old[k], new[k]) for k in old if k[0] == g and k in new]
        by_use = sorted(rows, key=lambda t: -t[1]["starts"])
        heavy = {id(t[1]) for t in by_use[:300]}
        heavier = {id(t[1]) for t in by_use[:1000]}
        parts = {"전체": lambda c: True, "실사용 119+": lambda c: c["ovr"] >= 119, "120+ 특성있음": lambda c: c["ovr"] >= 120 and not c["floor"] and c["traits"] > 0,
                 "120+ 특성없음": lambda c: c["ovr"] >= 120 and not c["floor"] and c["traits"] == 0, "경기 많이 300": lambda c: id(c) in heavy, "경기 많이 1000": lambda c: id(c) in heavier}
        print(f"+{g}: {len(rows):,}장")
        for name, test in parts.items():
            sel = [(a, b) for a, b in rows if test(b)]
            med = [statistics.median(abs(x[i]["exp"] / x[i]["price"] - 1) for x in sel) for i in (0, 1)]
            line = f"  {name:<14} {len(sel):>5}장 오차 {med[0]:.1%} → {med[1]:.1%}"
            for lab, half in (("선택", 1), ("확인", 0)):
                pairs = [(a["player"], abs(math.log(b["exp"] / b["price"])) - abs(math.log(a["exp"] / a["price"]))) for a, b in sel if (a["player"] * 2654435761) % 2**32 % 2 == (0 if half == 0 else 1)]
                d, lo, hi = interval(pairs)
                line += f" | {lab} {d:+.3f} ({lo:+.3f}~{hi:+.3f})"
            print(line)


if __name__ == "__main__":
    main()
