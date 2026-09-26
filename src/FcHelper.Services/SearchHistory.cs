using System.Text.Json;
using FcHelper.Data;

namespace FcHelper.Services;

/// <summary>
/// The 구단주 검색 start screen's two lists, kept in the DB (kv): nicknames searched recently (newest first)
/// and nicknames the user starred. Nicknames compare case-insensitively, like the users table.
/// </summary>
public sealed class SearchHistory(FcDatabase db)
{
    public const string RecentKey = "search.recent";
    public const string FavoritesKey = "search.favorites";
    public const int MaxRecent = 10;
    public const int MaxFavorites = 30;

    public IReadOnlyList<string> Recent => Read(RecentKey);
    public IReadOnlyList<string> Favorites => Read(FavoritesKey);

    /// <summary>Puts <paramref name="nickname"/> at the front of the recent list (once), dropping the oldest past <see cref="MaxRecent"/>.</summary>
    public void AddRecent(string nickname)
    {
        var n = nickname.Trim();
        if (n.Length == 0) return;
        Write(RecentKey, [n, .. Read(RecentKey).Where(x => !Same(x, n))], MaxRecent);
    }

    public void RemoveRecent(string nickname) => Write(RecentKey, Read(RecentKey).Where(x => !Same(x, nickname)), MaxRecent);

    public bool IsFavorite(string nickname) => Read(FavoritesKey).Any(x => Same(x, nickname));

    /// <summary>Stars or unstars <paramref name="nickname"/>; returns true when it is now a favourite.</summary>
    public bool ToggleFavorite(string nickname)
    {
        var n = nickname.Trim();
        if (n.Length == 0) return false;
        var list = Read(FavoritesKey);
        if (list.Any(x => Same(x, n)))
        {
            Write(FavoritesKey, list.Where(x => !Same(x, n)), MaxFavorites);
            return false;
        }
        Write(FavoritesKey, [n, .. list], MaxFavorites);
        return true;
    }

    private static bool Same(string a, string b) => FcDatabase.NicknameKey(a) == FcDatabase.NicknameKey(b);

    private List<string> Read(string key)
    {
        try { return db.GetValue(key) is { } v ? JsonSerializer.Deserialize<List<string>>(v.Value) ?? [] : []; }
        catch (JsonException) { return []; }
    }

    private void Write(string key, IEnumerable<string> list, int max) => db.SetValue(key, JsonSerializer.Serialize(list.Take(max).ToList()));
}
