# UI 구현 프롬프트 — FC Online Helper 스쿼드·시세 화면

> **현황 (2026-09-25)**: 이 사양대로 1차 UI가 구현됐다 (`src/FcHelper.App/Studio/`, 트레이 → "스쿼드·시세").
> 이미지는 코드 수정 없이 PNG로 교체한다: `docs/SKIN_ASSETS.md`. 이 문서는 화면을 다시 설계하거나 다른 AI가 손볼 때의 사양으로 남긴다.
>
> 이 문서를 그대로 UI 담당 AI에게 주면 된다. 엔진(계산·데이터)은 이미 구현돼 있고, **UI만** 만든다.

---

## 0. 너의 역할과 범위

너는 WPF(.NET 10) 데스크톱 UI 개발자다. 저장소 `fconline-helper`의 `src/FcHelper.App` 프로젝트에 **스쿼드 짜기와 시세 분석 화면**을 만든다.

- 계산, 데이터 수집, 캐시는 전부 `FcHelper.Market` 프로젝트의 `SquadService`에 있다. **UI에서 계산 로직을 새로 만들지 마라.** 필요한 값이 없으면 UI에서 추정하지 말고 "엔진에 필요한 것"으로 따로 적어라.
- 기존 파일 중 `App.xaml`(색·스타일), `App.xaml.cs`(트레이 메뉴), `ValueWindow.*`(가성비 찾기)만 필요한 만큼 고친다. 다른 프로젝트는 건드리지 않는다.
- 목표 수준: **FC온라인 공식 스쿼드메이커와 커뮤니티 사이트보다 빠르고, 한눈에 읽히고, 고급스러운** 화면. 정보가 많아도 3초 안에 핵심이 보여야 한다.

## 1. 반드시 지킬 원칙

1. **게임을 방해하지 않는다.** 창은 게임 창 포커스를 빼앗지 않는다(자동으로 `Activate()` 하지 않기, 사용자가 연 창만 활성화). 항상 위(Topmost)는 기본 끔.
2. **가볍게.** 폴링·타이머 애니메이션 금지. 창은 닫으면 해제(숨기지 말고 `Close`). 무거운 호출은 `await Task.Run`/`async`로, UI 스레드를 막지 않는다. `DataGrid`/`ListBox`는 가상화(`VirtualizingPanel.IsVirtualizing=True`, `VirtualizationMode=Recycling`).
3. **추정을 사실처럼 보이지 않게.** 엔진 값에는 근거 등급이 있다. `예상가`, `환산 OVR`, `추정 포메이션`, `상대 맞춤`은 모두 **[추정]** 배지를 붙인다. 랭커 사용률은 "전날 공식경기 기준", 시세는 "FC온라인 데이터센터 기준 · 갱신 시각"을 표시한다.
4. **출처 표기**: 화면 하단 한 줄 — `Data based on NEXON Open API · 시세/랭커 데이터: FC온라인 데이터센터`.
5. **한국어 UI.** 숫자는 게임과 같은 BP 표기(`Bp.Format(long)` 사용: `7.5억`, `3,000만`). 입력은 `Bp.TryParse`로 `1억`, `2억 5000만`, `1.5조`를 받는다.
6. **네트워크를 쓰는 호출은 사용자에게 보인다.** 로딩 상태, 취소 가능(`CancellationToken`), 실패 시 이전 데이터 유지 + 짧은 안내.

## 2. 디자인 방향 (고급스럽게)

