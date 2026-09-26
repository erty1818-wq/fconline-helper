# 구단주 검색 강화 — 진행 기록

> 이 파일은 작업자(AI)가 바뀌어도 이어갈 수 있게 하는 **단 하나의 기준**이다.
> 작업을 시작하거나 끝낼 때마다 반드시 갱신하고, 코드와 같은 커밋에 넣는다.
> 계획은 `PLAN.md`, 공통 규칙은 저장소 루트의 `AGENTS.md`에 있다.

## 지금 하는 중

- (없음. 다음은 OS-02)

## 상태 표

상태: `대기` · `진행 중` · `완료` · `보류`(이유를 적는다)

| ID | 작업 | 상태 | 커밋 | 메모 |
|---|---|---|---|---|
| OS-00 | 탭 구조와 창 크기 | 완료 | 992e99f | 칩 탭 7개, 요약 = 기존 카드. 내 전적 칩은 OS-18 전까지 숨김. 다른 탭은 "준비 중" 안내. 화면 확인 미완(사용자 앱 실행 중) |
| OS-01 | 첫 화면 (최근 검색·즐겨찾기·최근 상대·자동완성) | 완료 | (OS-01 커밋) | 즐겨찾기·최근 검색(×로 지움)·최근 상대(내 전적). 자동완성은 검색칸 아래 버튼 줄(Popup은 게임 위에 뜨므로 안 씀). 결과 머리에 별 버튼, 탭 줄에 [처음 화면]. 화면 확인 미완 |
| OS-02 | 머리 부분 강화 (등급 아이콘·승무패 칩·연승연패) | 대기 | | |
| OS-03 | 빠른 재조회와 갱신 버튼 | 대기 | | |
| OS-04 | 상대 스쿼드 피치 | 대기 | | |
| OS-05 | 슈팅 맵 | 대기 | | |
| OS-06 | 최근 경기 요약 (골 유형·최고 선수) | 대기 | | |
| OS-07 | 슛 종류별 성공률과 xG [추정] | 대기 | | |
| OS-08 | 경기 요약 지표 줄과 슈팅 타임라인 | 대기 | | |
| OS-09 | 시간대 그래프 | 대기 | | |
| OS-10 | 플레이 습관 (몰수·시간대) | 대기 | | |
| OS-11 | 나 vs 상대 비교 | 대기 | | |
| OS-12 | 패드 / 키보드 분리 성적 | 대기 | | |
| OS-13 | 포메이션별 성적 | 대기 | | |
| OS-14 | 포메이션별 대표 라인업 | 대기 | | OS-13 다음 |
| OS-15 | 팀 단위 분석 | 대기 | | |
| OS-16 | 라이벌 매치 (두 구단주 비교) | 대기 | | |
| OS-17 | 선수별 공격·수비 지수 | 대기 | | |
| OS-18 | 승률 개선 분석 (내 계정) [추정] | 대기 | | |
| OS-19 | 분석 이미지로 저장·복사 | 대기 | | |

## 확인한 사실 (다음 작업자가 다시 조사하지 않게)

- SearchWindow 구조(OS-00 이후): `StartPanel`(검색 전 첫 화면, 내용은 `StartContent`) / `SummaryTab`(기존 카드 `Card`) / `SquadTab`·`ShotsTab`·`FlowTab`·`CompareTab`·`PlayersTab`·`MineTab`. 각 탭 내용은 `SquadContent` 같은 이름의 StackPanel에 코드로 채운다. 채울 때 `Placeholder` 안내를 지운다(`Children.Clear()`).
- 탭 전환은 `SelectTab(key, widen)`. 키: summary, squad, shots, flow, compare, players, mine. 요약이 아닌 탭을 누르면 폭이 760보다 좁을 때 760으로 넓힌다.
- 창 크기는 `AppSettings.SearchWidth/SearchHeight`에 닫을 때 저장된다.
- 첫 화면 데이터: `SearchHistory`(Services, kv `search.recent` 최대 10 · `search.favorites` 최대 30, 대소문자 무시), `FcDatabase.RecentOpponents(myOuid)`(내 캐시 경기 최근 300개에서 상대별 내 승무패), `FcDatabase.SuggestNicknames(prefix)`(users + match_side 닉네임 앞글자, `%`·`_`는 글자 그대로).
- 내 ouid는 `db.FindUserByNickname(Settings.MyNickname)`로 찾는다. `SyncMyMatchesAsync`가 `ResolveUserAsync`로 users에 넣어 두므로 동기화를 한 번 하면 생긴다.
- 아이콘 `Icon.Star`(빈 별) / `Icon.Star.Active`(채운 별, Warn 색)를 Icons.xaml에 추가했다. `AppIcons.Make("Icon.Star", 14, active)`.

## 작업 일지 (새 항목을 맨 아래에 추가)

- 2026-09-26 · Claude · 계획 수립과 문서화(PLAN.md, PROGRESS.md, AGENTS.md, HANDOFF_PROMPT.md). 코드는 바꾸지 않았다. 기준 커밋 `a0a324c`.
- 2026-09-26 · Claude · OS-00 완료: 탭 구조, 창 크기 기억, 넓은 탭 자동 확장. 빌드·테스트 174개 통과. 실행 화면은 사용자 앱이 켜져 있어 아직 확인하지 않았다.
- 2026-09-26 · Claude · OS-01 완료: 첫 화면 세 목록, 자동완성, 즐겨찾기 별. 테스트 5개 추가(SearchStartTests), 전체 179개 통과. 실행 화면은 사용자 앱이 켜져 있어 아직 확인하지 않았다.
