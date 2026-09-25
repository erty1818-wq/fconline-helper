using System.Text;
using FcHelper.Data;
using FcHelper.Market;
using FcHelper.NexonApi;
using FcHelper.Services;

// Command-line front end: useful for checking the API with a real key before the Windows app is ready,
// and for saving raw samples (docs/samples) to verify the field semantics in docs/PLANNING.md 3.4.

Console.OutputEncoding = Encoding.UTF8;

if (args.Length < 1 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 1;
}

var command = args[0];
var positional = new List<string>();
var named = new Dictionary<string, string>();
for (var i = 1; i < args.Length; i++)
{
    if (args[i].StartsWith("--") && i + 1 < args.Length) named[args[i][2..]] = args[++i];
    else positional.Add(args[i]);
}
string? Option(string name) => named.GetValueOrDefault(name);

var apiKey = Option("key") ?? Environment.GetEnvironmentVariable("FCH_API_KEY");
if (SquadCommands.Names.Contains(command))
{
    // Market data needs no key; ranker stats and opponent lookups use it when present.
    return await SquadCommands.RunAsync(command, positional, Option, apiKey);
}
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("API 키가 없습니다. FCH_API_KEY 환경 변수나 --key 옵션으로 넘겨주세요.");
    return 2;
}

var options = new FcHelperOptions
{
    MyNickname = Option("me"),
    MatchWindow = int.TryParse(Option("window"), out var w) ? Math.Clamp(w, 1, 100) : 30,
};
var rate = double.TryParse(Option("rate"), out var r) && r > 0 ? r : 5;
var limiter = new RateLimiter(rate);
var http = new HttpClient();
var api = new FcOnlineApi(http, apiKey, limiter);
var db = new FcDatabase(Option("db") ?? AppPaths.DatabasePath);
var market = Option("market") == "off" ? null : new DataCenterClient(http, new RateLimiter(1));
var service = new FcHelperService(api, db, options, market: market);

try
{
    switch (command)
    {
        case "search" when positional.Count == 1:
            try
            {
                await service.EnsureMetadataAsync();
            }
            catch (HttpRequestException e)
            {
                Console.Error.WriteLine($"선수 이름 데이터를 받지 못했습니다 ({e.Message}). 선수는 번호로 표시됩니다.");
            }
            if (options.MyNickname is not null) Console.Error.WriteLine($"내 경기 동기화: 새 경기 {await service.SyncMyMatchesAsync()}건");
            var progress = new Progress<LookupProgress>(p =>
            {
                if (p.Stage == LookupStage.FetchingMatches) Console.Error.WriteLine($"  불러오는 중 {p.Fetched}/{p.ToFetch}");
            });
            var report = await service.LookupAsync(positional[0], progress);
            if (report is null)
            {
                Console.Error.WriteLine($"'{positional[0]}' 닉네임을 찾지 못했습니다.");
                return 3;
            }
            Console.WriteLine(ReportText.Card(report));
            Console.WriteLine();
            Console.WriteLine($"[음성] {ReportText.Voice(report)}");
            Console.WriteLine($"(API 호출 {limiter.IssuedCount}회)  Data based on NEXON Open API.");
            return 0;

        case "dump" when positional.Count == 1:
            return await Dump(api, positional[0], int.TryParse(Option("count"), out var c) ? Math.Clamp(c, 1, 20) : 3, Option("out") ?? "docs/samples");

        case "manager" when positional.Count == 1:
            var mr = await service.ManagerAnalysisAsync(positional[0], Math.Clamp(int.TryParse(Option("count"), out var mc) ? mc : 100, 1, 100),
                new Progress<string>(m => Console.Error.Write($"\r{m}          ")));
            Console.Error.WriteLine();
            if (mr is null) { Console.Error.WriteLine($"'{positional[0]}' 닉네임을 찾지 못했습니다."); return 3; }
            Console.WriteLine($"{mr.Nickname} · 감독모드 {mr.Games}경기 · {mr.Wins}승 {mr.Draws}무 {mr.Losses}패 (승률 {mr.WinRate:P1}) · 평균 득점 {mr.GoalsFor:0.00} · 실점 {mr.GoalsAgainst:0.00}");
            foreach (var p in mr.Players.Take(14))
                Console.WriteLine($"  {p.Position,-4} {p.Name,-10} +{p.Grade,-2} {p.Games,3}경기 승률 {p.WinRate,5:P0} · 공격 {p.Attack,5:0.0} 수비 {p.Defence,5:0.0} · 골 {p.Goals:0.00} 도움 {p.Assists:0.00} · 패스 {p.PassRate:P0} · 평점 {p.Rating:0.0}");
            foreach (var f in mr.Formations.Take(6)) Console.WriteLine($"  전술 {f.Lines} · {f.Games}경기 {f.Wins}승 {f.Draws}무 {f.Losses}패 ({f.WinRate:P1})");
            Console.WriteLine("공격·수비 = 경기당 기록의 가중합 [계산]. Data based on NEXON Open API.");
            return 0;

        case "sync":
            if (options.MyNickname is null)
            {
                Console.Error.WriteLine("--me <내 닉네임> 이 필요합니다.");
                return 2;
            }
            Console.WriteLine($"새 경기 {await service.SyncMyMatchesAsync()}건 저장");
            return 0;

        default:
            PrintUsage();
            return 1;
    }
}
catch (NexonApiException e)
{
    Console.Error.WriteLine(e.IsAuthError ? $"API 키 오류: {e.Message}" : e.Message);
    return 4;
}
catch (HttpRequestException e)
{
    Console.Error.WriteLine($"NEXON Open API에 연결하지 못했습니다: {e.Message}");
    return 5;
}

