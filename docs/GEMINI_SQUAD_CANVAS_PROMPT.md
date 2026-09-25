# Gemini 작업 프롬프트: 스쿼드 메이커 캔버스 전체 화면 + 드래그 포메이션 편집

> 아래 `---` 사이 전체를 그대로 붙여넣으세요. 저장소 파일을 읽을 수 있는 환경(예: Gemini CLI / Code Assist, 저장소 루트에서 실행)을 전제로 합니다.

---

너는 C# .NET 10 WPF 앱 `fconline-helper`(FC 온라인 보조 도구)를 고치는 시니어 WPF 개발자다. 이번 작업은 **스쿼드 메이커 화면 하나**다. 기능 세 가지를 넣는다. 다른 기능은 건드리지 않는다.

## 0. 작업 전에 반드시 할 것

1. 아래 파일을 **전부 먼저 읽고** 지금 구조를 파악한 다음 계획을 짧게 말하고 시작해라. 추측으로 코드를 쓰지 마라.
   - `src/FcHelper.Market/Formations.cs`: `Formation(Name, Slots)`, `Formations.All`, `Normalize`, `Custom`, `GroupOf`
   - `src/FcHelper.Market/SquadMaker.cs`: `Slot()`, `WithGrade()`, `Remap()`, `Search()`, `Suggest()`, `ApplyTeamColorsAsync()`
   - `src/FcHelper.Market/SquadBuilder.cs`: `SquadSlot`, `SquadPlan`, `SquadRequest`. AI 빌더는 `r.Formation.Slots`(평가 포지션 이름)를 쓴다.
   - `src/FcHelper.Market/OvrFormula.cs`: `FinalOvrMath.Compute`. 카드 목록에 없는 포지션이면 `CardAbility.Positions`(16개 포지션 전부)로 대체한다.
   - `src/FcHelper.Market/Ability.cs`: `CardAbility`, `AbilityCache`. `SquadService.KnownAbility` / `AbilityAsync`
   - `src/FcHelper.App/Studio/PitchView.cs`: 피치 그리기
     - `Viewbox` 안에 700×800 `Canvas`가 있다.
     - `LineY(position)`로 줄 높이를 정한다.
     - `Side()`는 포지션 첫 글자 L/R로 좌우를 정한다.
     - 같은 줄은 균등 배치한다.
     - 카드 폭은 124다.
     - 이벤트는 `SlotClicked`, `EmptySlotClicked`다.
   - `src/FcHelper.App/Studio/SquadPage.xaml` / `.xaml.cs`: 스쿼드 메이커 페이지
     - `_working`(SquadSlot?[11]), `_positions`(string[11])
     - `FormationBox`, `Remember()`(되돌리기), 보관함(`SavedSquad`)
     - `ApplyFinals()`, `RefreshWorkingAsync()`, `ShowSlotAsync()`
     - 줌: `ZoomSlider`, `OnPitchWheel`, `ApplyZoom`, `OnPitchPress/Drag/Release`
   - `src/FcHelper.App/Studio/StudioWindow.xaml` / `.xaml.cs`: 왼쪽 메뉴(`Nav`) + 페이지 호스트(`Host`, Grid.Column 1)
   - `src/FcHelper.App/App.xaml`: 공용 스타일(`Ghost`, `Primary`, `Chip`, `CardPanel`, `Hint`, `FieldLabel`)과 브러시(`Bg`, `Panel`, `Raised`, `Line`, `Text`, `Muted`, `Accent`, `AccentSoft`, `Warn`). **새 색을 만들지 말고 이것을 써라.**
   - `tests/FcHelper.Tests/SquadTests.cs`, `OvrFormulaTests.cs`: 테스트 스타일 참고
2. 빌드: `dotnet build src/FcHelper.App -c Debug`
3. 테스트: `dotnet test tests/FcHelper.Tests`. 지금 139개가 통과한다. 끝났을 때도 전부 통과해야 한다.

## 1. 지켜야 할 규칙 (어기면 안 됨)

- 게임 메모리 읽기, DLL 인젝션, 패킷 스니핑, 게임 프로세스 접근, 입력 자동화는 절대 넣지 않는다.
- API 키(`FCH_API_KEY` 환경 변수)는 코드, 로그, 커밋에 절대 넣지 않는다. `*.local.json`은 커밋하지 않는다(다른 유저 닉네임이 들어 있음).
- 가볍게 만든다. 폴링과 타이머 루프를 쓰지 않는다.
  - 드래그 중에는 전체 다시 그리기(`Render`)를 하지 않는다. 떠다니는 카드 하나만 `RenderTransform`으로 움직인다.
  - 전체 다시 그리기는 놓을 때 한 번만 한다.
