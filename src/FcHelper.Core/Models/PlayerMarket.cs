namespace FcHelper.Core.Models;

/// <summary>
/// A player's card as the official FC Online data center shows it (not part of the Open API): overall at the
/// given enhancement level, main position, and the current market price as displayed there (no unit is shown).
/// </summary>
public sealed record PlayerMarket(int SpId, int Strong, string Name, int Ovr, string Position, string? Price, DateTime FetchedAt);
