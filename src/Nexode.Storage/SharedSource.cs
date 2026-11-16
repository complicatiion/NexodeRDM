using System.Globalization;
using System.Text.Json;

namespace Nexode.Storage;

/// <summary>Content of the lock file next to a shared data source.</summary>
public sealed record SharedLock(string Owner, string User, string Machine, DateTime SinceUtc, DateTime HeartbeatUtc)
{
    public bool IsStale(DateTime nowUtc) => nowUtc - HeartbeatUtc > SharedSource.StaleAfter;
}

public enum PublishResult
{
    Published,
    NotEditor,
    /// <summary>Someone else replaced the file meanwhile; our copy was saved next to it.</summary>
    Conflict,
    Failed
}

/// <summary>
/// Team data source on a network share (one editor at a time). SQLite is never opened on the share: Nexode works on a
/// local cache copy, the editor publishes it back (temp file + replace), readers pull changes. A lock file with a
/// heartbeat names the editor; a lock without heartbeat for <see cref="StaleAfter"/> can be taken over.
/// </summary>
public sealed class SharedSource
{
    public static TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(2);

    private (DateTime Time, long Length) _stamp;

    public SharedSource(string remotePath, string cacheDirectory, string owner, string user, string machine)
    {
        RemotePath = Path.GetFullPath(remotePath);
        Owner = owner;
        User = user;
        Machine = machine;
        Directory.CreateDirectory(cacheDirectory);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(RemotePath.ToUpperInvariant())))[..16];
        CachePath = Path.Combine(cacheDirectory, $"shared_{hash}.db");
    }

    public string RemotePath { get; }
    public string CachePath { get; }
    public string LockPath => RemotePath + ".lock";
    public string Owner { get; }
    public string User { get; }
    public string Machine { get; }
    public bool IsEditor { get; private set; }
    public DateTime? LastPulledUtc { get; private set; }

    public bool RemoteExists => File.Exists(RemotePath);

    public SharedLock? ReadLock()
    {
        try
        {
            if (!File.Exists(LockPath)) return null;
            using var fs = new FileStream(LockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<SharedLock>(fs, LockJson.Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Copies the shared file into the cache (when it exists). True when a copy was made.</summary>
    public bool Pull()
    {
        if (!RemoteExists) return false;
        var tmp = CachePath + ".pull";
        File.Copy(RemotePath, tmp, true);
        _stamp = Stamp();
        foreach (var side in new[] { "-wal", "-shm" })
        {
            try { File.Delete(CachePath + side); } catch (IOException) { }
        }

        File.Move(tmp, CachePath, true);
        LastPulledUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>True when the shared file changed since the last pull or publish.</summary>
    public bool RemoteChanged() => RemoteExists && Stamp() != _stamp;

    /// <summary>Becomes the editor when the file is not locked (or the lock is ours, stale or <paramref name="force"/>).</summary>
    public bool TryAcquire(bool force = false)
    {
        var now = DateTime.UtcNow;
        var info = new SharedLock(Owner, User, Machine, now, now);
        try
        {
            using (var fs = new FileStream(LockPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                JsonSerializer.Serialize(fs, info, LockJson.Options);
            }

            IsEditor = true;
            return true;
        }
        catch (IOException) when (File.Exists(LockPath))
        {
            var current = ReadLock();
            if (current is null || current.Owner == Owner || current.IsStale(now) || force)
            {
                IsEditor = WriteLock(current?.Owner == Owner ? info with { SinceUtc = current.SinceUtc } : info);
                return IsEditor;
            }

            IsEditor = false;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            IsEditor = false;
            return false;
        }
    }

    /// <summary>Refreshes the lock; false (and read-only) when someone else took it over.</summary>
    public bool Heartbeat()
    {
        if (!IsEditor) return false;
        var current = ReadLock();
        if (current is not null && current.Owner != Owner)
        {
            IsEditor = false;
            return false;
        }

        var now = DateTime.UtcNow;
        WriteLock(new SharedLock(Owner, User, Machine, current?.SinceUtc ?? now, now));
        return true;
    }

    /// <summary>
    /// Writes <paramref name="snapshot"/> (a consistent copy of the cache) to the share. When the shared file changed
    /// without us (lock taken over), the snapshot is stored as a conflict copy instead.
    /// </summary>
    public PublishResult Publish(string snapshot)
    {
        if (!IsEditor) return PublishResult.NotEditor;
        try
        {
            if (RemoteExists && _stamp != default && Stamp() != _stamp)
            {
                var conflict = Path.Combine(Path.GetDirectoryName(RemotePath)!,
                    $"{Path.GetFileNameWithoutExtension(RemotePath)}.conflict-{Safe(User)}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}{Path.GetExtension(RemotePath)}");
                File.Copy(snapshot, conflict, true);
                IsEditor = false;
                return PublishResult.Conflict;
            }

            var tmp = RemotePath + "." + Safe(Machine) + ".tmp";
            File.Copy(snapshot, tmp, true);
            File.Move(tmp, RemotePath, true);
            _stamp = Stamp();
            return PublishResult.Published;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PublishResult.Failed;
        }
    }

    /// <summary>Gives up the editor role (deletes our lock).</summary>
    public void Release()
    {
        if (!IsEditor) return;
        IsEditor = false;
        try
        {
            if (ReadLock() is { } l && l.Owner == Owner) File.Delete(LockPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private bool WriteLock(SharedLock info)
    {
        try
        {
            using var fs = new FileStream(LockPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(fs, info, LockJson.Options);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private (DateTime, long) Stamp()
    {
        var fi = new FileInfo(RemotePath);
        fi.Refresh();
        return fi.Exists ? (fi.LastWriteTimeUtc, fi.Length) : default;
    }

    private static string Safe(string s) => string.Concat(s.Where(char.IsLetterOrDigit)) is { Length: > 0 } v ? v : "user";
}

internal static class LockJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, TypeInfoResolver = LockJsonContext.Default };
}

[System.Text.Json.Serialization.JsonSerializable(typeof(SharedLock))]
internal sealed partial class LockJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