- UI 문구는 한국어, 코드 주석은 영어로 쓴다. 주변 코드의 이름, 주석 밀도, 스타일을 따라간다.
- 계산값과 추정값을 구분한다.
  - 카드 스탯을 아직 안 받아서 추정인 OVR은 지금처럼 `~` 표시를 붙인다(`FinalOvr.Exact`).
- WPF 주의: 한 요소는 부모를 하나만 가질 수 있다. 드래그용 카드는 **새로 만든 요소**로 쓰고, 피치 위 카드를 옮겨 붙이지 않는다.
- 파일 전체를 다시 쓰지 말고 필요한 부분만 고친다. 관련 없는 리팩터링은 하지 않는다.

## 2. 지금 문제 (사용자 불만 그대로)

1. **캔버스가 작다.** 지금 확대는 돋보기식이다(ScrollViewer 안에서 피치만 키우고 스크롤). 사용자가 원하는 건 **캔버스 자체가 프로그램 화면 전체를 차지하는 것**이다.
2. **포메이션 설정이 안 보인다.** `FormationBox`가 `Conditions` 패널 안에 있어서, AI 추천을 누르면 `ShowConditions(false)`로 조건이 접히면서 같이 사라진다.
3. **마우스로 포지션을 못 바꾼다.** 다른 스쿼드메이커(공식 FC 온라인 스쿼드메이커, fc-info, 인벤, 피온북)처럼 카드를 끌어서 자리를 옮기고, 포메이션 이름이 저절로 바뀌어야 한다.
   - 예: 4-4-2에서 미드필더 한 명을 공격 줄로 올리면 4-3-3이 된다.

## 3. 기능 A: 포메이션 선택을 항상 보이게

- `FormationBox`를 `Conditions`에서 빼서 피치 바로 위 도구 줄(`빈 스쿼드` 버튼 앞)로 옮긴다. 조건을 접어도 항상 보여야 한다.
- 옆에 현재 포메이션 이름 표시(`TextBlock x:Name="FormationName"`)를 둔다.
  - 프리셋과 같으면 그 이름(예: "4-3-3")을 보여 준다.
  - 드래그로 바꿔서 프리셋이 아니면 "사용자 지정 4-1-3-2"처럼 보여 준다.
- 프리셋을 고르면 지금처럼 `maker.Remap()`으로 선수를 새 포메이션에 옮긴다. 이 동작은 유지한다.
- AI 추천(`OnBuild`)과 빈 자리 AI(`OnFillEmpty`)는 **지금 피치의 배치**(드래그로 바꾼 사용자 지정 포함)로 짠다.
  - `RequestAsync`에서 `CurrentFormation` 대신 `Formations.Custom(이름, 현재 11자리)`를 넘긴다.
  - 빌더는 평가 포지션(Normalize된 이름)을 쓰므로 `Slots`에는 Normalize된 이름을 넣는다.

## 4. 기능 B: 드래그로 포지션 바꾸기 + 포메이션 자동 인식

### 4-1. 존(칸) 모델: `src/FcHelper.Market/Formations.cs`에 추가

피치를 **세로 7줄 × 가로 5칸** 격자로 본다. 공격 방향이 위다.

| 줄 (y 비율) | L (0.1) | LC (0.3) | C (0.5) | RC (0.7) | R (0.9) |
|---|---|---|---|---|---|
| 0 ST (0.08) | LW | LS | ST | RS | RW |
| 1 CF (0.215) | LW | LF | CF | RF | RW |
| 2 AM (0.345) | LM | LAM | CAM | RAM | RM |
| 3 CM (0.475) | LM | LCM | CM | RCM | RM |
| 4 DM (0.605) | LWB | LDM | CDM | RDM | RWB |
| 5 DEF (0.76) | LB | LCB | CB | RCB | RB |
| 6 GK (0.92) | — | — | GK | — | — |

- 새 타입: `public sealed record Spot(int Row, int Col)`. 존의 세부 포지션 이름은 `Formations.PositionAt(Spot)`로 얻는다.
  - 예: LCB. 평가 포지션은 기존 `Normalize`를 그대로 쓴다(LCB→CB, LS→ST …).
  - LW/RW, LM/RM, LWB/RWB, LB/RB는 Normalize해도 그대로다. 데이터센터가 따로 평가하는 포지션이다.
