# AGENTS.md — AI 작업자 공통 규칙 (Claude, Codex, Gemini …)

이 저장소는 여러 AI가 번갈아 작업한다. **작업을 시작하기 전에 이 파일과 진행 중인 작업의 PROGRESS 문서를 먼저 읽어라.**

## 지금 진행 중인 작업

- **구단주 검색 강화** — 계획: `docs/opponent-search/PLAN.md`, 진행 기록: `docs/opponent-search/PROGRESS.md`
- 이어받았으면 PROGRESS.md의 "지금 하는 중"부터 확인하고, 거기 적힌 작업을 이어서 끝낸 뒤 다음 작업으로 간다.

## 프로젝트 한 줄 요약

FC 온라인 보조 도구. C# .NET 10 WPF 트레이 앱(`src/FcHelper.App`) + CLI(`src/FcHelper.Cli`).
- 데이터는 **NEXON Open API**와 **공식 데이터센터(fconline.nexon.com)** 만 쓴다.
- 저장은 SQLite(`%LOCALAPPDATA%\FcHelper\fchelper.db`), 테스트는 xUnit(`tests/FcHelper.Tests`).

| 프로젝트 | 역할 |
|---|---|
| `FcHelper.Core` | 모델 (MatchDetail 등) |
| `FcHelper.NexonApi` | Open API 클라이언트, RateLimiter |
| `FcHelper.Data` | SQLite (FcDatabase) |
| `FcHelper.Analysis` | 구단주 분석 (UserAnalyzer) |
| `FcHelper.Services` | 조회 흐름 (FcHelperService, OpponentReport, ReportText) |
| `FcHelper.Market` | 시세·스쿼드·데이터센터 |
| `FcHelper.App` | WPF UI (SearchWindow = 구단주 검색, Studio = 스쿼드 도우미) |

## 절대 규칙 (어기면 안 됨)

1. **게임 메모리 읽기, DLL 인젝션, 패킷 스니핑, 게임 프로세스 접근, 입력 자동화는 절대 쓰지 않는다.** 공식 API, 데이터센터, 화면에 보이는 정보만 쓴다.
2. **API 키**는 환경 변수 `FCH_API_KEY` 또는 앱 설정(DPAPI 암호화)에만 있다.
   - 코드, 로그, 커밋, 채팅에 넣지 않는다.
   - 사용자에게 키를 붙여 넣으라고 하지 않는다.
3. **`*.local.json`은 커밋하지 않는다** (다른 유저 닉네임이 들어 있음). 저장소 루트의 `powershell.cmd`(임시 파일)도 커밋하지 않는다.
4. **어떤 창도 Topmost(항상 위)로 만들지 않는다.** 게임을 가린다. 사용자가 여러 번 강조했다.
5. **금액은 전부 억 단위다.**
   - 표시는 `Bp.Format`, 입력은 `Bp.TryParse`를 쓴다. 숫자만 입력하면 억이다: `10` = 10억.
   - 금액 입력 칸에는 `(억)` 라벨을 달고 `{x:Static market:Bp.InputHint}` 툴팁을 단다.
6. **직접·계산·추정을 구분한다.** 추정은 `[추정]`으로 표시하고 사실처럼 쓰지 않는다.
7. **가볍게 만든다.** 폴링과 타이머 루프를 쓰지 않는다. 필요한 순간에만 요청하고, 결과는 캐시한다.
   - Open API 개발 키는 하루 한도가 있다(429 OPENAPI00007).
   - 데이터센터 요청은 공용 `RateLimiter(0.5)`(2초에 1번)를 쓴다.
8. **게임 규칙** (사용자 확인):
   - +11이 거래 최고 강화다.
   - 소속 팀컬러는 전원에게, 특성 팀컬러는 해당 카드에만 적용된다.
   - 급여 한도는 310이다.
   - 강화 팀컬러는 8명에서 최고 단계다.
9. **앱 아이콘과 로고 (사용자 승인 완료, 변경·롤백 금지)**:
   - `Assets/app.ico` 및 `skin/logo.png`는 사용자가 지정하고 승인한 **FC 온라인 티어 엠블럼**(5각형 배지+왕관+외곽 테크 브래킷 선)으로 고정되어 있다.
   - 이전의 초록색 원형 공("FC") 아이콘이나 기본 이모지로 되돌리지 않는다.
   - 모든 WPF 윈도우(`Icon="pack://application:,,,/Assets/app.ico"`), 시스템 트레이, 바탕화면 바로가기는 이 아이콘을 참조한다.
   - 빌드/배포 시에도 이 아이콘과 로고 파일을 유지한다.

## 코드 규칙

- UI 문구는 **한국어**, 코드 주석과 커밋 메시지는 **영어**로 쓴다. 주변 코드의 이름, 주석 밀도, 스타일을 따른다.
- 공용 브러시와 스타일은 `src/FcHelper.App/App.xaml`에 있다(`Bg`, `Panel`, `Line`, `Text`, `Muted`, `Accent`, `Warn`, `Info`, `Ghost`, `Primary`, `Chip`, `CardPanel`, `Hint`, `FieldLabel`). 새 색을 만들지 않는다.
- 아이콘은 `AppIcons.Make("Icon.X", size)`를 쓴다(벡터: `Icons.xaml`). 이모지는 WPF에서 흑백으로 나오니 UI에 쓰지 않는다.
- 그림:
  - 미니페이스는 `Faces.GetAsync(spId)`
  - 표 칸 안에서는 `studio:Pictures.FaceOf="{Binding SpId}"`, `studio:Pictures.SeasonOf="{Binding Season}"`
  - 피치 그리기는 `Studio/PitchView.cs`
