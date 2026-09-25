# Gemini 작업 프롬프트: 아이콘 세트와 꿀벌 배지 고급화

> 저장소 루트에서 Gemini CLI로 실행한다면 이렇게만 입력하면 됩니다: "docs/GEMINI_ICON_PROMPT.md를 읽고 첫 번째 --- 와 마지막 --- 사이 지시를 그대로 수행해."

---

너는 C# .NET 10 WPF 앱 `fconline-helper`(FC 온라인 보조 도구)의 UI 디자이너 겸 WPF 개발자다. 이번 작업은 **앱 전체의 아이콘을 하나의 고급스러운 세트로 바꾸는 것**이다. 기능 로직은 건드리지 않는다.

## 0. 먼저 읽을 것 (읽고 계획을 짧게 말한 다음 시작)

- `src/FcHelper.App/App.xaml`: 색 브러시와 공용 스타일
  - 브러시: `Bg #15181D`, `Panel #1E2229`, `Raised`, `Line #2C323B`, `Text #E8EAED`, `Muted #9AA3AE`, `Accent #3DDC97`, `AccentSoft`, `Info #6CB6FF`, `Warn #FFB547`, `Danger`
  - 스타일: `Ghost`, `Primary`, `Chip`, `HoneyMark`
- `src/FcHelper.App/Skin.cs`: 교체 가능한 PNG 스킨
  - `%LOCALAPPDATA%\FcHelper\skin\<키>.png`에 파일이 있으면 그 그림을 쓴다. `Skin.Get(key)`, `Skin.Brush(key)`, 키 목록은 `Skin.Keys`.
  - 설명 문서: `docs/SKIN_ASSETS.md`
- `src/FcHelper.App/Studio/StudioWindow.xaml.cs`
  - 왼쪽 메뉴 9개: `PageInfo(Key, Label, Glyph)`
  - 메뉴 스킨 PNG(`nav-<key>`)가 없으면 Segoe MDL2 글리프를 한 가지 색으로 그린다(68행 근처).
- `src/FcHelper.App/HomeWindow.xaml.cs`: 홈 카드 아이콘 3개. `EntryIcon("home-search", 글리프)`가 PNG 아니면 MDL2 글리프다.
- `src/FcHelper.App/Studio/PitchView.cs`: 피치 선수 카드의 🐝 배지와 🔒 표시
- `src/FcHelper.Market/ManagerMode.cs`: `Honey.Mark = "🐝"`. Market 프로젝트는 WPF를 모르므로 **이 문자열은 그대로 둔다.** 앱에서 이 문자열을 텍스트로 그리던 곳만 이미지로 바꾼다.
- 🐝가 들어간 곳
  - `Studio/StudioKit.cs`(ValueRow.Honey, PickRow.Honey)
  - `Studio/ValuePage`, `Studio/PicksPage`의 DataGrid "꿀" 열(`ElementStyle="{StaticResource HoneyMark}"`)
  - `ManagerWindow.xaml(.cs)`(🐝 꿀선수 찾기 탭, HoneyRow)
  - `Studio/SquadPage.xaml.cs`(CandidateButton)

빌드는 `dotnet build src/FcHelper.App -c Debug`, 테스트는 `dotnet test tests/FcHelper.Tests`다. 지금 테스트 161개가 통과하고, 끝났을 때도 전부 통과해야 한다.

## 1. 왜 하는가 (문제)

1. **WPF는 컬러 이모지를 못 그린다.** 🐝 🔒 🎯 📝 🔁 🖼 ✨ 📖 같은 이모지가 흑백 윤곽이나 깨진 모양으로 나온다.
   - 특히 🐝 꿀선수 표시는 색이 없어서 꿀벌인지 알아보기 어렵다. 사용자가 직접 지적한 문제다.
2. **메뉴와 버튼 아이콘이 싸 보인다.**
   - 메뉴는 Segoe MDL2 글리프 또는 한 가지 색의 선 아이콘 PNG다.
   - 도구 버튼은 글자 기호(↶ ⛶ ✕ ◀ ▶ ✓ ⬆ ⚠ →)다.
   - 고급스러운 스포츠 분석 앱처럼 보이지 않는다.

## 2. 만들 것

### 2-1. 벡터 아이콘 세트 `src/FcHelper.App/Icons.xaml` (ResourceDictionary)