- **프리셋 레이아웃**: `Formations.All`의 13개 프리셋 각각에 11개 `Spot`을 정한다. 기존 `Slots` 순서와 개수는 유지해서 다른 코드를 깨지 않는다.
  - 예: 4-2-2-2 = GK(6,2), LB(5,0), LCB(5,1), RCB(5,3), RB(5,4), LDM(4,1), RDM(4,3), LAM(2,1), RAM(2,3), LS(0,1), RS(0,3)
  - 예: 4-3-3 = … LCM(3,1), CDM(4,2), RCM(3,3), LW(0,0), ST(0,2), RW(0,4)
  - 예: 5-2-1-2 = LWB(4,0), LCB(5,1), CB(5,2), RCB(5,3), RWB(4,4) …
  - 각 프리셋의 존에서 얻은 세부 포지션을 Normalize한 값이 기존 `Slots[i]`와 같아야 한다. 테스트로 확인한다.
- **포메이션 이름 인식**: `Formations.Detect(IReadOnlyList<Spot> spots) → string`
  - GK를 빼고 앞뒤로 묶는다.
    - 수비 = DEF 줄 전체 + LWB/RWB(DM 줄 양끝)
    - 그다음 DM 줄 가운데 3칸, CM 줄, AM 줄, CF 줄, ST 줄 순서
  - 비어 있지 않은 묶음의 인원을 뒤에서 앞 순서로 `-`로 잇는다.
  - 결과가 프리셋 이름과 같으면 그 프리셋으로 본다. 이미 있는 이름 규칙(4-2-2-1-1, 4-1-2-1-2 등)과 맞는지 **13개 프리셋 모두로 테스트**한다.
  - 여러 레이아웃이 같은 이름이 되는 건 괜찮다. 예: 4-2-3-1이 LM·CAM·RM이든 LAM·CAM·RAM이든 4-2-3-1이다.
  - 사용자 예시: 4-4-2(LM LCM RCM RM + LS RS)에서 LCM을 ST 줄 가운데로 올리면 수비 4 / CM 줄 3 / ST 줄 3 → "4-3-3". 테스트에 넣는다.
- `SquadPage`가 들고 있는 `_positions`(string[11])는 `Spot[] _spots`로 바꾸거나 같이 둔다. 슬롯 i의 포지션은 항상 `PositionAt(_spots[i])`에서 만든다.

### 4-2. 드래그 동작: `PitchView.cs`

- 카드 배치를 `LineY/Side` 방식에서 **존 좌표 방식**으로 바꾼다.
  - 슬롯 i는 `_spots[i]` 칸 중심에 놓인다. x = W × 칸 x 비율, y = H × 줄 y 비율.
  - PitchView의 `Show(...)`에 `IReadOnlyList<Spot>`을 넘기는 오버로드를 추가한다.
  - `MySquadPage`는 지금 `Show(slots)`를 쓴다. 존이 없는 호출은 지금 방식(LineY/Side)을 그대로 유지해서 내 스쿼드 화면이 깨지지 않게 한다.
- **드래그 시작**: 카드(또는 빈 자리 +)에서 왼쪽 버튼을 누르고 5px 이상 움직이면 시작한다.
  - 5px 미만에서 놓으면 지금처럼 클릭(`SlotClicked` / `EmptySlotClicked`)이다.
- **드래그 중**
  - 원래 자리 카드는 반투명(Opacity 0.35)으로 둔다.
  - 마우스를 따라다니는 복제 카드는 Opacity 0.9에 그림자를 준다.
  - 35칸 중 쓸 수 있는 칸 중심에 옅은 점과 포지션 이름(ST, LCM …)을 띄운다.
  - 가장 가까운 칸을 강조한다: 빈 칸은 `Accent`, 다른 선수가 있는 칸(교체)은 `Warn`, 놓을 수 없는 칸은 빨강 계열(기존 브러시 중 경고색) 테두리.
- **놓기 규칙**
  - GK 줄(6)에는 GK 슬롯만 들어간다. GK 슬롯은 GK 줄 밖으로 못 나간다. 어기면 원래 자리로 돌아가고, 상태 줄에 이유를 한국어로 쓴다.
  - 빈 칸에 놓으면 그 슬롯의 존만 바꾼다.
  - 다른 선수가 있는 칸에 놓으면 **두 슬롯의 존을 맞바꾼다.**
  - 카드 목록은 그대로다. 11자리, 각 자리의 선수는 유지된다.
  - 피치 밖에 놓으면 취소한다. Esc를 눌러도 취소한다.
- PitchView는 결과를 이벤트로 알린다: `event Action<int /*slot*/, Spot /*to*/>? SlotMoved`. 교체 판단과 재계산은 `SquadPage`가 한다.
- 드래그 중 마우스 캡처는 PitchView가 잡고 놓는다. 창 밖에서 버튼을 떼도 캡처가 풀리게 한다(`LostMouseCapture` 처리).

