using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;

namespace Nexode.App.Services;

/// <summary>
/// "Reset Nexode RDM": removes settings and everything Nexode keeps in its own data folder, as after a fresh install.
/// Data sources outside the data folder (custom paths, team shares) are never touched – they only drop off the list,
/// because that list lives in the settings. The wipe runs at the next start, before anything opens a file.
/// </summary>
public static class AppReset
{
    private const string Marker = "reset.pending";
    private const string KeepBackupsMarker = "reset.keep-backups";

    /// <summary>Entries of the data folder that belong to Nexode (nothing else in that folder is deleted).</summary>
    private static readonly string[] OwnDirectories = ["sources", "cache", "state", "keys", "logs", "web"];
    private static readonly string[] OwnFiles = ["settings.json", "settings.json.tmp", "settings.json.broken", "known_hosts.json", "known_hosts.json.tmp"];

    /// <summary>Unregisters system integration, marks the data folder for the wipe and restarts Nexode.</summary>
    public static void Request(bool keepBackups)
    {
        var dir = AppPaths.DataDirectory;
        File.WriteAllText(Path.Combine(dir, Marker), DateTime.UtcNow.ToString("O"));
        if (keepBackups) File.WriteAllText(Path.Combine(dir, KeepBackupsMarker), "1");

        if (Environment.ProcessPath is { } exe)
        {
            try { if (Platform.Autostart.IsSupported) Platform.Autostart.Set(false, exe); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { if (Platform.FileAssociation.IsSupported && Platform.FileAssociation.IsRegistered(exe)) Platform.FileAssociation.Unregister(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        AppHost.Settings.SuppressSave = true;
        Restart();
    }

    /// <summary>Starts a new Nexode process that waits for this one to exit, then shuts this one down.</summary>
    public static void Restart()
    {
        if (Environment.ProcessPath is { } exe && !Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "--wait-pid=" + Environment.ProcessId }, UseShellExecute = false });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
            {
                AppLog.Write("Restart failed: " + ex.Message);
            }
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    /// <summary>Runs a requested wipe; called first thing at start. True when a wipe happened.</summary>
    public static bool RunPending()
    {
        var dir = AppPaths.DataDirectory;
        var marker = Path.Combine(dir, Marker);
        if (!File.Exists(marker)) return false;
        var keepBackups = File.Exists(Path.Combine(dir, KeepBackupsMarker));

        foreach (var name in OwnDirectories.Concat(keepBackups ? [] : new[] { "backups" }))
        {
            var path = Path.Combine(dir, name);
            for (var attempt = 0; attempt < 3 && Directory.Exists(path); attempt++)
            {
                try
                {
                    Directory.Delete(path, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A web view helper may still hold its profile for a moment.
                    Thread.Sleep(400);
                }
            }
        }

        foreach (var name in OwnFiles)
        {
            try { File.Delete(Path.Combine(dir, name)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        try
        {
            File.Delete(Path.Combine(dir, KeepBackupsMarker));
            File.Delete(marker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return true;
    }

    /// <summary>Waits for the previous process (restart after reset) so the single-instance check sees a free slot.</summary>
    public static void WaitForPrevious(string[] args)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith("--wait-pid=", StringComparison.Ordinal));
        if (arg is null || !int.TryParse(arg["--wait-pid=".Length..], out var pid)) return;
        try
        {
            using var previous = Process.GetProcessById(pid);
            previous.WaitForExit(20000);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (InvalidOperationException)
        {
        }
    }
}
