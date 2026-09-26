using System.Text.Json;
using FcHelper.Core;
using FcHelper.Core.Models;

namespace FcHelper.Tests.Fixtures;

/// <summary>
/// Builds synthetic matches in the API's shape. Score fields follow what real responses were verified to do
/// (docs/PLANNING.md 3.4): goalTotal counts the side's own shots, goalTotalDisplay adds the opponent's own goals
/// and becomes 3:0 on a forfeit.
/// </summary>
public sealed class MatchBuilder
{
    private static int _seq;
    private readonly string _id = $"m{Interlocked.Increment(ref _seq):D6}";
    private DateTime _date = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private int _matchType = 50;
    private readonly SideBuilder _a;
    private readonly SideBuilder _b;

    public MatchBuilder(string ouidA = "me", string ouidB = "opp")
    {
        _a = new SideBuilder(ouidA);
        _b = new SideBuilder(ouidB);
    }

    public MatchBuilder At(DateTime date) { _date = date; return this; }
    public MatchBuilder Type(int matchType) { _matchType = matchType; return this; }
    public MatchBuilder A(Action<SideBuilder> f) { f(_a); return this; }
    public MatchBuilder B(Action<SideBuilder> f) { f(_b); return this; }

    public MatchDetail Build()
    {
        var a = _a.Build();
        var b = _b.Build();
        var (da, db) = (a.Shoot.GoalTotal + b.Shoot.OwnGoal, b.Shoot.GoalTotal + a.Shoot.OwnGoal);
        var (ra, rb) = (da, db) switch
        {
            var (x, y) when x > y => ("승", "패"),
            var (x, y) when x < y => ("패", "승"),
            _ => ("무", "무"),
        };
        if (_a.ForfeitLoss) (ra, rb, da, db) = ("패", "승", 0, 3);
        if (_b.ForfeitLoss) (ra, rb, da, db) = ("승", "패", 3, 0);
        a = a with { MatchDetail = a.MatchDetail with { MatchResult = ra }, Shoot = a.Shoot with { GoalTotalDisplay = da } };
        b = b with { MatchDetail = b.MatchDetail with { MatchResult = rb }, Shoot = b.Shoot with { GoalTotalDisplay = db } };
        // Real data marks the other side of a forfeit as 1 (몰수승).
        if (_b.ForfeitLoss && !_a.ForfeitLoss) a = a with { MatchDetail = a.MatchDetail with { MatchEndType = 1 } };
        if (_a.ForfeitLoss && !_b.ForfeitLoss) b = b with { MatchDetail = b.MatchDetail with { MatchEndType = 1 } };
        return new MatchDetail { MatchId = _id, MatchDate = _date, MatchType = _matchType, MatchInfo = [a, b] };
    }

    public string Json() => JsonSerializer.Serialize(Build(), FcJsonContext.Default.MatchDetail);
}

public sealed class SideBuilder(string ouid)
{
    private readonly List<ShootDetail> _shots = [];
    private readonly List<MatchPlayer> _players = [];
    private string _nickname = ouid + "_nick";
    private int _possession = 50;
    private string _controller = "keyboard";
    private int _pause;
    private int _throughPass;
    private int _passTry = 100;
    private int _ownGoals;
    private int _division;
    public bool ForfeitLoss { get; private set; }

    public SideBuilder Nick(string n) { _nickname = n; return this; }
    public SideBuilder Possession(int p) { _possession = p; return this; }
    public SideBuilder Controller(string c) { _controller = c; return this; }
    public SideBuilder Pauses(int p) { _pause = p; return this; }
    public SideBuilder Forfeit() { ForfeitLoss = true; return this; }
    /// <summary>Own goals this side concedes; they have no shot entry and count for the opponent's scoreboard.</summary>
    public SideBuilder OwnGoals(int n) { _ownGoals = n; return this; }
    public SideBuilder Division(int d) { _division = d; return this; }
    /// <summary>A side that quit before any stats existed: the API sends no players and null numbers.</summary>
    public SideBuilder NoStats() { _noStats = true; return Forfeit(); }
    private bool _noStats;
    public SideBuilder Passes(int total, int through) { _passTry = total; _throughPass = through; return this; }
    public SideBuilder Player(int spId, int position = 25) { _players.Add(new MatchPlayer { SpId = spId, SpPosition = position, SpGrade = 5 }); return this; }

    /// <summary>Adds a goal. Coordinates default to the centre of the box.</summary>
    public SideBuilder Goal(int spId, int type = ShotTypes.Normal, double x = 0.9, double y = 0.5, int minute = 30, int? assist = null,
        double assistX = 0.7, double assistY = 0.5)
    {
        _shots.Add(Shot(spId, type, x, y, minute, 3, assist, assistX, assistY));
        return this;
    }

    public SideBuilder Miss(int spId, int type = ShotTypes.Normal, double x = 0.8, double y = 0.5, int minute = 20)
    {
        _shots.Add(Shot(spId, type, x, y, minute, 2, null, 0, 0));
        return this;
    }

    private static ShootDetail Shot(int spId, int type, double x, double y, int minute, int result, int? assist, double ax, double ay) => new()
    {
        GoalTime = EncodeMinute(minute),
        X = x,
        Y = y,
        Type = type,
        Result = result,
        SpId = spId,
        SpGrade = 5,
        Assist = assist is not null,
        AssistSpIdRaw = assist ?? -1,
        AssistX = ax,
        AssistY = ay,
        InPenalty = Pitch.IsInBox(x, y),
    };

    public static long EncodeMinute(int minute) =>
        minute < 45 ? minute * 60L : (1L << 24) + (minute - 45) * 60L;

    public MatchInfo Build()
    {
        var goals = _shots.Count(s => s.IsGoal);
        var players = _players.Count > 0 ? _players : _shots.Select(s => s.SpId).Distinct()
            .Select(id => new MatchPlayer { SpId = id, SpPosition = 25, SpGrade = 5 }).ToList();
        // Real sides always list their squad; only a side without stats has none.
        if (players.Count == 0) players = [new MatchPlayer { SpId = 1, SpPosition = 0, SpGrade = 1 }];
        if (_noStats)
        {
            return new MatchInfo
            {
                Ouid = ouid, Nickname = _nickname, Division = _division,
                MatchDetail = new MatchSideDetail { MatchEndType = 2 },
            };
        }
        return new MatchInfo
        {
            Ouid = ouid,
            Nickname = _nickname,
            Division = _division,
            MatchDetail = new MatchSideDetail
            {
                Possession = _possession,
                Controller = _controller,
                SystemPause = _pause,
                MatchEndType = ForfeitLoss ? 2 : 0,
            },
            Shoot = new ShootSummary
            {
                ShootTotal = _shots.Count,
                EffectiveShootTotal = _shots.Count(s => s.IsOnTarget),
                GoalTotal = goals,
                OwnGoal = _ownGoals,
            },
            ShootDetail = [.. _shots],
            Pass = new PassSummary { PassTry = _passTry, PassSuccess = _passTry * 8 / 10, ThroughPassTry = _throughPass, ShortPassTry = _passTry - _throughPass },
            Defence = new DefenceSummary { TackleTry = 10, TackleSuccess = 4 },
            Player = players,
        };
    }
}