### 4-3. 놓은 뒤 재계산: `SquadPage.xaml.cs`

1. `Remember()`를 호출한다. 되돌리기 스택에 존 배치도 같이 저장해서 Ctrl+Z로 되돌릴 수 있어야 한다.
2. 옮긴 슬롯(교체면 두 슬롯)을 새 평가 포지션으로 다시 만든다. `maker.Slot(i, 새포지션, card, grade, owned, locked)`
   - **버그 주의:** 지금 `SquadMaker.Slot()`은 카드 목록에 그 포지션이 없으면 `card.OvrAt(grade)`(최고 포지션 OVR)로 대체한다. 포지션을 옮기면 틀린 값이 된다.
   - 고치는 방법: 카드 목록에 없는 포지션이면 `SquadService.KnownAbility(spId)?.Positions`의 그 포지션 +1 OVR로 계산한다(강화 보너스는 `Grades.Bonus[grade] - Grades.Bonus[1]`).
   - 스탯을 아직 모르면 우선 최고 포지션 값으로 두고 `LoadAbilitiesAsync()`가 받아 오면 다시 계산한다. 이때 `FinalOvr.Exact = false`, 카드에 `~`.
   - 카드가 원래 목록에 없는 포지션에서 뛰면 카드의 포지션 글자를 `Warn` 색으로 칠하고 툴팁에 "원래 포지션 아님"을 쓴다.
3. `Formations.Detect`로 이름을 다시 정한다.
   - 프리셋과 같으면 `FormationBox`에서 그 프리셋을 선택한다. 이때 `Remap`이 돌지 않게 `_restoring` 플래그로 막는다.
   - 아니면 `FormationName`에 "사용자 지정 …"을 쓴다.
4. `RefreshWorkingAsync()`로 팀컬러, 인게임 OVR(`ApplyFinals`), 합계를 다시 계산한다.
5. **보관함**: `SavedSquad`에 존 배치(11개 Row/Col)를 추가로 저장한다. 예전 저장본(존 없음)은 프리셋 이름으로 레이아웃을 만들어 불러온다. 하위 호환을 지킨다.
6. **이미지로 저장**(`OnSaveImage`)은 지금 배치 그대로 찍히면 된다.

## 5. 기능 C: 캔버스 크게 보기 (돋보기 확대 대신)

- 지금의 돋보기식 확대는 **없앤다.** 사용자가 원하지 않는 방식이다.
  - 없앨 것: `ZoomSlider`, −/+/100% 오버레이, 휠 확대(`OnPitchWheel`), 드래그 이동(`OnPitchPress/Drag/Release`), `ApplyZoom`의 확대 부분
  - ScrollViewer도 필요 없으면 없애서, 피치가 늘 주어진 칸을 꽉 채우게 한다(`Viewbox` Uniform).
  - 드래그 이동 코드와 새 카드 드래그가 충돌하지 않게 완전히 지운다.
- 도구 줄에 **"⛶ 크게 보기"** 버튼을 둔다. 단축키 F11도 쓴다.
- 크게 보기 모드에서는:
  - `StudioWindow`의 왼쪽 메뉴 열(`Nav`가 든 Column 0)을 숨긴다.
    - `StudioWindow`에 `public void SetFocusMode(bool on)`를 만든다. 열 폭을 0으로 하고 `Host` 여백도 줄인다.
    - 창은 최대화한다. 원래 상태(WindowState, 크기)를 기억했다가 나갈 때 되돌린다.
  - `SquadPage`에서 위쪽 요청 패널(`스쿼드 메이커` 제목, 조건, AI 추천 버튼이 든 Border), 도구 카드, 상태 줄, 오른쪽 칸(AI 안 4개, 보관함, 상세), GridSplitter를 숨긴다.
    - **피치가 창 전체 높이를 차지한다.** 피치는 세로 3:4라 가로는 남는다.
  - 남는 좌우 공간에 **반투명 떠 있는 패널**(`Panel` 브러시, Opacity 0.95, CornerRadius 12)을 둔다.
    - 왼쪽 위: 포메이션 선택(`FormationBox`), 현재 포메이션 이름, 합계 한 줄(시세 / 급여 / 인게임 평균 OVR), 적용 팀컬러, "✕ 나가기 (Esc)" 버튼
    - 오른쪽: 카드를 누르면 **선수 상세 서랍**이 오른쪽에서 열린다(폭 400).
      - 지금의 `Detail` StackPanel을 옮겨 쓰거나 같은 내용을 보여 준다.
      - 새로 만들지 말고 기존 `ShowSlotAsync`가 채우는 `Detail`을 그대로 쓴다. 부모를 옮길 때는 먼저 원래 부모에서 떼어 낸다.
      - 서랍 바깥을 누르거나 Esc를 누르면 닫힌다.
    - 창 폭이 좁아서 떠 있는 패널이 피치를 가리면 패널을 접을 수 있게(아이콘만) 한다.
  - 카드 글자와 미니페이스는 피치와 함께 커진다. `Viewbox`라 자동이다. 크게 보기에서 카드 이름 13px 기준 글자가 화면에서 **최소 14px 이상**으로 보여야 한다.
  - Esc, F11, "나가기"로 원래 화면으로 돌아온다. 다른 메뉴로 이동할 때(`StudioWindow.Navigate`)도 자동으로 나간다.
