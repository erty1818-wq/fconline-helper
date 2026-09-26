# 구단주 검색 강화 — 진행 기록

> 이 파일은 작업자(AI)가 바뀌어도 이어갈 수 있게 하는 **단 하나의 기준**이다.
> 작업을 시작하거나 끝낼 때마다 반드시 갱신하고, 코드와 같은 커밋에 넣는다.
> 계획은 `PLAN.md`, 공통 규칙은 저장소 루트의 `AGENTS.md`에 있다.

## 지금 하는 중

- 작업: OS-05 슈팅 맵
- 담당: Claude (2026-09-26)
- 시작한 것: `SearchWindow.Shots.cs`에 반쪽 경기장 그림(Canvas)과 점. [상대가 찬 슛 / 상대가 허용한 슛] 전환. 점 색은 골 Accent, 유효 Info, 빗나감 Muted.
- 다음 단계: 코드 → 테스트(좌표 변환, 점 개수) → 화면 확인 → 커밋

## 상태 표

상태: `대기` · `진행 중` · `완료` · `보류`(이유를 적는다)

| ID | 작업 | 상태 | 커밋 | 메모 |
|---|---|---|---|---|
| OS-00 | 탭 구조와 창 크기 | 완료 | 992e99f | 칩 탭 7개, 요약 = 기존 카드. 내 전적 칩은 OS-18 전까지 숨김. 다른 탭은 "준비 중" 안내 |
| OS-01 | 첫 화면 (최근 검색·즐겨찾기·최근 상대·자동완성) | 완료 | 3d377ee | 즐겨찾기·최근 검색(×로 지움)·최근 상대(내 전적). 자동완성은 검색칸 아래 버튼 줄(Popup은 게임 위에 뜨므로 안 씀). 결과 머리에 별 버튼, 탭 줄에 [처음 화면] |
| OS-02 | 머리 부분 강화 (등급 아이콘·승무패 칩·연승연패) | 완료 | 041738b | 공식경기 최고 등급 엠블럼(공식 이미지), 최근 20경기 칩(왼쪽 최신), 2연속 이상일 때 연승·연패 |
| OS-03 | 빠른 재조회와 갱신 버튼 | 완료 | 16918b1 | 10분 안 재조회는 API 0회(`FcHelperOptions.RecheckAfter`). 탭 줄 오른쪽에 "n분 전 갱신"과 [갱신]. 확인이 중간에 멈추면 기록하지 않아 다음에 다시 확인한다 |
| OS-04 | 상대 스쿼드 피치 | 완료 | 3db6233 | 스쿼드 탭을 처음 열 때만 그린다. 최근 경기 선발, 포메이션 [추정], 선발 시세 합(구단가치 [계산]), 평균 OVR, 적용 팀컬러 [계산] |
| OS-05 | 슈팅 맵 | 진행 중 | | |
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
- **등급 엠블럼 주소(확인함, 2026-09-26 데이터센터 랭킹 페이지):** `https://ssl.nexon.com/s2/game/fo4/obt/rank/large/update_2026/ico_rank{n}.png`, 80×80. n은 division id를 정렬한 목록에서의 위치다: 800 슈퍼챔피언스=0, 900=1, 1000=2, 1100~1300=3~5, 1700~1900=6~8, 2000~2200=9~11, 2300~2500=12~14, 2600~2800=15~17, 2900~3100=18~20. 21개(0~20)만 있다. 랭커 행이 0번을 쓰고, 그림이 1·2·3 묶음으로 나뉘는 것까지 눈으로 확인했다. 코드: `Services/RecentForm.cs`의 `DivisionIcon`.
- `OpponentReport.Matches`(분석한 경기, 최신 먼저)와 `Form`(RecentForm)이 생겼다. 이후 탭은 `Matches`를 쓰면 된다.
- 재조회: kv `lookup.checked.{ouid}`(ISO 시각)에 마지막으로 **끝까지** 확인한 시각을 둔다. `LookupAsync(..., refresh: true)`가 [갱신]이다. `OpponentReport.CheckedAt`.
- 탭별 코드는 partial 파일로 나눈다: `SearchWindow.Squad.cs`(OS-04). 탭을 열 때 `OnTabShown(key)`가 불리고, 같은 경기면 다시 그리지 않는다. 다음 탭도 `SearchWindow.Shots.cs` 같은 식으로 만들고 `OnTabShown`의 switch에 한 줄 넣는다. 공용 도우미: `SetTab(panel, ...)`, `TabHint(text)`.
- 선발·포메이션: `SquadContext.StartersOf(side)`, `SquadContext.FormationOf(side)`(Services).
- **1단계 화면 확인(2026-09-26, 실제 데이터):** 첫 화면 목록·자동완성·즐겨찾기 별, 머리 엠블럼·칩·연승, 10분 안 재조회(즉시, API 0회), 스쿼드 탭(760 폭, 11명, 팀컬러 감지 소속 11명 4단계 등) 모두 동작. 팀컬러 감지는 처음에 1분 넘게 걸린다(데이터센터 2초에 1번).
- 공용 `Ghost` 버튼 템플릿은 이제 `HorizontalContentAlignment`를 따른다(기본 Center라 다른 버튼은 그대로).
- **슛 result 코드(확인함, 2026-09-26, 캐시 공식경기 734개 면):** 3 = 골(result 3 개수 = goalTotal, 기록 있는 면 전부 일치), 1 = 유효(막힘, 1+3 = effectiveShootTotal), 2 = 빗나감. 개수 합 = shootTotal. 안 맞는 면은 전부 shoot 요약이 빈 몰수 경기였다.
- **슛 좌표:** 각 팀 기준으로 x = 1이 공격하는 골문, y가 작을수록 공격자의 왼쪽(docs/PLANNING.md 3.4, `Core/Pitch.cs`). 골 x 평균 0.90.
- 아이콘 `Icon.Star`(빈 별) / `Icon.Star.Active`(채운 별, Warn 색)를 Icons.xaml에 추가했다. `AppIcons.Make("Icon.Star", 14, active)`.

