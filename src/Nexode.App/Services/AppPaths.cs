namespace Nexode.App.Services;

/// <summary>
/// Data locations. Portable mode (file "portable.mode" or "nexode.portable" next to the app): everything in ./data,
/// and Nexode changes nothing on the system (no autostart, no file type registration).
/// Otherwise: %AppData%\Nexode (Windows), ~/Library/Application Support/Nexode (macOS), $XDG_DATA_HOME/Nexode (Linux).
/// </summary>
public static class AppPaths
{
    public static string AppDirectory { get; } = AppContext.BaseDirectory;

    public static bool IsPortable { get; } =
        File.Exists(Path.Combine(AppDirectory, "portable.mode")) || File.Exists(Path.Combine(AppDirectory, "nexode.portable"));

    public static string DataDirectory { get; } = ResolveDataDirectory();

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string SourcesDirectory => Ensure(Path.Combine(DataDirectory, "sources"));
    public static string BackupDirectory => Ensure(Path.Combine(DataDirectory, "backups"));
    /// <summary>Local working copies of shared (team) data sources.</summary>
    public static string CacheDirectory => Ensure(Path.Combine(DataDirectory, "cache"));
    /// <summary>Personal state (favorites, pins, usage) for shared data sources.</summary>
    public static string StateDirectory => Ensure(Path.Combine(DataDirectory, "state"));
    public static string KeyDirectory => Ensure(Path.Combine(DataDirectory, "keys"));
    public static string LogDirectory => Ensure(Path.Combine(DataDirectory, "logs"));
    public static string LicensesDirectory => Path.Combine(AppDirectory, "licenses");

    private static string ResolveDataDirectory()
    {
        if (Environment.GetEnvironmentVariable("NEXODE_DATA") is { Length: > 0 } env) return Ensure(env);
        if (IsPortable) return Ensure(Path.Combine(AppDirectory, "data"));

        string root;
        if (OperatingSystem.IsWindows())
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }
        else if (OperatingSystem.IsMacOS())
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
        }
        else
        {
            root = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Ensure(Path.Combine(root, "Nexode"));
    }

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