- 표(DataGrid)는 금액·퍼센트 글자를 값으로 정렬한다(`SmartSort`, 공용 스타일로 자동 적용).

## WPF 함정 (실제로 당한 것)

1. 공용 TextBlock 스타일은 Text가 비면 숨긴다. `Run`만 쓴 TextBlock은 사라진다. 이때는 별도 Style을 준다(`ApiGuideWindow.xaml`의 `Rich`).
2. 한 요소는 부모를 하나만 가진다. 같은 Image나 Panel을 두 곳에 넣으면 예외가 난다.
3. `Ghost` 버튼 스타일은 BorderBrush를 무시한다.
4. 공용 ComboBox 템플릿은 `IsEditable`을 지원하지 않는다(글자가 안 보인다). 대신 `IsTextSearchEnabled`를 쓴다.
5. `DataGrid.Sorting`은 라우팅 이벤트가 아니다. 클래스 처리기가 안 걸리니 첨부 속성으로 연결한다.
6. `App.xaml`에서 StaticResource는 정의보다 뒤에서만 쓸 수 있다. 전방 참조하면 앱이 시작하자마자 죽는다.

## 빌드 · 테스트 · 배포

```bash
dotnet build src/FcHelper.App -c Debug
dotnet test tests/FcHelper.Tests
```

로컬 배포는 앱을 끈 뒤 아래 명령을 쓴다. 산출물 폴더는 `C:\Projects\fc helper\app-latest`다.

```bash
dotnet publish src/FcHelper.App -c Release -r win-x64 -o "C:\Projects\fc helper\app-latest" -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

- **릴리스:** `git tag vX.Y.Z && git push origin vX.Y.Z`를 하면 `.github/workflows/release.yml`이 exe를 빌드해 GitHub Releases에 올린다. 친구들 앱은 이걸 보고 업데이트한다.
  - **사용자가 "배포해줘"라고 할 때만 한다.**
  - 새 태그는 직전 태그보다 높아야 한다. 최신 태그는 `git fetch --tags && git tag --sort=-v:refname | head -1`로 확인한다(2026-09-26 기준 v0.8.1). **확인 결과를 본 다음에 번호를 정한다.** 확인과 태그 만들기를 한 명령에 묶지 않는다. 여러 AI와 사용자가 번갈아 배포하므로 기억한 번호는 틀릴 수 있다(2026-09-26에 v0.8.0이 이미 있는 줄 모르고 v0.7.2를 올린 적이 있다. 그래서 같은 커밋을 v0.8.1로 다시 올렸다). 새 기능이면 가운데 숫자, 고친 것만이면 끝 숫자를 올린다.
  - 순서:
    1. 빌드·테스트 통과, `git status`가 깨끗한지 확인한다.
    2. 브랜치를 푸시해 HEAD와 `origin/claude/folder-permissions-check-1pee7k`가 같은지 확인한다.
    3. 태그를 만들고 푸시한다.
    4. 끝났는지 확인한다. `gh`가 설치되어 있지 않으니 공개 API를 쓴다.
       `curl -s https://api.github.com/repos/erty1818-wq/fconline-helper/releases/latest`에서 `tag_name`이 새 태그이고 `assets`에 `FcHelper.exe`(약 200MB)가 있으면 성공이다. 빌드는 몇 분 걸린다.
  - 친구에게 줄 것은 `FcHelper.exe` 하나뿐이다(단일 파일, .NET 포함). `.pdb`와 `skin` 폴더는 필요 없다. 릴리스 페이지 링크를 보내는 것이 가장 좋다. 한 번 받으면 이후 버전은 앱이 GitHub Releases를 보고 알아서 업데이트한다.
  - 친구에게 알릴 것: 서명 안 된 exe라 처음에 "Windows의 PC 보호"가 뜨면 [추가 정보] → [실행]. NEXON Open API 키는 각자 발급한다(첫 실행 안내 창).
  - 이 PC 바탕화면 바로가기가 옛 아이콘으로 보이면 exe 문제가 아니라 Windows 아이콘 캐시다. 바로가기 아이콘을 `app-latest\FcHelper-emblem.ico`(= `Assets/app.ico` 복사본)로 지정해 두었다.
- 브랜치는 `claude/folder-permissions-check-1pee7k`다. 작은 단위로 커밋하고 푸시한다.
  - 커밋 전 `git status`로 `*.local.json`, 키, `powershell.cmd`가 없는지 확인한다.

## 작업 방식 (끊겨도 이어갈 수 있게)

- 작업 하나를 **시작할 때** PROGRESS.md의 그 작업을 `진행 중`으로 바꾸고, "지금 하는 중"에 무엇을 어디까지 할지 적는다. 그다음 코드를 고친다.
- 작업 하나를 **끝낼 때** 다음을 한 커밋으로 올린다.
  1. 빌드와 테스트 통과
  2. 코드
  3. PROGRESS.md 갱신: `완료` 표시, 커밋 해시, 한 일, 남은 일
- 도중에 멈춰야 하면 PROGRESS.md "지금 하는 중"에 남긴다: 바꾼 파일, 되는 것, 안 되는 것, 다음 단계.
- 확인하지 못한 것은 확인하지 못했다고 쓴다. 추측을 사실처럼 쓰지 않는다.