- 아이콘마다 `DrawingImage` 하나를 만든다. 키는 `Icon.<이름>`이다.
  - 24×24 좌표계, `GeometryDrawing`과 `Path` 데이터를 쓴다.
  - **벡터**라 어떤 크기·확대에서도 선명하고, 파일도 가볍다. PNG를 만들지 마라.
- **스타일: 투톤(duotone)**
  - 주선은 `#E8EAED`, 선 두께 1.75~2, 끝과 이음은 둥글게 한다.
  - 강조면 하나는 `#3DDC97`로 채운다. 불투명도 0.9 또는 그라데이션 `#3DDC97 → #2BB57A`.
  - 필요하면 보조색 `#6CB6FF` 또는 `#FFB547`을 **한 아이콘에 한 가지만** 쓴다.
  - 모든 아이콘이 같은 시각 무게(선 두께, 여백 2px, 둥근 정도)를 가져야 한다. 한 세트처럼 보여야 한다.
- 그려도 되는 것과 안 되는 것
  - 직접 그리거나, 라이선스가 자유로운 아이콘 세트의 경로 데이터를 가져와 투톤으로 다시 칠해도 된다. Lucide(ISC), Tabler(MIT) 정도다.
  - 가져온 경우 `Icons.xaml` 맨 위 주석에 출처와 라이선스를 적는다.
  - 실제 구단 로고, 실존 인물, FC온라인·EA·넥슨 로고는 금지다.
- 선택 상태: 메뉴 선택 등 강조 상태용으로 주선까지 `#3DDC97`인 변형 `Icon.<이름>.Active`를 둔다(메뉴 9개와 탭 3개만).

필요한 아이콘과 쓰이는 곳(전부 만들 것):

| 키 | 모양 제안 | 바꿀 곳 |
|---|---|---|
| `Icon.Nav.Squad` | 전술판 + 선수 점 4-2-2-2 | 메뉴 스쿼드 짜기 (MDL2 U+E80F) |
| `Icon.Nav.Picks` | 별 + 돋보기 | 숨은 랭커픽 |
| `Icon.Nav.Value` | 가격표 + 아래 화살표 | 가성비 찾기 |
| `Icon.Nav.Grade` | 계단 막대 + 위 화살표 | 강화 효율 |
| `Icon.Nav.Salary` | 저울 | 급여 효율 |
| `Icon.Nav.Trends` | 꺾은선 그래프 | 시세 추이 |
| `Icon.Nav.MySquad` | 유니폼 셔츠 | 내 스쿼드 |
| `Icon.Nav.Opponent` | 과녁 + 방패 | 상대 맞춤 |
| `Icon.Nav.TeamColor` | 겹친 원 3개(팔레트) | 팀컬러 |
| `Icon.Home.Search` | 돋보기 + 방패 | 홈 구단주 검색 (44px로 쓰임, 디테일 조금 더) |
| `Icon.Home.Squad` | 전술판 + 선수 | 홈 스쿼드 도우미 |
| `Icon.Home.Manager` | 작전판(클립보드) + 호루라기 | 홈 감독모드 |
| `Icon.Honey` | **꿀벌 배지** (아래 2-2) | 🐝 전부 |
| `Icon.Lock` | 자물쇠 | 🔒 (피치 카드, 고정 버튼) |
| `Icon.Undo` | 되돌리기 화살표 | ↶ 버튼 |
| `Icon.Fullscreen` | 네 모서리 확장 | ⛶ 크게 보기 |
| `Icon.Image` | 사진 액자 | 🖼 저장 |
| `Icon.Close` | ✕ | ✕ 버튼들 |
| `Icon.ChevronLeft` / `Icon.ChevronRight` | 꺾쇠 | ◀ ▶ 접기/펴기 |
| `Icon.Check` | 체크 | ✓ (고른 미니페이스) |
| `Icon.Update` | 원 안 위 화살표 | ⬆ 업데이트 알림, 트레이 메뉴 옆 |
| `Icon.Warning` | 삼각 느낌표 (`#FFB547`) | ⚠ 주의, 거래 거의 없음 |
| `Icon.Target` | 과녁 | 🎯 약점 (SearchWindow) |
| `Icon.Note` | 메모지 | 📝 메모 |
| `Icon.Rematch` | 순환 화살표 | 🔁 재대결 (ReportView) |
| `Icon.Signature` | 반짝이는 별 | ✦ 시그니처 골 |
| `Icon.Sparkle` | AI 반짝임 | ✨ AI 추천픽 버튼 |
| `Icon.Guide` | 펼친 책 | 📖 키 발급 방법 보기 |

