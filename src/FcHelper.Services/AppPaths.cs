namespace FcHelper.Services;

/// <summary>Where the app keeps its files: %LOCALAPPDATA%\FcHelper on Windows. The CLI and the app share the same database.</summary>
public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("FCH_DATA_DIR");
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FcHelper");
            }
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DatabasePath => Path.Combine(DataDirectory, "fchelper.db");
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Recognition screenshots, written only when the (off by default) debug option is on.</summary>
    public static string CapturesDirectory
    {
        get
        {
            var dir = Path.Combine(DataDirectory, "captures");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
