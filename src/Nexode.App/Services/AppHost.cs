using System.Reflection;
using Nexode.Platform;

namespace Nexode.App.Services;

/// <summary>Process-wide services shared by all windows.</summary>
public static class AppHost
{
    public static AppSettings Settings { get; set; } = new();
    public static ThemeService Theme { get; set; } = null!;
    public static Workspace Workspace { get; set; } = null!;
    public static WindowManager Windows { get; } = new();
    public static SecureClipboard Clipboard { get; } = new();
    public static SingleInstance? Instance { get; set; }
    public static Nexode.Protocols.KnownHosts KnownHosts { get; } = new(Path.Combine(AppPaths.DataDirectory, "known_hosts.json"));

    public static string Version { get; } =
        typeof(AppHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.8";

    public const string ProductName = "Nexode RDM";
    public const string AppName = "Nexode RDM";
    public const string RepositoryUrl = "https://github.com/complicatiion/NexodeRDM";
    public const string WebsiteUrl = "https://sksdesign.de";
}
