using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Nexode.Platform;

/// <summary>Registers the .nexode file type for the current user (Windows registry, Linux shared-mime-info).</summary>
public static partial class FileAssociation
{
    public const string Extension = ".nexode";
    private const string ProgId = "Nexode.File";
    private const string MimeType = "application/x-nexode";

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public static bool IsRegistered(string exePath)
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
            return key?.GetValue(null) is string cmd && cmd.Contains(exePath, StringComparison.OrdinalIgnoreCase);
        }

        if (OperatingSystem.IsLinux())
        {
            return File.Exists(Path.Combine(DataHome(), "applications", "nexode.desktop"));
        }

        return false;
    }

    public static void Register(string exePath, string iconPath, string description)
    {
        if (OperatingSystem.IsWindows())
        {
            using (var ext = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Extension}"))
            {
                ext.SetValue(null, ProgId);
                ext.SetValue("Content Type", MimeType);
            }

            using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                prog.SetValue(null, description);
                using var icon = prog.CreateSubKey("DefaultIcon");
                icon.SetValue(null, $"\"{iconPath}\",0");
                using var cmd = prog.CreateSubKey(@"shell\open\command");
                cmd.SetValue(null, $"\"{exePath}\" \"%1\"");
            }

            SHChangeNotify(0x08000000, 0, 0, 0);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            var data = DataHome();
            var apps = Path.Combine(data, "applications");
            var mime = Path.Combine(data, "mime", "packages");
            Directory.CreateDirectory(apps);
            Directory.CreateDirectory(mime);
            File.WriteAllText(Path.Combine(apps, "nexode.desktop"), $"""
                [Desktop Entry]
                Type=Application
                Name=Nexode RDM
                Comment={description}
                Exec="{exePath}" %f
                Icon={iconPath}
                Terminal=false
                Categories=Network;RemoteAccess;
                MimeType={MimeType};

                """);
            File.WriteAllText(Path.Combine(mime, "nexode.xml"), $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
                  <mime-type type="{MimeType}">
                    <comment>{description}</comment>
                    <glob pattern="*{Extension}"/>
                  </mime-type>
                </mime-info>

                """);
            Run("update-mime-database", Path.Combine(data, "mime"));
            Run("update-desktop-database", apps);
            Run("xdg-mime", "default", "nexode.desktop", MimeType);
        }
    }

    public static void Unregister()
    {
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", false);
            using (var ext = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Extension}", writable: true))
            {
                if (ext?.GetValue(null) as string == ProgId)
                {
                    Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{Extension}", false);
                }
            }

            SHChangeNotify(0x08000000, 0, 0, 0);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            var data = DataHome();
            TryDelete(Path.Combine(data, "applications", "nexode.desktop"));
            TryDelete(Path.Combine(data, "mime", "packages", "nexode.xml"));
            Run("update-mime-database", Path.Combine(data, "mime"));
        }
    }

    private static string DataHome() =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    private static void Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Tool not installed: the files are in place anyway.
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, int flags, nint item1, nint item2);
}
