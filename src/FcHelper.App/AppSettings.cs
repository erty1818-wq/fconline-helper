using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FcHelper.Services;
using Microsoft.Win32;

namespace FcHelper.App;

/// <summary>How the opponent is found (docs/PLANNING.md 14, v0.5).</summary>
public enum DetectionMode
{
    /// <summary>Ctrl+Alt+F on the matchmaking screen reads the opponent's nickname once.</summary>
    Hotkey,
    /// <summary>Only the search window (Ctrl+Alt+S).</summary>
    Manual,
    /// <summary>Watches the game window while it is in front: opponent card on a match, end card after it (docs/auto-mode).</summary>
    Auto,
}

public sealed class AppSettings
{
    /// <summary>The API key encrypted with Windows DPAPI for the current user; never stored in plain text.</summary>
    public string? ProtectedApiKey { get; set; }
    public string? MyNickname { get; set; }
    public double RequestsPerSecond { get; set; } = 5;
    public int MatchWindow { get; set; } = 30;
    public bool VoiceBriefing { get; set; }
    public bool StartWithWindows { get; set; }
    public DetectionMode Detection { get; set; } = DetectionMode.Hotkey;
    /// <summary>Debug aid: keep each recognition screenshot and its OCR text. Off by default (PLANNING 19.5).</summary>
    public bool SaveCaptures { get; set; }
    /// <summary>Overall and price of the dangerous players from the official data center (personal use).</summary>
    public bool ShowMarket { get; set; } = true;
    /// <summary>Refresh value-finder prices daily and new seasons at once, in the background, never during a game.</summary>
    public bool MarketAutoRefresh { get; set; } = true;
    /// <summary>The API key guide opens by itself once, on the first run without a key; later from its button.</summary>
    public bool ApiGuideShown { get; set; }
    /// <summary>Download new versions in the background and switch to them on the next start (the notice still shows).</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Which sections the 매칭 카드 shows besides the basics (keys of <see cref="MatchCardSections"/>).</summary>
    public List<string> CardSections { get; set; } = [.. MatchCardSections.Defaults];
    /// <summary>The 구단주 검색 window's size as the user left it (null = the default card size).</summary>
    public double? SearchWidth { get; set; }
    public double? SearchHeight { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly byte[] Entropy = "FcHelper.ApiKey.v1"u8.ToArray();

    public string? ApiKey
    {
        get
        {
            if (string.IsNullOrEmpty(ProtectedApiKey)) return null;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedApiKey), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception e) when (e is CryptographicException or FormatException)
            {
                // Settings copied from another Windows account cannot be decrypted; ask for the key again.
                return null;
            }
        }
        set => ProtectedApiKey = string.IsNullOrWhiteSpace(value)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), Entropy, DataProtectionScope.CurrentUser));
    }

    public FcHelperOptions ToOptions() => new()
    {
        MyNickname = string.IsNullOrWhiteSpace(MyNickname) ? null : MyNickname.Trim(),
        MatchWindow = Math.Clamp(MatchWindow, 10, 100),
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsPath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsPath), JsonOptions) ?? new();
            }
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
        }
        return new AppSettings();
    }

    public void Save()
    {
        File.WriteAllText(AppPaths.SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        ApplyStartWithWindows();
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private void ApplyStartWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;
        if (StartWithWindows && Environment.ProcessPath is { } exe) key.SetValue("FcHelper", $"\"{exe}\" --tray");
        else key.DeleteValue("FcHelper", throwOnMissingValue: false);
    }
}
