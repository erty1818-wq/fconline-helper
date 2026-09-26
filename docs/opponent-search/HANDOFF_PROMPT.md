# 다른 AI(Codex 등)에게 작업을 넘길 때 쓰는 프롬프트

Claude 사용량이 끝나서 다른 AI로 이어갈 때, 그 AI를 **저장소 폴더(`C:\Projects\fc helper\fconline-helper`)에서** 연 다음 아래 내용을 그대로 붙여 넣는다.

---

이 저장소는 여러 AI가 번갈아 작업하는 FC 온라인 보조 도구(C# .NET 10 WPF)다. 지금부터 네가 이어서 작업한다.

1. 먼저 다음 파일들을 이 순서대로 읽어라. 다 읽기 전에는 코드를 고치지 마라.
   - `AGENTS.md`: 절대 규칙, 빌드·테스트 명령, WPF 함정
   - `docs/opponent-search/PROGRESS.md`: 어디까지 했는지
   - `docs/opponent-search/PLAN.md`: 무엇을 만들지
2. `git log --oneline -15`와 `git status`로 실제 상태를 확인해라. PROGRESS.md "지금 하는 중"에 적힌 내용과 다르면 코드가 맞다고 보고 PROGRESS.md를 먼저 바로잡아라.
3. "지금 하는 중"에 작업이 있으면 그것부터 끝내라. 없으면 상태 표에서 `대기`인 가장 앞 작업을 골라라. PLAN.md의 순서와 의존을 지킨다.
4. 작업 하나를 **시작할 때:** PROGRESS.md에서 그 작업을 `진행 중`으로 바꾸고, "지금 하는 중"에 무엇을 할지 적어라.
5. 작업 하나를 **끝낼 때:** 한 커밋으로 올리고 푸시해라.
   - 빌드: `dotnet build src/FcHelper.App -c Debug`
   - 테스트: `dotnet test tests/FcHelper.Tests`, 전부 통과해야 한다.
   - PROGRESS.md: `완료`, 커밋 해시, 메모, "지금 하는 중" 비우기, 작업 일지 한 줄
   - 브랜치는 `claude/folder-permissions-check-1pee7k`다. `*.local.json`, API 키, `powershell.cmd`는 절대 커밋하지 마라.
6. 확인하지 못한 것은 추측하지 말고 PROGRESS.md "확인한 사실"이나 메모에 "미확인"으로 남겨라.
7. 릴리스 태그(`vX.Y.Z`)는 내가 "배포해줘"라고 할 때만 올려라.
8. 한 작업을 끝낼 때마다 무엇을 했는지 한국어로 짧게 보고하고 다음 작업으로 넘어가라.

---

## 짧은 버전

AI가 `AGENTS.md`를 자동으로 읽는 경우(Codex CLI 등)에 쓴다.

> AGENTS.md와 docs/opponent-search/PROGRESS.md, PLAN.md를 읽고, PROGRESS.md의 "지금 하는 중" 또는 다음 `대기` 작업부터 이어서 진행해. 작업마다 PROGRESS.md를 갱신하고 빌드·테스트 통과 후 커밋·푸시해.
