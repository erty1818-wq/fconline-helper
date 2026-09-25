using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FcHelper.Services;

namespace FcHelper.App;

/// <summary>A newer version on the release page: its version, notes and the exe to download.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Notes, string DownloadUrl, long Size, string? Sha256);

/// <summary>
/// One-touch and automatic updates from the GitHub release page (<see cref="Repository"/>). The newest release's
/// FcHelper.exe is downloaded to %LOCALAPPDATA%\FcHelper\update, checked (size, SHA-256 when GitHub gives one) and swapped
/// in: Windows lets a running exe be renamed, so the old one becomes FcHelper.old.exe (deleted on the next start), the
/// new one takes its name and starts. One request at start-up and every six hours; nothing else runs in between.
/// </summary>
public sealed class Updater(HttpClient http)
{
    /// <summary>The public repository whose releases carry FcHelper.exe.</summary>
    public const string Repository = "erty1818-wq/fconline-helper";
    private const string AssetName = "FcHelper.exe";

    public static Version Current { get; } =
        Version.TryParse((Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0], out var v)
            ? v : new Version(0, 0, 0);

    public UpdateInfo? Available { get; private set; }
    public event Action<UpdateInfo>? Found;

    private static string Folder
    {
        get
        {
            var dir = Path.Combine(AppPaths.DataDirectory, "update");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string Pending => Path.Combine(Folder, "FcHelper.new.exe");
    private static string PendingInfo => Path.Combine(Folder, "pending.json");

    private sealed record Release([property: JsonPropertyName("tag_name")] string Tag, [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("draft")] bool Draft, [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] List<Asset> Assets);

    private sealed record Asset([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("browser_download_url")] string Url,
        [property: JsonPropertyName("size")] long Size, [property: JsonPropertyName("digest")] string? Digest);

    /// <summary>Asks the release page once; raises <see cref="Found"/> when it holds a newer version with an exe.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
            req.Headers.UserAgent.ParseAdd($"FcHelper/{Current}");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            var release = JsonSerializer.Deserialize<Release>(await res.Content.ReadAsStringAsync(ct));
            if (release is null || release.Draft || release.Prerelease) return null;
            if (!Version.TryParse(release.Tag.TrimStart('v', 'V'), out var version) || version <= Current) return null;
            if (release.Assets.FirstOrDefault(a => a.Name.Equals(AssetName, StringComparison.OrdinalIgnoreCase)) is not { } asset) return null;
            var sha = asset.Digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d[7..] : null;
            Available = new UpdateInfo(version, release.Tag, release.Body ?? "", asset.Url, asset.Size, sha);
            Found?.Invoke(Available);
            return Available;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null; // offline or GitHub down: try again later
        }
    }

    /// <summary>Downloads the update (once) and checks it; true when <see cref="Pending"/> holds a good copy.</summary>
    public async Task<bool> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (IsPending(info)) return true;
        var part = Pending + ".part";
        using (var res = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            await using var source = await res.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(part);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (info.Size > 0) progress?.Report(done / (double)info.Size);
            }
        }
        if (!Verify(part, info))
        {
            File.Delete(part);
            return false;
        }
        File.Move(part, Pending, overwrite: true);
        await File.WriteAllTextAsync(PendingInfo, JsonSerializer.Serialize(new { info.Tag, info.Size, info.Sha256 }), ct);
        return true;
    }

    private static bool Verify(string file, UpdateInfo info)
    {
        if (info.Size > 0 && new FileInfo(file).Length != info.Size) return false;
        if (info.Sha256 is null) return true;
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(info.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPending(UpdateInfo info)
    {
        try
        {
            return File.Exists(Pending) && File.Exists(PendingInfo) && File.ReadAllText(PendingInfo).Contains(info.Tag, StringComparison.Ordinal) && Verify(Pending, info);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Puts the downloaded exe in place of the running one and starts it (the caller then shuts down). False when the
    /// app does not run from a published exe (e.g. from the IDE) or the folder cannot be written.
    /// </summary>
    public static bool ApplyAndRestart()
    {
        var exe = Environment.ProcessPath;
        if (exe is null || !File.Exists(Pending) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        var old = Path.ChangeExtension(exe, ".old.exe");
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try
            {
                File.Copy(Pending, exe);
            }
            catch
            {
                File.Move(old, exe); // put the running version back
                throw;
            }
            File.Delete(Pending);
            File.Delete(PendingInfo);
            Process.Start(new ProcessStartInfo(exe, "--after-update") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>On start-up: deletes the exe an update replaced, and says whether a newer downloaded version is waiting.</summary>
    public static bool Cleanup()
    {
        if (Environment.ProcessPath is { } exe && Path.ChangeExtension(exe, ".old.exe") is var old && File.Exists(old))
        {
            try { File.Delete(old); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* still locked: next time */ }
        }
        try
        {
            if (!File.Exists(Pending) || !File.Exists(PendingInfo)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(PendingInfo));
            var tag = doc.RootElement.GetProperty("Tag").GetString() ?? "";
            if (Version.TryParse(tag.TrimStart('v', 'V'), out var v) && v > Current) return true;
            File.Delete(Pending); // already installed (or older): drop it
            File.Delete(PendingInfo);
            return false;
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