- 기존 다크 테마 토큰(`App.xaml`): `Bg #15181D`, `Panel #1E2229`, `Line #2C323B`, `Text #E8EAED`, `Muted #9AA3AE`, `Accent #3DDC97`, `Warn #FFB547`, `Danger #FF6B6B`, `Info #6CB6FF`. 새 색은 이 토큰에서 파생만 한다(투명도/명도).
- 글꼴: `Malgun Gothic, Segoe UI`. 숫자 열은 `Segoe UI` + `Typography.NumeralAlignment=Tabular` 로 자릿수 정렬.
- 구성: 왼쪽 **내비게이션 레일**(아이콘+짧은 라벨), 가운데 **작업 영역**, 오른쪽 **상세 패널**(선택한 카드). 카드형 섹션, 8px 그리드, 모서리 8px, 그림자 대신 1px `Line` 테두리.
- 강조 규칙: 좋은 값 `Accent`, 주의 `Warn`, 나쁜 값 `Danger`, 정보 `Info`. 한 화면에 강조색은 2가지 이하.
- 차트는 외부 라이브러리 없이 `Polyline`/`Rectangle`로 그린다(가격 추이 스파크라인, 강화 단계별 가격 막대).
- 모든 목록 행에 선수 **시즌 배지**(시즌 코드 텍스트 칩), **강화 배지**(`+8`), 포지션 칩을 쓴다. 선수 이미지는 선택 사항: `https://fco.dn.nexoncdn.co.kr/live/externalAssets/common/playersAction/p{spid}.png` (실패 시 이니셜 원형). 이미지는 `DecodePixelWidth=64`로 작게, 디스크 캐시는 하지 않아도 된다.

## 3. 엔진 사용법

```csharp
var squads = ((App)Application.Current).Squads;   // FcHelper.Market.SquadService (null이면 준비 중)
var market = squads.Market;                        // MarketService: 상태·갱신
```

| 메서드 | 네트워크 | 속도 | 반환 |
|---|---|---|---|
| `market.Status` / `market.NextDue` / `market.Changed` 이벤트 | 없음 | 즉시 | 시세 데이터 기준 시각, 카드 수, 갱신 진행률 |
| `market.RefreshIfDueAsync(force: true)` | 데이터센터 | 10~20분 | 수동 갱신 (진행률은 `Changed`) |
| `squads.BuildAsync(SquadRequest)` | 첫 호출 시 랭커 차트(약 1분) | 1~3초 | `IReadOnlyList<SquadPlan>` (3안) |
| `squads.CompareModesAsync(SquadRequest)` | 동일 | 3~8초 | 모드별 최선 1안씩 (최강·균형·가성비·랭커픽) |
| `squads.HiddenRankerPicksAsync(position?, min, max, minUsers)` | 랭커 차트 | 즉시~1분 | `HiddenPick` 목록 |
| `squads.Grade(spId, position, from?, to?)` | 없음 | 즉시 | `GradeAdvice` |
| `squads.SalaryEfficiency(position, grade, min, max, minOvr)` | 없음 | 즉시 | `SalaryValue` 목록 |
| `squads.PriceMoves(grade, days, minPrice)` / `squads.Alerts()` | 없음 | 즉시 | `PriceMove` 목록 (과거 시세가 쌓여야 보임) |
| `squads.CurrentSquad(owned)` / `squads.UpgradesAsync(owned, budget, grades, fee, 2, teamColorId)` | 팀컬러 지정 시 첫 호출 | 1~2초 | 내 스쿼드 / `UpgradePlan` |
| `squads.DetectTeamColorsAsync(owned)` | 카드 4장 상세 + 팀컬러 멤버 | 첫 호출 10~60초 | 내 스쿼드의 팀컬러와 단계 |
| `squads.Tailored(needs, grade, maxPrice)` | 없음 | 즉시 | `TailoredPick` (상대 맞춤) |
| `squads.FormationAdviceAsync(opponentFormation)` | 랭커 차트 | 즉시 | `FormationAdvice` |
| `squads.TeamColorsAsync()` / `PopularTeamColorsAsync()` / `TeamColorAsync(id)` | 첫 호출 시 | 수 초~30초 | 팀컬러 목록·인기·상세(단계+멤버) |
| `squads.RankerStatsAsync([(spId, position)])` | **Open API 한도 사용** | 1~2초 | 랭커 20경기 평균(골·도움·슈팅…) — 화면에 보이는 카드에만 |
| `squads.Pool()` / `squads.Card(spId)` | 없음 | 즉시 | 전체 카드 (검색 자동완성용) |

