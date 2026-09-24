using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FcHelper.Services;
using Microsoft.Win32;

namespace FcHelper.App;

public sealed class AppSettings
{
    /// <summary>The API key encrypted with Windows DPAPI for the current user; never stored in plain text.</summary>
    public string? ProtectedApiKey { get; set; }
    public string? MyNickname { get; set; }
    public double RequestsPerSecond { get; set; } = 5;
    public int MatchWindow { get; set; } = 30;
    public bool VoiceBriefing { get; set; }
    public bool StartWithWindows { get; set; }
    public bool KeepCardOnTop { get; set; } = true;

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