- 크게 보기에서도 드래그 포지션 편집, 카드 클릭, 빈 자리 +가 똑같이 된다.

## 6. 테스트 (xUnit, `tests/FcHelper.Tests/FormationLayoutTests.cs` 새로 만들기)

1. 13개 프리셋 모두에 대해 다음을 확인한다.
   - 레이아웃 존 11개가 서로 다르다.
   - GK는 하나이고 (6,2)에 있다.
   - `Normalize(PositionAt(spot))`가 기존 `Slots[i]`와 같다.
   - `Detect(레이아웃)`이 프리셋 이름이다.
2. 4-4-2에서 LCM을 (0,2)로 옮기면 `Detect`가 "4-3-3"이다.
3. 4-4-2에서 ST 하나를 (2,2)로 내리면 "4-4-1-1"이다.
4. 5-2-1-2에서 LWB·RWB를 DEF 줄 양끝으로 옮겨도 수비 5명이라 "5-2-1-2"다.
5. 교체 규칙을 PitchView와 떼어 내서 순수 함수로 만든다. 예: `Formations.Move(spots, slot, to)` → 새 배치 또는 거절 사유.
   - 빈 칸으로 옮기기, 선수 있는 칸과 맞바꾸기, GK 규칙 위반 거절을 테스트한다.
6. `SquadMaker.Slot`에 카드 목록에 없는 포지션을 넣었을 때 `CardAbility` 값으로 OVR이 나오는지 테스트한다. 스탯이 없으면 대체값을 쓴다.
7. 기존 테스트 139개가 모두 통과해야 한다.

## 7. 직접 확인할 것 (완료 조건)

- 4-4-2를 고르고 오른쪽 중앙 미드필더를 ST 줄로 끌어 놓았을 때:
  - 이름이 4-3-3으로 바뀐다.
  - 그 선수의 포지션이 ST로, OVR이 ST 기준으로 바뀐다.
  - 합계와 팀컬러가 다시 계산된다.
  - Ctrl+Z로 되돌아간다.
- 두 선수를 맞바꿔도 둘 다 새 포지션 OVR로 바뀐다.
- GK를 공격 줄로 끌면 거절되고 이유가 나온다.
- 크게 보기에서:
  - 왼쪽 메뉴, 오른쪽 칸, 위쪽 패널이 사라지고 피치가 창 높이를 꽉 채운다.
  - 카드를 누르면 상세 서랍이 열린다.
  - Esc로 원래 화면에 돌아온다(창 크기도 원래대로).
- AI 추천을 누른 뒤에도 포메이션 선택이 보인다.
- 사용자 지정 배치로 AI 추천을 누르면 그 배치로 짜 준다.
- 보관함에 저장하고 불러오면 사용자 지정 배치가 그대로 돌아온다. 예전 저장본도 열린다.

## 8. 결과물 형식

- 고친 파일마다 **바뀐 부분을 정확히** 보여 줘라(파일 경로 + 전후 코드, 또는 unified diff). 새 파일은 전체 내용을 준다.
- 빌드와 테스트를 실제로 돌린 결과(통과 개수)를 적는다. 돌리지 못했으면 "돌리지 못함"이라고 솔직히 쓴다.
- 브랜치 `claude/folder-permissions-check-1pee7k`에 커밋한다.
  - 메시지는 영어 한 줄 제목 + 본문 bullet으로 쓴다. 예: `Let the squad canvas fill the window and edit formations by dragging`
  - 커밋 전에 `git status`로 `*.local.json`이나 키가 없는지 확인한다.
- 배포 확인용 명령 (앱을 끈 뒤):
  `dotnet publish src/FcHelper.App -c Release -r win-x64 -o "C:\Projects\fc helper\app-latest" -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`
  실행은 `app-latest\FcHelper.exe --studio`로 한다.

---