### 2-2. 꿀벌 배지 `Icon.Honey` (가장 중요)

- 작게(12~16px) 써도 한눈에 꿀벌로 보여야 한다.
  - 몸통은 둥근 타원, 노랑 `#FFC43D`에서 호박색 `#F5A623` 그라데이션이다.
  - 몸통에 진한 줄무늬 2줄(`#2A2118`)을 넣는다.
  - 날개 2개는 반투명 흰색(`#FFFFFF` 불투명도 0.7)에 얇은 테두리를 두른다.
  - 더듬이는 짧게 두 개다.
- 뒤에 `#FFC43D` 20% 원형 후광을 깔아 어두운 배경에서도 튀게 한다.
- 이 앱에서 노랑 계열은 꿀벌에만 쓴다. 꿀선수 표시는 앱의 "시그니처 색"이다.

### 2-3. 쓰는 방법 `src/FcHelper.App/AppIcons.cs`

- `public static ImageSource? AppIcons.Get(string key, bool active = false)`
  1. 스킨 PNG가 있으면 그것을 쓴다. 키 매핑은 `Icon.Nav.Squad` → `nav-squad`, `Icon.Home.Search` → `home-search`이고 나머지는 그대로 쓴다.
     - 사용자가 넣어 둔 PNG가 이긴다는 **기존 규칙을 지킨다.**
  2. 없으면 `Icons.xaml`의 `DrawingImage`를 쓴다.
  3. 둘 다 없으면 null이다.
- `public static Image AppIcons.Make(string key, double size, bool active = false)`: 크기를 지정한 `Image`를 만든다(`RenderOptions.BitmapScalingMode=HighQuality`, `SnapsToDevicePixels`).
- `App.xaml`의 `Application.Resources`에 `Icons.xaml`을 MergedDictionaries로 합친다.
  - 순서에 주의한다. `App.xaml`은 브러시를 **맨 위에** 정의해야 앱이 뜬다. 예전에 StaticResource 전방 참조 때문에 시작하자마자 죽은 적이 있다.
- XAML에서 쓰기 편한 마크업 확장 `{app:Icon Undo}`도 만든다. 이미 `{app:Skin empty}`가 있으니 그것을 참고한다.

### 2-4. 바꾸는 규칙

- **이모지와 글자 기호를 전부 이미지로 바꾼다.** 버튼 `Content="↶"` 같은 것은 `StackPanel(아이콘 16px + 글자)`로, 아이콘만 있는 버튼은 아이콘만(ToolTip 필수) 둔다.
  - 코드에서 만드는 곳(StudioKit, PitchView, SquadPage, ReportView, UpdateToast, App 트레이)도 같게 바꾼다.
  - 문자열 안에 이모지를 붙이던 코드는 아래처럼 바꾼다(예: `Honey.Mark + " " + 이름`).
    - 목록 셀과 버튼은 이미지와 글자를 나란히 두는 형태로 바꾼다.
    - DataGrid "꿀" 열은 `DataGridTemplateColumn`으로 바꾸고, `Honey` 값이 비어 있지 않을 때만 `Icon.Honey` 16px를 보여 준다.
  - **트레이 메뉴(WinForms ContextMenuStrip)와 풍선 알림 글자**는 이미지로 바꾸기 어렵다. 이모지만 빼고 글자로 둔다.
    - 트레이 메뉴 항목의 `Image`는 `DrawingImage`를 `System.Drawing.Bitmap`으로 렌더해서 넣어도 된다(16px).
- **크기 규칙**

  | 쓰이는 곳 | 크기 |
  |---|---|
  | 메뉴 | 20px |
  | 도구 버튼 | 16px |
  | 탭 칩 | 16px |
  | 목록·카드 배지 | 14px |
  | 피치 카드 🔒·🐝 | 14px (카드 모서리에 원형 배지로) |
  | 홈 카드 | 44px |
  | 업데이트 알림 제목 | 20px |

