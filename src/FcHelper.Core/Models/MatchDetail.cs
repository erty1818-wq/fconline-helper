using System.Text.Json.Serialization;

namespace FcHelper.Core.Models;

// Shapes of the NEXON Open API FC Online `match-detail` response.
// Field names follow the API; see docs/PLANNING.md section 3 for the verification status of each field.

public sealed record MatchDetail
{
    [JsonPropertyName("matchId")] public string MatchId { get; init; } = "";
    [JsonPropertyName("matchDate")] public DateTime MatchDate { get; init; }
    [JsonPropertyName("matchType")] public int MatchType { get; init; }
    [JsonPropertyName("matchInfo")] public List<MatchInfo> MatchInfo { get; init; } = [];

    public MatchInfo? SideOf(string ouid) => MatchInfo.FirstOrDefault(m => m.Ouid == ouid);

    public MatchInfo? OpponentOf(string ouid) => MatchInfo.FirstOrDefault(m => m.Ouid != ouid);
}

public sealed record MatchInfo
{
    [JsonPropertyName("ouid")] public string Ouid { get; init; } = "";
    [JsonPropertyName("nickname")] public string Nickname { get; init; } = "";
    [JsonPropertyName("matchDetail")] public MatchSideDetail MatchDetail { get; init; } = new();
    [JsonPropertyName("shoot")] public ShootSummary Shoot { get; init; } = new();
    [JsonPropertyName("shootDetail")] public List<ShootDetail> ShootDetail { get; init; } = [];
    [JsonPropertyName("pass")] public PassSummary Pass { get; init; } = new();
    [JsonPropertyName("defence")] public DefenceSummary Defence { get; init; } = new();
    [JsonPropertyName("player")] public List<MatchPlayer> Player { get; init; } = [];
}

public sealed record MatchSideDetail
{
    [JsonPropertyName("seasonId")] public int SeasonId { get; init; }
    /// <summary>"승" | "무" | "패"</summary>
    [JsonPropertyName("matchResult")] public string MatchResult { get; init; } = "";
    /// <summary>0: 정상 종료, 1: 몰수승, 2: 몰수패</summary>
    [JsonPropertyName("matchEndType")] public int MatchEndType { get; init; }
    [JsonPropertyName("systemPause")] public int SystemPause { get; init; }
    [JsonPropertyName("foul")] public int Foul { get; init; }
    [JsonPropertyName("injury")] public int Injury { get; init; }
    [JsonPropertyName("redCards")] public int RedCards { get; init; }
    [JsonPropertyName("yellowCards")] public int YellowCards { get; init; }
    [JsonPropertyName("dribble")] public double Dribble { get; init; }
    [JsonPropertyName("cornerKick")] public int CornerKick { get; init; }
    [JsonPropertyName("possession")] public int Possession { get; init; }
    [JsonPropertyName("OffsideCount")] public int OffsideCount { get; init; }
    [JsonPropertyName("averageRating")] public double AverageRating { get; init; }
    /// <summary>"keyboard" | "pad" 등</summary>
    [JsonPropertyName("controller")] public string Controller { get; init; } = "";

    [JsonIgnore] public MatchOutcome Outcome => MatchResult switch
    {
        "승" => MatchOutcome.Win,
        "무" => MatchOutcome.Draw,
        "패" => MatchOutcome.Loss,
        _ => MatchOutcome.Unknown,
    };

    [JsonIgnore] public bool IsForfeit => MatchEndType is 1 or 2;
}

public enum MatchOutcome { Unknown, Win, Draw, Loss }

public sealed record ShootSummary
{
    [JsonPropertyName("shootTotal")] public int ShootTotal { get; init; }
    [JsonPropertyName("effectiveShootTotal")] public int EffectiveShootTotal { get; init; }
    [JsonPropertyName("shootOutScore")] public int ShootOutScore { get; init; }
    [JsonPropertyName("goalTotal")] public int GoalTotal { get; init; }
    [JsonPropertyName("goalTotalDisplay")] public int GoalTotalDisplay { get; init; }
    [JsonPropertyName("ownGoal")] public int OwnGoal { get; init; }
    [JsonPropertyName("shootHeading")] public int ShootHeading { get; init; }
    [JsonPropertyName("goalHeading")] public int GoalHeading { get; init; }
    [JsonPropertyName("shootFreekick")] public int ShootFreekick { get; init; }
    [JsonPropertyName("goalFreekick")] public int GoalFreekick { get; init; }
    [JsonPropertyName("shootInPenalty")] public int ShootInPenalty { get; init; }
    [JsonPropertyName("goalInPenalty")] public int GoalInPenalty { get; init; }
    [JsonPropertyName("shootOutPenalty")] public int ShootOutPenalty { get; init; }
    [JsonPropertyName("goalOutPenalty")] public int GoalOutPenalty { get; init; }
    [JsonPropertyName("shootPenaltyKick")] public int ShootPenaltyKick { get; init; }
    [JsonPropertyName("goalPenaltyKick")] public int GoalPenaltyKick { get; init; }
}