내 스쿼드와 상대 정보는 `FcHelper.Services.SquadContext`:
- `SquadContext.MyCurrentCards(db, myOuid)` → `OwnedCard` 목록 (내 최근 공식경기 선발 11명·강화)
- `SquadContext.NeedsAgainst(report.Analysis)` → `TacticalNeed` 목록 (상대 약점/위협)
- `SquadContext.OpponentFormation(db, ouid)` → 추정 포메이션 문자열
(`db`는 `App`의 `FcDatabase`, `myOuid`는 설정의 내 닉네임을 `db.FindUserByNickname`으로 찾은 값. 필요하면 `App`에 읽기 전용 속성을 추가해 넘겨라.)

오류: 조건을 만족하는 스쿼드가 없으면 `InvalidOperationException`(한국어 메시지)을 던진다 → 그대로 인라인 안내로 보여준다.

### 주요 타입 (읽기 전용 record)

- `SquadRequest { Formation, Budget, SalaryCap, Grades(허용 강화 목록), Mode, Locked(slotIndex→LockedCard), ExcludedPlayers, TeamColor, TeamColorMembers, RankerPicksOnly, Plans }`
- `Formation(Name, Slots[11])` — `Formations.All`(13종), `Formations.Custom(name, slots)`
- `SquadPlan { Label, Mode, Formation, Slots, TeamColorLevel, TeamColorMembers, TotalPrice, MarketValue, TotalPay, AverageOvr, AverageEffectiveOvr }`
- `SquadSlot { Index, Position, Card, Grade, Ovr, Premium, TeamColorBonus, Price, Expected, Pay, RankerUsers, RankerShare, Locked, Owned, EffectiveOvr, Discount }`
- `MarketCard { SpId, Name, Season, Pay, Ovr1, WeakFoot, Positions, Stats, Tags, Prices, RatingCount, OvrAt(pos, grade), PriceAt(grade) }` — 태그 표시는 `MarketGroups.TagLabel(tag)`, 능력치 이름은 `MarketGroups.StatNames`
- `HiddenPick { Card, Position, Grade, Ovr, Price, Expected, Users, Share, Discount }`
- `GradeAdvice { Card, Position, Steps[GradeStep{Grade, Ovr, Price, CostPerOvrFromPrevious, Alternative, AlternativeGrade, AlternativePrice, PremiumOverAlternative}], CompetitiveUpTo, FromGrade, ToGrade, UpgradeCost, Alternative… }`
- `SalaryValue { Card, Position, Grade, Ovr, EffectiveOvr, Pay, Price, OvrPerPay }`
- `PriceMove { Card, Grade, Before, Now, Since, Change, Discount }`
- `UpgradePlan { Moves[Upgrade{Out(SquadSlot), In, Grade, Ovr, EffectiveGain, BuyPrice, SaleValue, NetCost}], TotalGain, NetCost }`
- `TailoredPick { Need, Position, Card, Grade, Ovr, Fit, Price, Reason }`
- `FormationAdvice { Opponent, Best[FormationMatchup], Worst[…] }`, `FormationMatchup { Formation, Opponent, Wins, Draws, Losses, Games, WinRate }`
- `TeamColor { Id, Name, Kind, MaxMembers, Levels[TeamColorLevel{Level, Members, AllStats, Effects}], LevelFor(n) }`

## 4. 만들 화면

트레이 메뉴에 **"스쿼드·시세"** 항목 하나를 추가하고, 이 하나의 창(`SquadStudioWindow`)에 내비게이션으로 아래 화면을 둔다. 기존 "가성비 찾기"(`ValueWindow`)는 이 창의 한 탭으로 옮기거나 같은 디자인으로 맞춘다.

