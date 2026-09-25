using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FcHelper.Market;
using FcHelper.Services;

namespace FcHelper.App.Studio;

/// <summary>
/// Mini face pictures (미니페이스온) on the pitch. Each card shows its own season's picture unless the user picked
/// another one for it: any season's picture of the same footballer (the list the official squad maker offers), an
/// image file of their own, or none. Pictures are downloaded once from the official CDN into
/// %LOCALAPPDATA%\FcHelper\faces and read from there afterwards.
/// </summary>
public static class Faces
{
    private const string ChoiceKey = "face.choices";
    private const string EnabledKey = "face.enabled";
    private const string None = "none";
    private static readonly SemaphoreSlim Downloads = new(3, 3);
    private static readonly Dictionary<string, ImageSource?> Images = [];
    private static readonly Dictionary<string, Task<ImageSource?>> Pending = [];
    private static Dictionary<long, string>? _choices;

    /// <summary>A picture was picked or loaded: the pitch redraws.</summary>
    public static event Action? Changed;

    private static string Folder(string sub = "")
    {
        var dir = Path.Combine(AppPaths.DataDirectory, "faces", sub);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static bool Enabled
    {
        get => StudioKit.App.Db?.GetValue(EnabledKey)?.Value != "0";
        set
        {
            StudioKit.App.Db?.SetValue(EnabledKey, value ? "1" : "0");
            Changed?.Invoke();
        }
    }

    private static Dictionary<long, string> Choices()
    {
        if (_choices is not null) return _choices;
        try
        {
            _choices = StudioKit.App.Db?.GetValue(ChoiceKey) is { } v ? JsonSerializer.Deserialize<Dictionary<long, string>>(v.Value) ?? [] : [];
        }
        catch (JsonException)
        {
            _choices = [];
        }
        return _choices;
    }

    /// <summary>The user's pick for a card: a picture URL, a local file, "none", or null for the card's own picture.</summary>
    public static string? Choice(long spId) => Choices().GetValueOrDefault(spId);

    public static void Pick(long spId, string? choice)
    {
        var choices = Choices();
        if (choice is null) choices.Remove(spId);
        else choices[spId] = choice;
        StudioKit.App.Db?.SetValue(ChoiceKey, JsonSerializer.Serialize(choices));
        Changed?.Invoke();
    }

    public static void PickNone(long spId) => Pick(spId, None);

    /// <summary>Copies the user's own picture next to the others (the original may move) and picks it for the card.</summary>
    public static void PickFile(long spId, string file)
    {
        var target = Path.Combine(Folder("custom"), $"{spId}{Path.GetExtension(file).ToLowerInvariant()}");
        File.Copy(file, target, overwrite: true);
        lock (Images) Images.Remove(target);
        Pick(spId, target);
    }

    /// <summary>The picture for a card when it is already loaded (the pitch draws it right away), else null.</summary>
    public static ImageSource? Cached(long spId)
    {
        if (!Enabled || Choice(spId) == None) return null;
        var key = Choice(spId) ?? FaceUrls.Default(spId);
        lock (Images) return Images.GetValueOrDefault(key);
    }

    /// <summary>The picture for a card: the user's pick, else its season's action picture, else the footballer's head shot.</summary>
    public static async Task<ImageSource?> GetAsync(long spId)
    {
        if (!Enabled) return null;
        switch (Choice(spId))
        {
            case None:
                return null;
            case { } picked:
                return await LoadAsync(picked);
            default:
                var own = await LoadAsync(FaceUrls.Default(spId));
                if (own is not null) return own;
                var head = await LoadAsync(FaceUrls.Of(spId, 4));
                lock (Images) Images[FaceUrls.Default(spId)] = head; // next time straight from memory
                return head;
        }
    }

    /// <summary>A picture by URL (downloaded once) or local path; null when it does not exist (some are withdrawn for licence reasons).</summary>
    public static Task<ImageSource?> LoadAsync(string source)
    {
        lock (Images)
        {
            if (Images.TryGetValue(source, out var known)) return Task.FromResult(known);
            if (Pending.TryGetValue(source, out var running)) return running;
            var task = LoadCoreAsync(source);
            Pending[source] = task;
            return task;
        }
    }

    private static async Task<ImageSource?> LoadCoreAsync(string source)
    {
        ImageSource? image = null;
        try
        {
            var file = source;
            if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // Player pictures keep their old cache names; other pictures (season icons …) go by their path.
                var common = source.IndexOf("/common/", StringComparison.Ordinal);
                var assets = source.IndexOf("externalAssets/", StringComparison.OrdinalIgnoreCase);
                var name = (common >= 0 ? source[(common + 8)..] : assets >= 0 ? source[(assets + 15)..]
                    : Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(source))) + ".png").Replace('/', '_');
                file = Path.Combine(Folder("cdn"), name);
                var missing = file + ".missing";
                if (File.Exists(missing) && DateTime.UtcNow - File.GetLastWriteTimeUtc(missing) < TimeSpan.FromDays(7)) file = "";
                else if (!File.Exists(file))
                {
                    await Downloads.WaitAsync();
                    try
                    {
                        using var res = await StudioKit.App.Http.GetAsync(source);
                        if (res.IsSuccessStatusCode) await File.WriteAllBytesAsync(file, await res.Content.ReadAsByteArrayAsync());
                        else
                        {
                            await File.WriteAllTextAsync(missing, ((int)res.StatusCode).ToString());
                            file = "";
                        }
                    }
                    finally
                    {
                        Downloads.Release();
                    }
                }
            }
            if (file.Length > 0 && File.Exists(file)) image = Read(file);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            image = null;
        }
        lock (Images)
        {
            Images[source] = image;
            Pending.Remove(source);
        }
        return image;
    }

    /// <summary>Decoded small (the chips are ~70 px high) and frozen so any thread can use it; the file is not kept open.</summary>
    private static BitmapImage? Read(string file)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelHeight = 180;
            bitmap.UriSource = new Uri(file);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
