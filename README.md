# fconline-helper

FC온라인 1:1 공식경기용 개인 Windows 컴패니언 프로그램.
NEXON Open API의 공개 데이터만 사용한다. 게임 메모리 접근, 인젝션, 패킷 가로채기, 입력 자동화는 하지 않는다.

- 기획안: [docs/PLANNING.md](docs/PLANNING.md)

## 지금 되는 것 (v0.1 진행 중)

- 닉네임으로 상대 검색 → 최근 공식경기 30경기 분석 (10경기마다 중간 결과 표시)
- 요약 카드: 전적, 최근경기 등급·최고 등급, 키보드/패드, **주의 3개**, **약점 1개**, 시그니처 골, 위험 선수, 특징
  - 모든 항목에 근거 표시: `직접`(API 값 그대로) / `계산`(집계) / `추정`(패턴 해석)
  - 캐시에 쌓인 다른 유저들을 기준선으로 "평균 대비"를 계산하고, 표본이 작으면 기준선 쪽으로 보정
- 재대결 전적, 상성 경보 (내 실점 패턴 × 상대 득점 패턴), 닉네임 변경 추적
- 상대 메모 + 원클릭 태그
- 상세 분석: 득점/실점 방식·위치, 패스 구성, 선수별 골·도움, 득점 조합, 선제골 승률 등
- 음성 브리핑 (선택), 트레이 상주, 단축키 `Ctrl+Alt+S`
- SQLite 캐시: 끝난 경기는 영구 보관 → 같은 상대를 다시 검색하면 새 경기만 API 호출

- **스쿼드·시세 엔진** (`FcHelper.Market`): 스쿼드 짜기(예산·급여·강화·팀컬러·모드 4가지), 랭커픽·숨은 랭커픽, 강화 효율,
  급여 효율, 시세 추이·급락 알림, 내 스쿼드 업그레이드, 상대 맞춤 추천, 포메이션 상성. 시세·랭커 데이터는 자동 갱신.
  화면은 아직 CLI(`fch squad`, `fch picks` …)와 "가성비 찾기" 창뿐이고, 전용 UI는 `docs/UI_PROMPT.md` 사양으로 만든다.

아직 없는 것: 자동 상대 인식(v0.5: 설정에서 완전 자동 / 단축키 캡처 / 수동 검색 선택), 오버레이, 음악 기능 (로드맵은 기획안 14장)

API 필드 해석은 실제 응답으로 검증했다 (기획안 3.4).

## 실행하기

### 1. API 키 발급
[NEXON Open API](https://openapi.nexon.com/)에서 FC온라인 앱을 등록하고 API 키를 받는다.

### 2-A. 빌드된 exe 받기
GitHub Actions의 **CI** 워크플로 실행 결과에서 `FcHelper-win-x64` 아티팩트를 받는다.
- `FcHelper.exe`: Windows 앱 (.NET 런타임 포함, 설치 불필요)
- `fch.exe`: 명령줄 도구

### 2-B. 직접 빌드
[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 설치 후:

```powershell
dotnet run --project src/FcHelper.App
```

처음 실행하면 설정 창이 뜬다. API 키와 (선택) 내 닉네임을 입력한다.

### 명령줄 도구 (API 동작 확인용)

```powershell
$env:FCH_API_KEY = "발급받은 키"
dotnet run --project src/FcHelper.Cli -- search "상대닉네임" --me "내닉네임"
dotnet run --project src/FcHelper.Cli -- dump "아무닉네임" --count 3   # 원본 JSON을 docs/samples에 저장
```

`dump`로 저장한 `*.local.json`이 있으면 `dotnet test`가 그 실제 응답으로도 필드 해석을 다시 확인한다.

## 구조

```
src/
  FcHelper.Core       API 응답 모델, 경기 시각 디코딩, 경기장 구역
  FcHelper.NexonApi   API 클라이언트, 호출 제한, 재시도
  FcHelper.Data       SQLite 캐시
  FcHelper.Analysis   분석 엔진 (순수 함수)
  FcHelper.Services   캐시 → API → 분석 흐름
  FcHelper.App        WPF 앱 (트레이, 검색 창, 설정)
  FcHelper.Cli        명령줄 도구
tests/FcHelper.Tests
```

데이터 위치: `%LOCALAPPDATA%\FcHelper` (DB, 설정). API 키는 Windows DPAPI로 암호화해 저장한다.

## 테스트

```
dotnet test
```

Data based on NEXON Open API.