### 4.1 스쿼드 짜기 (메인)
- **입력 바**(상단 한 줄, 접을 수 있음): 포메이션(드롭다운 + "직접 지정"), 예산(BP 입력 + 빠른 칩: 10억/50억/100억/500억), 급여 한도(숫자, 기본 비움=무제한), 강화 단계(다중 선택 칩 1~13, 기본 +8; 2개 이상이면 "강화도 자동 선택" 표시), 팀컬러(검색 가능한 드롭다운, 인기 팀컬러 상단 + "내 스쿼드 팀컬러 감지" 버튼), 랭커 범위(1~10000, 기본 전체), "랭커픽만" 토글, **짜기** 버튼.
- **모드 비교 스트립**: `CompareModesAsync` 결과 4개 카드 가로 배치 — 모드명, 총액, 평균 OVR, 환산 OVR, 급여 합, 팀컬러 단계. 가장 싼 안과 가장 강한 안에 배지. 클릭하면 아래 피치에 표시. 기본 선택은 "균형".
- **피치 뷰**: 초록 계열이 아닌 어두운 피치(Panel 톤 + Line 라인). 포메이션 슬롯 위치에 선수 칩(이름, 시즌 배지, `+강화`, OVR 큰 숫자, 팀컬러 보너스 있으면 `*`). 슬롯 좌표는 포지션 줄로 배치: GK y=92%, CB/LB/RB/LWB/RWB y=74%, CDM y=58%, CM y=47%, CAM/LM/RM y=34%, CF y=22%, ST/LW/RW y=11%; 같은 줄은 좌→우(L* 왼쪽, R* 오른쪽, 나머지 가운데)로 균등 분배.
- **슬롯 상호작용**: 칩 클릭 → 오른쪽 상세 패널(카드 정보, 강화 단계별 가격 미니 차트, 랭커 사용률, 랭커 20경기 스탯(`RankerStatsAsync`), "이 슬롯 고정"(Locked), "이 선수 제외"(ExcludedPlayers), "보유 중"(Owned=가격 0) 토글). 고정/제외 후 **다시 짜기**.
- **합계 바**(하단 고정): 총액 / 예산 게이지, 급여 합 / 한도 게이지, 평균 OVR, 환산 OVR [추정], 팀컬러 단계, "대안 2·3 보기".
- 비어 있는 상태: 시세 데이터가 없으면 "처음 데이터를 받는 중 (약 20분)" + 진행률(`market.Status.Running`).

### 4.2 숨은 랭커픽
- 필터: 포지션 칩, 가격대, 최소 사용 랭커 수(기본 10), 랭커 범위.
- 목록: 선수, 포지션, 랭커가 쓰는 강화, OVR, **사용률 막대**, 시세, 예상가, **할인율**(Accent), 한 줄 해석("랭커 31명이 쓰는데 같은 스펙보다 17% 쌈").
- 행 클릭 → 상세 패널 + "스쿼드에 넣기"(해당 포지션 슬롯에 Locked로).

### 4.3 가성비 찾기 (기존 ValueWindow 기능)
- 포지션 그룹·강화·가격대·약발·특성·개인기 필터 → 예상가보다 싼 순. (`market.FindValue`)

### 4.4 강화 효율
- 선수 검색(`Pool()` 이름 자동완성) + 포지션 + 현재/목표 강화.
- **단계별 표 + 막대**: +1~+13 가격(로그 스케일 막대), OVR, OVR 1당 비용, "같은 OVR 최저 대안"과 프리미엄 %.
- 결론 카드: `CompetitiveUpTo`가 있으면 "+N까지는 대안과 비슷한 값", 없으면 "OVR 외의 가치(특성·팀컬러·체감)에 값을 치르는 카드". `from→to` 지정 시 "강화 비용 X vs 대안 카드 Y".

### 4.5 급여 효율
- 포지션·강화·가격대·최소 OVR → 급여 1당 환산 OVR 순. 급여 한도가 병목일 때 쓰는 화면이라는 안내 문구.

