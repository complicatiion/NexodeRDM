using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Nexode.Platform;

/// <summary>
/// Start with the operating system (current user only): Windows registry "Run" key, Linux XDG autostart entry,
/// macOS LaunchAgent. The program is started with "--autostart".
/// </summary>
public static class Autostart
{
    private const string Name = "Nexode RDM";
    private const string LinuxFile = "nexoderdm.desktop";
    private const string MacLabel = "de.sksdesign.nexoderdm";

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string LinuxPath => Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".config"), "autostart", LinuxFile);

    private static string MacPath => Path.Combine(Home, "Library", "LaunchAgents", MacLabel + ".plist");

    public static bool IsEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsValue() is not null;
            if (OperatingSystem.IsLinux()) return File.Exists(LinuxPath);
            if (OperatingSystem.IsMacOS()) return File.Exists(MacPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        return false;
    }

    /// <summary>Turns the autostart on or off for <paramref name="executable"/>. False when it could not be changed.</summary>
    public static bool Set(bool enabled, string executable)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                SetWindows(enabled, executable);
            }
            else if (OperatingSystem.IsLinux())
            {
                if (enabled)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LinuxPath)!);
                    File.WriteAllText(LinuxPath, LinuxEntry(executable));
                }
                else if (File.Exists(LinuxPath))
                {
                    File.Delete(LinuxPath);
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (enabled)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(MacPath)!);
                    File.WriteAllText(MacPath, MacPlist(executable));
                }
                else if (File.Exists(MacPath))
                {
                    File.Delete(MacPath);
                }
            }
            else
            {
                return false;
            }

            return IsEnabled() == enabled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static string LinuxEntry(string executable) => $"""
        [Desktop Entry]
        Type=Application
        Name={Name}
        Exec="{executable}" --autostart
        Icon=nexode
        Terminal=false
        X-GNOME-Autostart-enabled=true
        """.Replace("\r", "", StringComparison.Ordinal) + "\n";

    public static string MacPlist(string executable)
    {
        // Inside an .app bundle the bundle is opened (keeps the Dock icon and the bundle identity).
        var app = executable.Contains(".app/Contents/MacOS/", StringComparison.Ordinal) ? executable[..(executable.IndexOf(".app/", StringComparison.Ordinal) + 4)] : null;
        string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? s;
        var args = app is not null
            ? $"<string>/usr/bin/open</string><string>-a</string><string>{Esc(app)}</string><string>--args</string><string>--autostart</string>"
            : $"<string>{Esc(executable)}</string><string>--autostart</string>";
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{MacLabel}</string>
              <key>ProgramArguments</key><array>{args}</array>
              <key>RunAtLoad</key><true/>
            </dict>
            </plist>
            """;
    }

    [SupportedOSPlatform("windows")]
    private static string? WindowsValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue(Name) as string;
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindows(bool enabled, string executable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue(Name, $"\"{executable}\" --autostart");
        else key.DeleteValue(Name, false);
    }
}
