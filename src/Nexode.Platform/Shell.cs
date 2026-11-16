using System.Diagnostics;

namespace Nexode.Platform;

/// <summary>Opens URLs, files and folders with the system handler.</summary>
public static class Shell
{
    public static bool Open(string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { ArgumentList = { target }, UseShellExecute = false });
            }

            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Only http(s) URLs are opened – never other schemes from imported data.</summary>
    public static bool OpenWebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && Open(uri.AbsoluteUri);

    /// <summary>True for UNC paths and mapped network drives (SQLite WAL is unsafe there).</summary>
    public static bool IsNetworkPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            if (!OperatingSystem.IsWindows()) return false;
            var root = Path.GetPathRoot(full);
            return root is not null && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
