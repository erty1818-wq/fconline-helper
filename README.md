# fconline-helper

FC온라인 1:1 공식경기용 개인 Windows 컴패니언 프로그램.
NEXON Open API의 공개 데이터만 사용한다. 게임 메모리 접근, 인젝션, 패킷 가로채기, 입력 자동화는 하지 않는다.

- 기획안: [docs/PLANNING.md](docs/PLANNING.md)

## 지금 되는 것 (v0.1 진행 중)

- 닉네임으로 상대 검색 → 최근 공식경기 30경기 분석 (10경기마다 중간 결과 표시)
- 요약 카드: 전적, 최고 등급, 키보드/패드, **주의 3개**, **약점 1개**, 시그니처 골, 위험 선수, 특징
  - 모든 항목에 근거 표시: `직접`(API 값 그대로) / `계산`(집계) / `추정`(패턴 해석)
  - 캐시에 쌓인 다른 유저들을 기준선으로 "평균 대비"를 계산하고, 표본이 작으면 기준선 쪽으로 보정
- 재대결 전적, 상성 경보 (내 실점 패턴 × 상대 득점 패턴), 닉네임 변경 추적
- 상대 메모 + 원클릭 태그
- 상세 분석: 득점/실점 방식·위치, 패스 구성, 선수별 골·도움, 득점 조합, 선제골 승률 등
- 음성 브리핑 (선택), 트레이 상주, 단축키 `Ctrl+Alt+S`
- SQLite 캐시: 끝난 경기는 영구 보관 → 같은 상대를 다시 검색하면 새 경기만 API 호출

아직 없는 것: OCR 자동 인식, 오버레이, 음악 기능 (로드맵은 기획안 14장)

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

**처음 키를 받으면 `dump`로 실제 응답을 몇 건 저장해서 확인해 주세요.** API 필드 해석 중 일부
(좌표 방향, 슈팅 종류 코드)는 공식 문서를 직접 확인하지 못해 실측 검증이 필요합니다 (기획안 3.4).

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
