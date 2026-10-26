# 시세 분석 · 가성비 찾기 (개인용)

> **앱에 들어갔다.** 트레이 → **가성비 찾기** 창에서 포지션·강화·가격대로 검색하고, 시세는 앱이 **자동으로 갱신**한다
> (하루 1번, 새 시즌은 공식 시즌 목록으로 감지해 바로, 게임 중에는 미룸). 명령줄은 `fch value --pos W --grade 11 --min 1억 --max 30억`.
> MV-10부터 기본 +11·OVR 140, +10은 같은 카드 범위인 OVR 138. 현재 앱 모형과 검증은 `fch`를 기준으로 본다. 예전 `model.py`·`value.py`는 초기 분석 실험용이며 현재 앱과 같은 모형이 아니다.

## MV-11 스쿼드 재현

`fch allocation --refresh yes`로 공식경기 랭커 표본을 명시적으로 갱신한다. 성공한 뒤 캐시를 교체하며 실패한 요청은 이전 표본의 날짜를 바꾸지 않는다. 키는 기존 환경 변수로만 전달한다.

`dotnet run --project tools/market/SquadAudit -- <원본DB> <새작업DB> 100 4-2-2-2 --allocation`은 원본을 읽기 전용으로 열고 새 사본에서 +11·급여 310의 네 안을 검증한다. `4-2-2-1-1`도 같은 방법으로 비교한다. 네트워크 요청을 차단하므로 출력의 랭커·시세·차트 날짜를 확인한다. 개인 랭커 JSON과 원본 DB는 커밋하지 않는다. 결과와 한계는 [`MV-11.md`](../../docs/market-value/MV-11.md)에 기록했다.

## MV-10 재현 (+8·+10·+11)

Python 3.10+와 numpy, .NET 10이 필요하다. 원본 SQLite는 읽기 전용으로 열고 시장 테이블만 사본으로 만든다. 시세·실거래 구분, 특성 모양·가짜 특성·전체 5묶음 결과는 [`MV-10.md`](../../docs/market-value/MV-10.md)에 기록했다.

```powershell
python tools/market/mv10.py --phase liquidity --out artifacts/mv10/liquidity.json --prepare-db artifacts/mv10/market.db
python tools/market/mv10.py --db artifacts/mv10/market.db --out artifacts/mv10/fe-final.json --draws 60
./tools/market/mv10_cli.ps1 -DatabaseCopy artifacts/mv10/market.db
python tools/market/mv10_report.py --input artifacts/mv10 --out docs/market-value/MV-10.md --json docs/market-value/data/mv10-summary-20261026.json.gz
python -m unittest discover -s tools/market -p test_mv10.py
```

`mv10.py`는 같은 선수·시즌 고정효과와 하한 토빗의 독립 분석, `mv10_cli.ps1`은 앱의 실제 세부 능력치·팀컬러·선수 효과 모형 비교다. 모든 컷은 +1 OVR, 모든 강화는 같은 카드 해시 5묶음이다. 네트워크·API 키를 쓰지 않는다. 사본이 이미 있으면 `--prepare-db`를 빼거나 새 경로를 지정한다. CLI는 `MarketStore`를 초기화하므로 원본 DB를 넘기지 않는다.

FC온라인 **공식 데이터센터**(fconline.nexon.com)의 선수 목록으로
1. 능력치·약발·특성·개인기·체형이 가격에 미치는 영향을 계산하고 (`analyze.py`)
2. 원하는 가격대에서 "같은 스펙 대비 싸게 거래되는" 선수를 찾는다 (`value.py`).

Open API에는 능력치와 시세가 없어서 데이터센터를 쓴다. **개인용**으로만 쓰고, 받은 데이터는 `data/`에만 두며
git에 올리지 않는다 (API 약관: 수집 데이터는 30일 안에 갱신).

## 준비: 데이터 받기

```powershell
cd tools/market
python harvest.py                 # 전 포지션 (약 20분, 2초에 1번 요청). 중간에 끊겨도 다시 실행하면 이어서 받는다
python harvest.py --groups ST W   # 일부 포지션만
```

다시 처음부터 받으려면(시세 갱신) `data/` 폴더를 지우고 실행한다. Python 3.10 이상, 표준 라이브러리만 쓴다.

포지션 그룹: `ST`(ST/LS/RS) `W`(LW/RW) `CF` `SM`(LM/RM) `CAM` `CM` `CDM` `CB` `FB`(LB/RB/LWB/RWB) `GK`

## 가격 영향 분석

```powershell
python analyze.py                    # 전 포지션, +5와 +8 가격
python analyze.py --pos W --grade 8
```

"같은 OVR·시즌·급여·다른 능력치에서 이것 하나만 다를 때 가격이 몇 % 다른가"를 보여준다.
★★ 통계적으로 확실, ★ 어느 정도 확실, - 우연일 수 있음.

## 가성비 찾기

```powershell
python value.py --pos W --grade 8 --min 1억 --max 30억
python value.py --pos ST CF --grade 5 --max 5000만 --wf 5 --trait "라인 브레이커"
python value.py --pos CB --grade 8 --min 5억 --max 20억 --skill 5
```

| 옵션 | 뜻 |
|---|---|
| `--pos` | 포지션 그룹 (여러 개 가능) |
| `--grade` | 강화 단계. 표시되는 OVR과 시세는 이 단계 기준 |
| `--min`, `--max` | 가격대. `1억`, `5000만`, `1.5조`, `2억 5000만` 형식 |
| `--wf` | 최소 약발 (5 = 양발만) |
| `--trait` | 반드시 가진 특성 (여러 번 가능) |
| `--skill` | 개인기 5 또는 6성만 |
| `--min-ratings` | 구단주 평가 수 하한. 거래가 거의 없는 카드를 거른다 (기본 10) |

**예상가**는 같은 OVR·약발·급여·능력치·특성·개인기·체형·시즌 카드들의 오늘 시세로 계산한 값이다 **[추정]**.
예상가보다 많이 싸면 "후보"이지 정답이 아니다. 체감, 팀컬러, 받지 않은 능력치는 모델이 모른다.

## 알아둘 점

- 강화 하한 배수는 +5 ≥ 1.5×+3, +8 ≥ 9.5×+5, +10 ≥ 2.1×+8, +11 ≥ 1.55×+10이다. 하한에 붙은 시세와 실거래 수요를 구분한다. 현재 앱은 하한 카드를 토빗으로 학습한다.
- 목록 검색은 한 번에 최대 200명이라 포지션 × OVR 하나씩 잘라서 받는다.
- 능력치는 포지션마다 핵심 8개만 받는다 (`harvest.py`의 `GROUPS`).
- 체형은 ST와 윙어만, 개인기는 5·6성만 표시한다.
