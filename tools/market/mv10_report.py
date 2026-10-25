"""Build the committed, aggregate-only MV-10 report from mv10.py / mv10_cli.ps1 output.

    python tools/market/mv10_report.py --input artifacts/mv10 --out docs/market-value/MV-10.md \
        --json docs/market-value/data/mv10-summary-20261026.json.gz
No raw database, nicknames, credentials or per-card prices are copied into the report.
"""
import argparse
import gzip
import json
from pathlib import Path

GROUPS = ["ST","CF","W","CAM","CM","CDM","FB","CB","GK"]
NAMES = dict(zip(GROUPS,["스트라이커","중앙 공격수","윙어","공격형 미드","중앙 미드","수비형 미드","풀백","센터백","골키퍼"]))
FOCUS = {"ST":["크로스 포쳐","라인 브레이커"],"CF":["크로스 포쳐","라인 브레이커"],
         "W":["라인 브레이커","크로스 포쳐","트릭스터","스피드스터"]}


def table(headers, rows):
    return "\n".join(["| " + " | ".join(headers) + " |", "|" + "---|"*len(headers)]
                     + ["| " + " | ".join(map(str,r)) + " |" for r in rows])+"\n"


def pct(v):
    return "—" if v is None or isinstance(v,str) else f"{100*v:.1f}%"


def residual(obj):
    v=obj["MedianLogActualExpected"]
    return "—" if v is None else f"{v:+.2f} (n={obj['Cards']})"


