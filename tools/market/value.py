"""Value finder: cards that trade below what the model expects for their spec, within a price range.

    python value.py --pos W --grade 8 --min 1억 --max 30억
    python value.py --pos ST CF --grade 5 --max 5000만 --wf 5 --trait "라인 브레이커" --top 20

"Expected price" is what cards with the same OVR, weak foot, salary, stats, tags and season trade for today.
A card far below it is a candidate, not a verdict: the model does not see feel, team colour or every stat.
"""
import argparse, sys

from harvest import GROUPS
from model import STAT_KO, fit, fmt_bp, load, parse_bp, tag_label


def main():
    ap = argparse.ArgumentParser(description="가성비 선수 찾기 (FC온라인 데이터센터 시세 기준, 추정)")
    ap.add_argument("--pos", nargs="+", default=["ST"], choices=list(GROUPS), help="포지션 그룹")
    ap.add_argument("--grade", type=int, default=8, help="강화 단계 (1~13)")
    ap.add_argument("--min", default="0", help="최소 가격 (예: 1억, 5000만)")
    ap.add_argument("--max", default="1000조", help="최대 가격")
    ap.add_argument("--wf", type=int, default=0, help="최소 약발 (예: 5 = 양발만)")
    ap.add_argument("--trait", action="append", default=[], help="반드시 가진 특성 (여러 번 가능)")
    ap.add_argument("--skill", type=int, default=0, help="개인기 5 또는 6성만")
    ap.add_argument("--min-ratings", type=int, default=10, help="구단주 평가 수 하한 (거래가 거의 없는 카드 제외)")
    ap.add_argument("--top", type=int, default=15)
    args = ap.parse_args()
    lo, hi = parse_bp(args.min), parse_bp(args.max)

    by_group = load(args.pos)
    for g in args.pos:
        players = by_group.get(g, [])
        if not players:
            print(f"{g}: 데이터가 없습니다. harvest.py를 먼저 실행하세요."); continue
        model, data = fit(g, players, args.grade)
        picks = []
        for p in data:
            price = p.price_at(args.grade)
            if not lo <= price <= hi or p.rating_count < args.min_ratings or p.weak_foot < args.wf: continue
            if any(f"trait:{t}" not in p.tags for t in args.trait): continue
            if args.skill and f"skill:{args.skill}" not in p.tags: continue
            expected = model.predict(p)
            picks.append((price / expected - 1, p, price, expected))
        picks.sort(key=lambda t: t[0])

        key_stats = GROUPS[g]["stats"][0]
        print(f"\n=== {GROUPS[g]['name']} · +{args.grade} · {fmt_bp(lo)} ~ {fmt_bp(hi) if hi < 10 ** 15 else '상한 없음'}"
              f" · 조건에 맞는 카드 {len(picks)}장 (모델 설명력 R² {model.r2:.2f}) ===")
        print("예상가 = 같은 OVR·약발·급여·능력치·특성·시즌 카드들의 오늘 시세로 계산한 값 [추정]")
        head = "  ".join(f"{STAT_KO.get(s, s)[:3]:>4}" for s in key_stats)
        print(f"{'선수':<12}{'시즌':<9}{'OVR':>4} {'약발':>3} {'급여':>3}  {head}   {'시세':>8} {'예상가':>8} {'차이':>6}  특성/개인기/체형")
        for diff, p, price, expected in picks[:args.top]:
            stats = "  ".join(f"{p.stats.get(s, 0):>4}" for s in key_stats)
            tags = " ".join(tag_label(t) for t in sorted(p.tags))
            print(f"{p.name[:11]:<12}{p.season:<9}{p.ovr_at(args.grade):>4} {p.weak_foot:>3} {p.pay:>3}  {stats}   "
                  f"{fmt_bp(price):>8} {fmt_bp(int(expected)):>8} {diff * 100:>+5.0f}%  {tags}")
        if not picks:
            print("조건에 맞는 카드가 없습니다. 가격대나 조건을 넓혀 보세요.")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