### 4.6 시세 추이·알림
- 강화·기간(1/7/30일)·최소 가격 → 급락/급등 목록, 행마다 스파크라인(price history 일자별, 엔진에 일자별 조회가 필요하면 "엔진 요청"으로 적기).
- "급락 + 스펙 대비 싼" 알림 목록(`Alerts()`), 과거 데이터가 없으면 "하루 이상 자동 갱신이 쌓이면 보입니다".

### 4.7 내 스쿼드 업그레이드
- 내 닉네임(설정) → `MyCurrentCards` → 현재 11명 피치 뷰(보유=가격 0).
- 팀컬러 감지 결과 칩(`DetectTeamColorsAsync`: "대한민국 10명 3단계 +3"), 유지할 팀컬러 선택.
- 예산·허용 강화·판매 수수료(기본 0, 입력 가능) → `UpgradesAsync` 상위 5안: "하석주 → 이영표 TK +8 · 순비용 880만 · 환산 +2.4", 적용 미리보기(피치에서 바뀌는 칸 강조).

### 4.8 상대 맞춤 (상대 분석과 연결)
- 검색 창/상대 카드에서 "맞춤 추천" 버튼 → 상대의 `NeedsAgainst`, `OpponentFormation`.
- 추정 포메이션 [추정] + 랭커 전적상 유리/불리 포메이션(`FormationAdviceAsync`: 승·무·패와 승률, 30경기 이상만).
- 필요 유형별 카드 묶음(`Tailored`): 이유 문장 + 추천 카드 4장(예산 내).

### 4.9 팀컬러
- 인기 팀컬러(랭커 사용률), 검색, 상세(단계별 인원·효과, 멤버 수), "이 팀컬러로 스쿼드 짜기".

## 5. 상태·에러·성능

- 모든 비동기 버튼: 누르면 비활성 + 인라인 스피너, 완료 시 복구. 창을 닫으면 진행 중 작업 `Cancel`.
- 네트워크 실패: 이전 결과 유지 + 상단 얇은 배너(Warn): "데이터센터에 연결하지 못했습니다. 이전 데이터로 보여줍니다."
- `market.Changed`는 다른 스레드에서 온다 → `Dispatcher.BeginInvoke`.
- 결과 목록은 300행까지 표시, 그 이상은 "조건을 좁혀 주세요".
- 창 크기·마지막 탭·필터는 `%LOCALAPPDATA%\FcHelper\settings.json`(`AppSettings`)에 저장해도 된다(속성 추가).

## 6. 완료 기준

- [ ] 트레이 → "스쿼드·시세" 창에서 4.1~4.9가 모두 동작하고, 엔진 메서드 외의 계산이 없다.
- [ ] 4-2-2-2, 예산 100억, +8로 모드 비교 4안이 3초 안에 보인다(차트 캐시 후).
- [ ] 게임 창이 앞에 있을 때 이 창이 스스로 포커스를 가져가지 않는다.
- [ ] 모든 추정 값에 [추정] 배지, 출처 한 줄, 시세 기준 시각이 있다.
- [ ] 다크 테마 토큰만 사용, 1280×720 창에서도 잘리지 않고, 1920×1080에서 여백이 과하지 않다.
- [ ] `dotnet build`와 `dotnet test`가 통과한다(테스트는 엔진용, UI는 수동 확인).

## 7. 빌드·실행

```powershell
dotnet build
dotnet run --project src/FcHelper.App
```
엔진 동작은 CLI로도 확인할 수 있다(같은 계산): `fch squad --formation 4-2-2-2 --budget 100억 --grades 5,8`, `fch picks --pos ST`, `fch grade --name 앙리 --from 5 --to 8`, `fch salary --pos CB`, `fch movers`, `fch formation --vs 4-2-2-2`, `fch teamcolor --id 1016`, `fch upgrade --me <닉네임> --budget 10억`, `fch tailor <상대>`.