- **메뉴 선택 상태**: 선택된 메뉴는 `Icon.X.Active`로 바꾸고, 왼쪽에 3px 민트 막대를 둔다. 지금 막대가 있으면 유지한다.
- **감독모드 탭**: `🐝 꿀선수 찾기`는 `Icon.Honey` + "꿀선수 찾기"로 바꾼다. 다른 두 탭에도 아이콘을 붙인다(팀컬러 픽률은 `Icon.Nav.TeamColor`, 구단주 분석은 `Icon.Home.Manager`).
- **피치 카드**
  - 🐝는 카드 왼쪽 위 모서리에 걸친 16px 원형 배지다(`Panel` 배경, 노랑 테두리 1px).
  - 🔒는 오른쪽 위에 같은 방식으로 둔다.
  - 둘 다 카드 글자를 가리지 않아야 한다.

## 3. 지켜야 할 것

- 게임 메모리, 인젝션, 패킷, 게임 프로세스 접근, 입력 자동화 금지(이번 작업과 무관하지만 규칙이다).
  - API 키와 `*.local.json`은 커밋하지 않는다. 저장소 루트의 임시 파일 `powershell.cmd`도 커밋하지 않는다.
- 가볍게 만든다. 아이콘은 한 번 만들어 `Freeze()`한 `DrawingImage`를 재사용한다. 애니메이션, 타이머, 큰 PNG는 쓰지 않는다.
- UI 문구는 한국어, 코드 주석은 영어, 주변 코드 스타일을 따른다.
- WPF 함정 세 가지
  1. 앱 공용 TextBlock 스타일은 `Text`가 비면 숨긴다. 그래서 `Run`만으로 만든 TextBlock은 사라진다. 그런 곳에는 별도 Style을 준다(`ApiGuideWindow.xaml`의 `Rich` 참고).
  2. `Ghost` 버튼 스타일은 `BorderBrush`를 무시한다. 선택 표시는 내용물로 한다.
  3. 한 요소는 부모를 하나만 가진다. 같은 `Image`를 두 곳에 넣지 않는다.
- 스킨 PNG가 있으면 PNG가 이기는 규칙을 깨지 않는다. `docs/SKIN_ASSETS.md`에 "아이콘은 기본이 벡터이고, 같은 키의 PNG를 넣으면 바뀐다"는 설명과 키 목록을 더한다.

## 4. 확인 (완료 조건)

- 앱 전체에서 이모지 문자가 **화면에 그려지는 곳이 없다.** 트레이 풍선 글자는 예외다.
  - `src/FcHelper.App`에서 U+1F300–U+1FAFF, U+2600–U+27BF, U+2B06 문자를 찾아 남은 곳이 트레이 글자뿐인지 목록으로 보고한다.
- Segoe MDL2 글리프를 쓰는 곳이 없다(StudioWindow, HomeWindow).
- 확인할 화면
  - 가성비 찾기와 숨은 랭커픽의 "꿀" 열, 스쿼드 메이커 피치와 후보 목록, 감독모드 꿀선수 탭에서 꿀벌이 **노란 꿀벌로 또렷이** 보인다.
  - 다크 배경에서 메뉴 아이콘 9개가 한 세트로 보이고, 선택된 메뉴만 민트로 바뀐다.
  - 스킨 폴더에 `nav-squad.png`가 있으면 그 PNG가 우선하고, 지우면 벡터 아이콘이 나온다.
  - 125%, 150% 화면 배율에서도 아이콘이 흐리지 않다.
- 빌드 성공, 테스트 전부 통과(161개 이상).

## 5. 결과물 형식

- 새 파일(`Icons.xaml`, `AppIcons.cs`, 마크업 확장)은 전체 내용을 준다. 고친 파일은 바뀐 부분을 정확히 보여 준다(diff 또는 전후 코드).
- 빌드·테스트를 실제로 돌린 결과를 적는다. 못 돌렸으면 "돌리지 못함"이라고 솔직히 쓴다.
- 브랜치 `claude/folder-permissions-check-1pee7k`에 커밋한다.
  - 메시지는 영어 제목 한 줄 + bullet이다. 예: `Replace emoji and glyphs with a duotone vector icon set and a honey bee badge`
  - 커밋 전에 `git status`로 키, `*.local.json`, `powershell.cmd`가 없는지 확인한다.
- 배포 확인 명령(앱을 끈 뒤): `dotnet publish src/FcHelper.App -c Release -r win-x64 -o "C:\Projects\fc helper\app-latest" -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`
  - 실행: `app-latest\FcHelper.exe --studio`

---
