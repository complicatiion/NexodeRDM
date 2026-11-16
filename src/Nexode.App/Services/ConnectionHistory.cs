using System.Text.Json;
using System.Text.Json.Serialization;
using Nexode.Core.Model;

namespace Nexode.App.Services;

public enum HistoryResult
{
    Connected,
    Failed,
    External
}

/// <summary>One connection in the history (no passwords, no user input – only what was opened, when and how it ended).</summary>
public sealed class HistoryRecord
{
    public DateTime StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public Guid EntryId { get; set; }
    public string Name { get; set; } = "";
    public EntryKind Kind { get; set; }
    public string Target { get; set; } = "";
    public HistoryResult Result { get; set; }
    public string? Error { get; set; }
    [JsonIgnore] public TimeSpan? Duration => EndUtc is { } e ? e - StartUtc : null;
}

/// <summary>
/// Connection history of this device for the active data source (file in the state folder, newest last, at most
/// <see cref="MaxRecords"/> records). It is personal: not part of the data source and not shared with a team.
/// </summary>
public static class ConnectionHistory
{
    public const int MaxRecords = 2000;
    private static readonly Lock Gate = new();
    private static List<HistoryRecord>? _cache;
    private static string? _cacheFile;

    public static event Action? Changed;

    private static string File => Path.Combine(AppPaths.StateDirectory, $"history-{AppHost.Workspace.Info?.Id ?? "none"}.json");

    public static IReadOnlyList<HistoryRecord> All()
    {
        lock (Gate) return [.. Load()];
    }

    private static List<HistoryRecord> Load()
    {
        var file = File;
        if (_cache is not null && _cacheFile == file) return _cache;
        _cacheFile = file;
        try
        {
            _cache = System.IO.File.Exists(file) ? JsonSerializer.Deserialize(System.IO.File.ReadAllText(file), HistoryJson.Default.ListHistoryRecord) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _cache = [];
        }

        return _cache;
    }

    private static void Persist(List<HistoryRecord> list)
    {
        try
        {
            if (list.Count > MaxRecords) list.RemoveRange(0, list.Count - MaxRecords);
            var tmp = _cacheFile + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(list, HistoryJson.Default.ListHistoryRecord));
            System.IO.File.Move(tmp, _cacheFile!, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Records the start of a connection; returns the record to finish later (null when the history is off).</summary>
    public static HistoryRecord? Begin(Entry entry, string target, HistoryResult result = HistoryResult.Connected, string? error = null)
    {
        if (!AppHost.Settings.ConnectionHistory || AppHost.Workspace.Info is null) return null;
        var record = new HistoryRecord
        {
            StartUtc = DateTime.UtcNow,
            EntryId = entry.Id,
            Name = entry.Name,
            Kind = entry.Kind,
            Target = target,
            Result = result,
            Error = error,
            EndUtc = result == HistoryResult.Connected ? null : DateTime.UtcNow
        };
        lock (Gate)
        {
            var list = Load();
            list.Add(record);
            Persist(list);
        }

        Changed?.Invoke();
        return record;
    }

    /// <summary>Connection ended (closed or dropped).</summary>
    public static void End(HistoryRecord? record, string? error = null)
    {
        if (record is null || record.EndUtc is not null) return;
        lock (Gate)
        {
            record.EndUtc = DateTime.UtcNow;
            if (error is not null) record.Error = error;
            if (_cache is { } list && list.Contains(record)) Persist(list);
        }

        Changed?.Invoke();
    }

    public static void Clear()
    {
        lock (Gate)
        {
            var list = Load();
            list.Clear();
            Persist(list);
        }

        Changed?.Invoke();
    }
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<HistoryRecord>))]
internal sealed partial class HistoryJson : JsonSerializerContext;