def main():
    ap=argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--input",type=Path,required=True)
    ap.add_argument("--out",type=Path,required=True)
    ap.add_argument("--json",type=Path,required=True)
    args=ap.parse_args()
    def read(name):
        return json.loads((args.input/name).read_text(encoding="utf-8-sig"))
    liquidity=read("liquidity.json")["liquidity"]
    baseline=read("app-baseline.json")
    final=read("app-final.json")
    shape_models=read("app-attack.json")["Models"]+read("app-other.json")["Models"]
    calibration=read("app-calibration.json")
    fe=read("fe-final.json")
    model={(r["Group"],r["Grade"],r["MinOvr1"]):r for r in final["Models"]}
    old={(r["Group"],r["Grade"],r["MinOvr1"]):r for r in baseline["Models"]}
    # Retain all cuts, shapes, trait estimates and null distributions, without repeated unpriced UI factors or BP values.
    def tidy(row):
        row=dict(row)
        row["Factors"]=[{k:f[k] for k in ["Key","Name","Percent","Low","High","Players","Comparisons","PerOvr","Critical","Clear"]}
                        for f in row["Factors"] if f["Estimated"]]
        return row
    summary={"snapshot":final["Snapshot"],"default_grade":11,"default_min_ovr":140,"min_ovr1":119,
             "trait_hinge":115,"trait_reference_ovr1":122,"trait_critical":3.0,"playable_margin":4,
             "liquidity":liquidity,"app_final":[tidy(r) for r in final["Models"]],
             "app_before":[tidy(r) for r in baseline["Models"]],"app_shapes":[tidy(r) for r in shape_models],
             "app_calibration":[tidy(r) for r in calibration["Models"]],
             "within_player_season_tobit":{"specification":fe["specification"],"trait_shape":fe["trait_shape"],"fits":fe["fits"],"margins":fe["margins"]},
             "limitations":["Displayed price is not a completed trade; no grade-specific volumes or execution records.",
                            "No +11 daily history in the nine stored dates. Snapshots 10 and 11 are two observations, not a daily series.",
                            "Partial card folds at tiny cuts are not complete CV; GK OVR1 120 has fewer than 60 cards.",
                            "Model selection reused these folds; no untouched final test set. Intervals are ridge/Tobit CR1 approximations.",
                            "UI capture and coordinate input failed; value/factors/trait screens need visual verification."]}
    args.json.parent.mkdir(parents=True,exist_ok=True)
    encoded=(json.dumps(summary,ensure_ascii=False,indent=2,allow_nan=False)+"\n").encode("utf-8")
    args.json.write_bytes(gzip.compress(encoded,mtime=0) if args.json.suffix==".gz" else encoded)
    text=["# MV-10 · 실사용 +11 값어치 모델 (2026-10-26)",
          "완료 스냅샷 11 (2026-10-25 23:36 KST), 원본 DB는 SQLite `mode=ro`·`query_only`로 읽었다. CLI는 시장 테이블만 복제한 DB를 썼다. 분석 중 API 요청은 하지 않았다. 아래 가격 프리미엄과 예상가·검증 결과는 모두 **[추정: 시장 회귀]**이며 실거래나 인과 효과가 아니다.",
          "## +11 표시 시세의 신뢰 범위",
          "**실거래 체결가는 확인하지 못했다.** 카드별 강화 1~13 표시 시세와 9일의 `price_history`는 있지만, 거래량·체결 일시·체결가는 없다. 기존 일별 저장 단계가 1·5·8·10이라 **+11 일별 관측은 0일**이다. 새 빌드부터 +11도 저장하며, 과거 +11 일별 값을 만들어 채우지 않는다.",
          "원시 카드 수·하한 비율(사용자가 제시한 모집단, +1 OVR 115+):"]
    text.append(table(["포지션","카드","+8 하한","+10 하한","+11 하한"],
                      [[NAMES[r["group"]],r["cards"],*[pct(r["grades"][str(g)]["floor_share"]) for g in [8,10,11]]] for r in liquidity]))
    text.append("모든 강화에서 같은 값만 표시하는 카드는 기존 규칙대로 학습에서 제외한다([추정: 거래 제한 등], 개별 원인 미확인). 원시 표의 하한 비율에는 이 카드들도 포함된다. 다음 +11/+10 비율은 그 고정 표시 카드를 뺀 분포이며 마지막 열의 변동 비율은 원시 카드 전체 기준이다. 비율·변동도 체결 증거는 아니다.")
    text.append(table(["포지션","비고정 카드","+11/+10 5·50·95분위","원시 카드 +11 변화 비율"],
          [[NAMES[r["group"]],r["grades"]["11"]["nonflat_price_proxy_cards"],
            " / ".join(f"{r['grades']['11']['ratio_to_lower_nonflat'][p]:.2f}배" for p in ["p05","p50","p95"]),
            pct(r["grades"]["11"]["latest_two_snapshots"]["share_changed"])] for r in liquidity]))
    text.append("스냅샷 10→11은 **10/25 03:26→23:36 KST**의 두 관측이다. 스트라이커 +11 가격의 75%, 윙어 74%가 달라졌지만 +10 가격이나 하한을 따라 움직였을 수도 있다. +11 표시 시세를 하한·호가와 분리해 실거래로 믿을 근거는 부족하다. +10의 하한 비율이 더 낮다고 거래량까지 더 많다고 단정하지 않는다.")
    text += ["## 앱 모델 · 같은 카드의 강화별 5묶음 검증",
             "학습은 모두 +1 OVR 115+, 같은 카드 해시로 5묶음. 호날두·호나우두·굴리트를 학습에서 빼는 기존 규칙도 유지한다. 각 묶음에서 열 선택·선수 효과·토빗 잠재값을 훈련 카드만으로 다시 구한다. 예상가는 강화 하한 이상, 오차는 `median(abs(예상/표시시세−1))`. 하한 카드에서 값이 잘 맞는다고 실제 체감이나 수요를 맞힌 것은 아니다. 화면의 검증 문구는 기존처럼 5묶음 중 첫 1묶음이며, 아래는 **5묶음을 합친 값**이다."]
    text.append(table(["포지션","MV-09 +8","MV-09 +10","MV-09 +11","MV-10 +8","MV-10 +10","MV-10 +11"],
         [[NAMES[g],*[pct(old[g,k,115]["All"]["MedianError"]) for k in [8,10,11]],
           *[pct(model[g,k,115]["All"]["MedianError"]) for k in [8,10,11]]] for g in GROUPS]))
    text.append("+11의 전체 오차는 +8보다 대부분 크다. 기본 강화 전환으로 오차가 자동 개선됐다고 해석하지 않는다. 같은 상위 검색 카드(+1 OVR 119+)에서는 양상이 다르다:")
    text.append(table(["포지션","+8 상위 검색 오차","+10 상위 검색 오차","+11 상위 검색 오차"],
           [[NAMES[g],*[pct(model[g,k,115]["Playable"]["MedianError"]) for k in [8,10,11]]] for g in GROUPS]))
    text += ["## 특성 모양 선택",
             "앱의 동일한 세부 스탯·신체·소속 팀컬러·선수 효과를 유지하고 특성의 상수 프리미엄만 / 직선 / 꺾임 115·118·120·122를 비교했다. 선형은 +1 OVR 118 중심, 없음(constant)은 특성을 빼는 것이 아니라 OVR 상호작용만 없는 모형이다. 아래는 +11, +1 OVR 115+ 학습, 각 셀 `전체 중앙오차 / 최상위 특성 있음 log(시세/예상) / 없음 log(시세/예상)`이다. 최상위는 +1 OVR 120+, 하한 제외."]
    text.append(table(["포지션", "없음", "직선", "꺾임115", "꺾임118", "꺾임120", "꺾임122"],
            [[NAMES[r["Group"]],*[f"{pct(v['All']['MedianError'])} / {residual(v['TopWithTrait'])} / {residual(v['TopWithoutTrait'])}"
               for v in r["Shapes"].values()]] for r in shape_models if r["Grade"]==11 and r["MinOvr1"]==115]))
    text.append("**`TraitHinge=115` 선택.** 공격수·윙어의 특성 없는 최상위 카드 과대평가를 줄이며 전체 오차 변화는 작다. 모든 포지션·지표에서 최선인 모형은 아니다. 115+ 표본에서는 꺾임115가 관측 구간상 직선이고 릿지의 기준점도 달라진다. 115가 실제 경제적 문턱이라고 확정하지 않는다. `TraitTopOvr=122` 유지: 프리미엄을 보여 주는 기준이며 +11 OVR 143이다. OVR 120+만 학습하면 비교 선수·비특성 카드가 급감하므로 기본으로 쓰지 않는다. 전체 컷별 숫자는 동봉 JSON에 있다. 60장 근처 표본은 4묶음 훈련이 50장 미만이면 검증 묶음이 빠질 수 있어 완전한 5묶음 결과로 보지 않는다(GK 120+는 학습 자체 불가).")
    text += ["## 가짜 특성 · 판정 기준",
             "각 시즌 안에서 카드의 **특성 묶음 전체**를 섞어 시즌별 빈도·특성 동시 보유를 보존했다. 앱 모형과 같은 선수 군집 CR1, 토빗·릿지를 다시 학습해 +1 OVR 122에서의 결합 계수 t를 구했다. 각 위치·컷마다 60회, 고정 시드. 아래는 +11의 통합 분포다. 특성별 값도 JSON에 저장했다."]
    text.append(table(["포지션","+1 OVR 컷","|t| 95분위","거짓 양성 |t|>1.96","거짓 양성 |t|>3"],
            [[NAMES[r["Group"]],r["MinOvr1"],f"{r['Placebo']['pooled']['AbsT95']:.2f}",pct(r["Placebo"]["pooled"]["FalsePositive196"]),pct(r["Placebo"]["pooled"]["FalsePositive3"])] for r in calibration["Models"]]))
    text.append("**`TraitCritical=3.0` 유지.** 새 꺾임 115에서는 통합 95분위 최대 2.63, |t|>3 오탐 최대 3.7%다. 기존 꺾임 118에서는 넓은 표본의 최대 3.11이었다. 일부 희귀 특성의 개별 95분위는 3을 넘으므로 모든 특성에서 5% 오탐을 보장하지 않는다. 동시 검정 보정·완전한 검열 우도 신뢰구간이 아니며 작은 컷의 낮은 t값을 강한 근거로 해석하지 않는다.")
    text += ["## 주요 특성 프리미엄 [추정]",
             "앱 기본 모형(+11, +1 OVR 115+ 학습), **+1 OVR 122 / +11 OVR 143 카드 기준**. 같은 선수의 다시즌 카드를 비교하고 시즌·OVR·급여·약발·세부 능력치 등을 통제한다. 확실은 앱의 t=3 구간·선수 10명·같은 선수 비교 8명 기준으로, 실거래·인과 확정이 아니다."]
    rows=[]
    for g,traits in FOCUS.items():
        for t in traits:
            f=next(f for f in model[g,11,115]["Factors"] if f["Name"]==t)
            strict=next(z for z in fe["fits"] if z["group"]==g and z["grade"]==11 and z["cut_ovr1"]==115)
            strict_factor=next((z for z in strict["traits"] if z["trait"]==t),None)
            robust=f["Clear"] and strict_factor is not None and strict_factor["clear"] and f["Percent"]*strict_factor["percent"]>0
            rows.append([NAMES[g],t,f"{f['Percent']:+.0f}%",f"{f['Low']:+.0f} ~ {f['High']:+.0f}%",f["Comparisons"],"확실" if f["Clear"] else "불확실","확실" if robust else "불확실"])
    text.append(table(["포지션","특성","앱 프리미엄","근사 구간","비교 선수","앱 판정","종합 판정"],rows))
    text.append("중앙 공격수 크로스 포쳐는 앱 t 약 3.11로 판정 경계에 있고, 엄격한 선수 고정효과 모형에서는 +52%·불확실이라 **종합 판정도 불확실**이다. 라인 브레이커도 큰 양의 점추정이지만 불확실하다. 실제 검색 대상 OVR이 143보다 낮으면 표의 프리미엄을 그대로 적용하지 않고 해당 카드의 꺾임 값을 쓴다.")
    text += ["## 별도의 같은 선수·시즌 고정효과 분석 [추정]",
             "`mv10.py`는 세부 능력치·팀컬러를 추가하지 않은 검증용 명세로 `선수 + 시즌 + OVR + OVR² + 급여 + 약발 + 특성 + 꺾임115`, 하한 토빗을 쓴다. **선수 고정효과는 단일 카드 선수까지 모두 무축소**로 흡수하고 기준 선수 효과를 뺀 뒤 평균으로 중심을 맞춘다. 단일 카드 선수는 특성 계수를 식별하는 데 기여하지 않는다. 앱은 기존처럼 여러 카드 선수만 릿지 .05로 흡수하므로 두 계수가 같아야 하는 수치가 아니다. 모든 포지션, +8·+10·+11, 컷 110·115·120의 특성 계수·구간·가짜 특성 60회·6가지 모양의 5묶음 결과를 JSON에 저장했다. 특성 릿지 2, 꺾임 5를 유지하며 시즌 등 통제 열은 수치 안정용 .001만 둔다."]
    rows=[]
    for r in fe["fits"]:
        if r["group"] not in FOCUS or r["grade"]==8:
            continue
        for t in FOCUS[r["group"]]:
            f=next((f for f in r["traits"] if f["trait"]==t),None)
            if f:
                rows.append([NAMES[r["group"]],f"+{r['grade']}",r["cut_ovr1"],t,f"{f['percent']:+.0f}%",f["comparisons"],"확실" if f["clear"] else "불확실"])
    text.append(table(["포지션","강화","+1 컷","특성","프리미엄","비교 선수","판정"],rows))
    text += ["## 기본값과 학습 여유",
             "가성비 카드·가격 요인·신특 평균·숨은 랭커픽·스쿼드 구매의 기본 강화는 **+11**. 기본 필터 **OVR 140**(+1 119), +10 선택 시 **138**로 바뀌며 수동 입력은 보존한다. GK도 140을 쓰고, 스쿼드는 예산·급여가 부족하거나 랭커가 실제 사용하는 저 OVR 카드에는 기존 완화 로직을 유지한다. 숨은 랭커픽은 선택한 강화로 랭커가 실제 사용한 카드만 비교한다.",
             "`PlayableMargin=4`: 기본 검색 140에서 학습은 136(+1 115)부터다. 기존 꺾임 118에서 동일한 +1 119+ 검증 대상의 평균 위치별 중앙오차로 여유 0·3·4·5·9를 비교했다(작은 표본만 남는 0은 불안정). 4는 5보다 8개 필드 포지션 평균에서 약 1%p 작았고 윙어·공미·수미·풀백에 유리했다. 포지션마다 최적값은 다르며 선택에 쓴 검증 묶음을 최종 평가에도 써 과적합 가능성이 있다."]
    text.append(table(["포지션","여유9 (+1 110)","여유5 (+1 114)","여유4 (+1 115)","여유3 (+1 116)","여유0 (+1 119)"],
          [[NAMES[g],*[pct(old[g,11,cut]["Playable"]["MedianError"]) if (g,11,cut) in old else "표본 부족" for cut in [110,114,115,116,119]]] for g in GROUPS if g!="GK"]))
    text += ["## 검증과 남은 일",
             "- Release CLI 빌드, Debug 앱 빌드, .NET 전체 **297개 통과·Skip 0**, Python 합성 시장 테스트 **3개 통과**. +11 하한의 반올림 여유, +11 가성비·스쿼드 기본값, 같은 OVR 모집단, 하한 제외, 5묶음 자체 가격 누수, 요인·신특 평균의 동일 모델, 숨은 랭커픽의 실제 +11 사용·+10 선택, +11 이력 저장을 확인했다. 단일 카드의 고가 선수가 같은 선수 고정효과의 특성 값을 바꾸지 않는 것도 확인했다.",
             "- 기본 상위 카드 모델의 캐시가 전체 카드 모델을 먼저 부르지 않으면 매번 지워지던 문제를 고쳤다. 시세 갱신·팀컬러 변경 때 함께 무효화한다.",
             "- Debug 앱 시작·스쿼드 화면을 접근성 정보로 확인했다. **창 캡처가 `FrameArrived timed out` / `window capture timed out`, 요소 클릭이 `coordinate input geometry is unavailable`로 실패했다. 가성비 카드(ST·W)·가격 요인·신특 평균의 화면 배치·목록은 확인하지 못했다.** 기존 `app-latest/FcHelper.exe --tray` 실행을 복원했다.",
             "- +11 실거래 체결·거래량, 과거 일별 +11 시세는 미확인이다. 새 빌드에서 +11 이력을 축적하고 EVO 교환 후 시장을 다시 봐야 한다.",
             "- 모형 선택에 쓰지 않은 새 날짜/스냅샷으로 재검증이 필요하다. 태그·릴리스·app-latest 교체는 하지 않았다.",
             "## 재현",
             "```powershell\n# Python 3.10+, numpy. 원본은 읽기 전용, 복사본은 artifacts 아래에 만든다.\npython tools/market/mv10.py --phase liquidity --out artifacts/mv10/liquidity.json --prepare-db artifacts/mv10/market.db\npython tools/market/mv10.py --db artifacts/mv10/market.db --out artifacts/mv10/fe-final.json --draws 60\n./tools/market/mv10_cli.ps1 -DatabaseCopy artifacts/mv10/market.db\npython tools/market/mv10_report.py --input artifacts/mv10 --out docs/market-value/MV-10.md --json docs/market-value/data/mv10-summary-20261026.json.gz\npython -m unittest discover -s tools/market -p test_mv10.py\n```",
             "이미 사본이 있으면 `--prepare-db`는 덮어쓰지 않고 중단한다. 새 폴더를 지정하거나 그 옵션을 빼고 원본을 다시 읽는다. `market-validate`는 로컬 시장 캐시만 읽고 네트워크를 쓰지 않지만 `MarketStore` 초기화가 있으므로 원본 대신 사본을 넘긴다. 캐시된 상위 소속 팀컬러 20개를 고정하며 특성 팀컬러는 앱과 같이 학습에서 제외된다. `factors --minovr`는 검색 하한이 아니라 **학습** OVR이며 기본 +11 136이다. 요청한 `factors --pos ST --grade 11 --minovr 130`도 별도로 실행했다. `value --pos ST --grade 11 --top 3000`은 기본 OVR 140, 학습 136이다."]
    args.out.parent.mkdir(parents=True,exist_ok=True)
    args.out.write_text("\n\n".join(text)+"\n",encoding="utf-8")


if __name__=="__main__":
    main()