static async Task<int> Dump(FcOnlineApi api, string nickname, int count, string outDir)
{
    var ouid = await api.GetOuidAsync(nickname);
    if (ouid is null)
    {
        Console.Error.WriteLine($"'{nickname}' 닉네임을 찾지 못했습니다.");
        return 3;
    }
    Directory.CreateDirectory(outDir);
    var ids = await api.GetUserMatchIdsAsync(ouid, 50, 0, count);
    foreach (var id in ids)
    {
        var path = Path.Combine(outDir, $"match-{id}.local.json");
        await File.WriteAllTextAsync(path, await api.GetMatchDetailJsonAsync(id));
        Console.WriteLine(path);
    }
    Console.WriteLine($"{ids.Count}건 저장. *.local.json 파일은 다른 유저 닉네임이 들어 있어 git에 올라가지 않습니다.");
    return 0;
}

static void PrintUsage() => Console.Error.WriteLine("""
    FC Online Helper CLI

    사용법 (API 키: FCH_API_KEY 환경 변수 또는 --key):
      fch search <닉네임> [--me <내 닉네임>] [--window 30] [--rate 5] [--market off]
          상대 분석 카드를 출력합니다. --me를 주면 재대결 전적과 상성 경보가 나옵니다.
          위험 선수의 능력치·시세는 FC온라인 데이터센터에서 조회합니다 (--market off로 끔).
      fch sync --me <내 닉네임>
          내 최근 경기 100건을 캐시에 저장합니다.
      fch value [--pos W] [--grade 8] [--min 1억] [--max 30억] [--minovr 135] [--trait 트릭스터] [--tc-only yes] [--top 15]
          같은 스펙 대비 싸게 거래되는 선수 (API 키 불필요, 앱이 받아 둔 시세 사용). 세부 조건: --maxovr --foot 5
          --body thin|normal|heavy --height 183-192 --maxpay 28 --stat 속력=130,밸런스=120 --core 0 --name 이름
      fch factors [--pos CB] [--grade 8]
          포지션 가격 요인: 코어 능력치·신특·체형·키가 시세에 주는 영향 (%, OVR 환산, BP) [추정].
      fch squad [--formation 4-2-2-2] [--budget 100억] [--grades 5,8] [--cap 310] [--teamcolor 2002,40515]
          스쿼드 추천. 급여 한도는 공식 스쿼드메이커에서 읽은 값이 기본. 팀컬러는 소속,특성 번호.
      fch upgrade --me <닉네임> [--budget 10억] [--pcroom yes] [--topclass yes] [--coupon 10] [--coupon-max 5억]
          내 스쿼드 교체 추천. 판매 수수료는 공식 계산식 (기본 40%, PC방·TOP CLASS·쿠폰 할인).
      fch picks | grade | salary | movers | formation | teamcolor | tailor  (자세한 옵션은 docs/UI_PROMPT.md)
      fch dump <닉네임> [--count 3] [--out docs/samples]
          match-detail 원본 JSON을 저장합니다 (필드 검증용).

    공통 옵션: --db <경로> (기본: %LOCALAPPDATA%\FcHelper\fchelper.db)
    """);
