using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Nexode.App.Services;

/// <summary>Support report without personal data (no names, hosts, users or passwords).</summary>
public static class Diagnostics
{
    public static string Build()
    {
        var s = AppHost.Settings;
        var ws = AppHost.Workspace;
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Nexode RDM {AppHost.Version} – diagnostics {DateTime.UtcNow:O}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Runtime: {RuntimeInformation.FrameworkDescription}, process {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Portable: {AppPaths.IsPortable}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Language: {s.Language}, theme: {s.Theme}, scale: {(s.UiScale > 0 ? s.UiScale.ToString(CultureInfo.InvariantCulture) : "auto")}, animations: {s.Animations} ({s.AnimationSpeed.ToString(CultureInfo.InvariantCulture)}×)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Data sources: {s.DataSources.Count} (team: {s.DataSources.Count(d => d.Shared)}, sample: {s.DataSources.Count(d => d.IsSample)})");
        if (ws.Store is not null)
        {
            var all = ws.Tree.All.ToList();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Active source: schema {Storage.DataSourceStore.SchemaVersion}, encryption {ws.EncryptionMode}, master password {ws.HasPassword}, locked {ws.IsLocked}, shared {ws.Shared is not null} (editor {ws.Shared?.IsEditor})");
            var kinds = string.Join(", ", all.GroupBy(e => e.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));
            sb.AppendLine(CultureInfo.InvariantCulture, $"Entries: {all.Count} – {kinds}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Categories {ws.Categories.Count}, environments {ws.Environments.Count}, profiles {ws.Profiles.Count}, trash {ws.Deleted.Count()}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"Windows: {AppHost.Windows.Windows.Count}, tabs: {AppHost.Windows.ViewModels.Sum(v => v.Tabs.Items.Count)}, sessions: {AppHost.Windows.ViewModels.Sum(v => v.SessionCount)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Known hosts: {AppHost.KnownHosts.All().Count()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Serial ports: {Nexode.Protocols.Terminal.SerialConnection.ListPorts().Count}");
        return sb.ToString();
    }
}

/// <summary>Small technical log (logs/nexode.log, rotated at 1 MB). Never receives secrets.</summary>
public static class AppLog
{
    private static readonly Lock Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var file = Path.Combine(AppPaths.LogDirectory, "nexode.log");
                if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024) File.Move(file, file + ".1", true);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