## 작업 일지 (새 항목을 맨 아래에 추가)

- 2026-09-26 · Claude · 계획 수립과 문서화(PLAN.md, PROGRESS.md, AGENTS.md, HANDOFF_PROMPT.md). 코드는 바꾸지 않았다. 기준 커밋 `a0a324c`.
- 2026-09-26 · Claude · OS-00 완료: 탭 구조, 창 크기 기억, 넓은 탭 자동 확장. 빌드·테스트 174개 통과. 실행 화면은 사용자 앱이 켜져 있어 아직 확인하지 않았다.
- 2026-09-26 · Claude · OS-01 완료: 첫 화면 세 목록, 자동완성, 즐겨찾기 별. 테스트 5개 추가(SearchStartTests), 전체 179개 통과. 실행 화면은 사용자 앱이 켜져 있어 아직 확인하지 않았다.
- 2026-09-26 · Claude · OS-02 완료: 등급 엠블럼, 승무패 칩, 연승·연패. 보고서에 Matches·Form·등급 id 추가. 테스트 8개 추가(RecentFormTests), 전체 187개 통과. 화면 확인은 아직.
- 2026-09-26 · Claude · OS-03 완료: 10분 재조회 생략, [갱신] 버튼, 마지막 갱신 표시. 기존 테스트 2개는 시계를 11분 넘겨 원래 뜻을 유지했다. 테스트 2개 추가, 전체 189개 통과.
- 2026-09-26 · Claude · OS-04 완료: 스쿼드 탭 피치. 1단계(OS-00~04) 코드 끝. 테스트 1개 추가, 전체 190개 통과. 사용자 앱(app-latest)이 켜져 있어 실행 화면은 아직 확인하지 않았다. 다음 작업자는 먼저 앱을 빌드해 검색 창 네 가지(첫 화면, 머리 칩, 갱신, 스쿼드 탭)를 확인할 것.
- 2026-09-26 · Claude · 1단계 화면 확인. 고친 것: 첫 화면 이름 왼쪽 정렬(Ghost 템플릿), 승무패 칩 크기(440 폭에서 한 줄), 스쿼드 탭 카드의 '보유'·자물쇠 제거(상대 카드라 시세 표시), 팀컬러 반영 후 평균 OVR 갱신, 피치 폭 470.