public sealed record ShootDetail
{
    /// <summary>Encoded time, see <see cref="GoalTime"/>.</summary>
    [JsonPropertyName("goalTime")] public long GoalTime { get; init; }
    [JsonPropertyName("x")] public double X { get; init; }
    [JsonPropertyName("y")] public double Y { get; init; }
    [JsonPropertyName("type")] public int Type { get; init; }
    /// <summary>1: 유효슛, 2: 빗나감, 3: 골</summary>
    [JsonPropertyName("result")] public int Result { get; init; }
    [JsonPropertyName("spId")] public int SpId { get; init; }
    [JsonPropertyName("spGrade")] public int SpGrade { get; init; }
    [JsonPropertyName("spLevel")] public int SpLevel { get; init; }
    [JsonPropertyName("spIdType")] public bool SpIdType { get; init; }
    [JsonPropertyName("assist")] public bool Assist { get; init; }
    // Documentation sources disagree on this name ("assistSpId" vs "assistSpI"); accept both.
    [JsonPropertyName("assistSpId")] public int? AssistSpIdRaw { get; init; }
    [JsonPropertyName("assistSpI")] public int? AssistSpIRaw { get; init; }
    [JsonPropertyName("assistX")] public double AssistX { get; init; }
    [JsonPropertyName("assistY")] public double AssistY { get; init; }
    [JsonPropertyName("hitPost")] public bool HitPost { get; init; }
    [JsonPropertyName("inPenalty")] public bool InPenalty { get; init; }

    [JsonIgnore] public bool IsGoal => Result == 3;
    [JsonIgnore] public bool IsOnTarget => Result is 1 or 3;

    /// <summary>Assisting player's spId, or null when the shot was unassisted.</summary>
    [JsonIgnore] public int? AssistSpId
    {
        get
        {
            var id = AssistSpIdRaw ?? AssistSpIRaw;
            return Assist && id is > 0 ? id : null;
        }
    }

    [JsonIgnore] public int Seconds => Core.GoalTime.ToSeconds(GoalTime);
}

public sealed record PassSummary
{
    [JsonPropertyName("passTry")] public int PassTry { get; init; }
    [JsonPropertyName("passSuccess")] public int PassSuccess { get; init; }
    [JsonPropertyName("shortPassTry")] public int ShortPassTry { get; init; }
    [JsonPropertyName("shortPassSuccess")] public int ShortPassSuccess { get; init; }
    [JsonPropertyName("longPassTry")] public int LongPassTry { get; init; }
    [JsonPropertyName("longPassSuccess")] public int LongPassSuccess { get; init; }
    [JsonPropertyName("bouncingLobPassTry")] public int BouncingLobPassTry { get; init; }
    [JsonPropertyName("bouncingLobPassSuccess")] public int BouncingLobPassSuccess { get; init; }
    [JsonPropertyName("drivenGroundPassTry")] public int DrivenGroundPassTry { get; init; }
    [JsonPropertyName("drivenGroundPassSuccess")] public int DrivenGroundPassSuccess { get; init; }
    [JsonPropertyName("throughPassTry")] public int ThroughPassTry { get; init; }
    [JsonPropertyName("throughPassSuccess")] public int ThroughPassSuccess { get; init; }
    [JsonPropertyName("lobbedThroughPassTry")] public int LobbedThroughPassTry { get; init; }
    [JsonPropertyName("lobbedThroughPassSuccess")] public int LobbedThroughPassSuccess { get; init; }
}

public sealed record DefenceSummary
{
    [JsonPropertyName("blockTry")] public int BlockTry { get; init; }
    [JsonPropertyName("blockSuccess")] public int BlockSuccess { get; init; }
    [JsonPropertyName("tackleTry")] public int TackleTry { get; init; }
    [JsonPropertyName("tackleSuccess")] public int TackleSuccess { get; init; }
}

public sealed record MatchPlayer
{
    [JsonPropertyName("spId")] public int SpId { get; init; }
    [JsonPropertyName("spPosition")] public int SpPosition { get; init; }
    [JsonPropertyName("spGrade")] public int SpGrade { get; init; }
    [JsonPropertyName("status")] public PlayerStatus Status { get; init; } = new();

    /// <summary>spPosition 28 is the substitutes' bench.</summary>
    [JsonIgnore] public bool IsSubstitute => SpPosition == 28;
}

public sealed record PlayerStatus
{
    [JsonPropertyName("shoot")] public int Shoot { get; init; }
    [JsonPropertyName("effectiveShoot")] public int EffectiveShoot { get; init; }
    [JsonPropertyName("assist")] public int Assist { get; init; }
    [JsonPropertyName("goal")] public int Goal { get; init; }
    [JsonPropertyName("dribble")] public double Dribble { get; init; }
    [JsonPropertyName("intercept")] public int Intercept { get; init; }
    [JsonPropertyName("defending")] public int Defending { get; init; }
    [JsonPropertyName("passTry")] public int PassTry { get; init; }
    [JsonPropertyName("passSuccess")] public int PassSuccess { get; init; }
    [JsonPropertyName("dribbleTry")] public int DribbleTry { get; init; }
    [JsonPropertyName("dribbleSuccess")] public int DribbleSuccess { get; init; }
    [JsonPropertyName("ballPossesionTry")] public int BallPossesionTry { get; init; }
    [JsonPropertyName("ballPossesionSuc")] public int BallPossesionSuc { get; init; }
    [JsonPropertyName("aerialTry")] public int AerialTry { get; init; }
    [JsonPropertyName("aerialSuccess")] public int AerialSuccess { get; init; }
    [JsonPropertyName("blockTry")] public int BlockTry { get; init; }
    [JsonPropertyName("block")] public int Block { get; init; }
    [JsonPropertyName("tackleTry")] public int TackleTry { get; init; }
    [JsonPropertyName("tackle")] public int Tackle { get; init; }
    [JsonPropertyName("yellowCards")] public int YellowCards { get; init; }
    [JsonPropertyName("redCards")] public int RedCards { get; init; }
    [JsonPropertyName("spRating")] public double SpRating { get; init; }
}
