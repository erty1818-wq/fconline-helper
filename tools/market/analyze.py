"""Price-factor report: how much each stat, weak foot, trait, skill move and body type moves the price, per position.

    python analyze.py                 # all groups, +5 and +8
    python analyze.py --pos W --grade 8

Writes data/report.json for other tools, prints a table per group.
"""
import argparse, json, sys

from harvest import DATA, GROUPS
from model import fit, load


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pos", nargs="+", default=list(GROUPS), choices=list(GROUPS))
    ap.add_argument("--grade", type=int, nargs="+", default=[5, 8])
    args = ap.parse_args()
    by_group = load(args.pos)
    report = {}
    for g in args.pos:
        if not by_group.get(g): continue
        for grade in args.grade:
            model, data = fit(g, by_group[g], grade)
            effects = model.effects()
            report[f"{g}+{grade}"] = {"group": g, "grade": grade, "n": model.n, "r2": model.r2,
                                      "effects": effects, "seasons": model.season_effects()}
            print(f"\n=== {GROUPS[g]['name']} ({g}) · +{grade} 가격 · 카드 {model.n}장 · R² {model.r2:.2f} ===")
            print(f"{'요인':<26}{'영향':>8}  {'95% 범위':<17}{'신뢰':<4}{'카드 수':>6}")
            for name, p, lo, hi, t, count in effects:
                mark = "★★" if abs(t) >= 3 else "★" if abs(t) >= 2 else "-"
                print(f"{name:<26}{p:>+7.1f}%  [{lo:+.0f}% ~ {hi:+.0f}%]".ljust(52) + f"{mark:<4}{count if count is not None else '':>6}")
    json.dump(report, open(DATA / "report.json", "w", encoding="utf-8"), ensure_ascii=False, indent=1)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
