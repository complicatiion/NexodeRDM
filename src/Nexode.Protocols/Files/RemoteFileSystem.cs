namespace Nexode.Protocols.Files;

public sealed record RemoteItem(string Name, string FullPath, bool IsDirectory, long Size, DateTime? Modified, string? Permissions, bool IsLink = false);

/// <summary>Remote file system behind a file browser tab (SFTP, FTP/FTPS).</summary>
public interface IRemoteFileSystem : IDisposable
{
    string Protocol { get; }
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken ct);
    Task<string> GetHomeAsync(CancellationToken ct);
    Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken ct);
    Task DownloadAsync(string remotePath, Stream target, IProgress<long>? progress, CancellationToken ct);
    Task UploadAsync(Stream source, string remotePath, IProgress<long>? progress, CancellationToken ct);
    Task DeleteFileAsync(string path, CancellationToken ct);
    Task DeleteDirectoryAsync(string path, CancellationToken ct);
    Task CreateDirectoryAsync(string path, CancellationToken ct);
    Task RenameAsync(string from, string to, CancellationToken ct);

    /// <summary>A second, independent connection for background transfers.</summary>
    IRemoteFileSystem Clone();
}

/// <summary>Optional: set the modification time of a remote file (SFTP, SCP, FTP with MFMT).</summary>
public interface ITimestampFileSystem
{
    /// <summary>False when the server does not support it (nothing changed).</summary>
    Task<bool> SetModifiedAsync(string path, DateTime utc, CancellationToken ct);
}

/// <summary>Optional: Unix permissions (SFTP, SCP).</summary>
public interface IPermissionFileSystem
{
    bool CanChangePermissions { get; }

    /// <summary><paramref name="mode"/>: permission bits (0755 octal = 493).</summary>
    Task SetPermissionsAsync(string path, int mode, CancellationToken ct);
}

public static class Permissions
{
    /// <summary>"rwxr-x---" (with or without type char) → mode bits; null when not parseable.</summary>
    public static int? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length == 10) text = text[1..];
        if (text.Length != 9) return null;
        var mode = 0;
        for (var i = 0; i < 9; i++)
        {
            mode <<= 1;
            if (text[i] is not '-' and not 'S' and not 'T') mode |= 1;
        }

        return mode;
    }

    public static string Format(int mode)
    {
        Span<char> c = stackalloc char[9];
        const string rwx = "rwxrwxrwx";
        for (var i = 0; i < 9; i++) c[i] = (mode & (1 << (8 - i))) != 0 ? rwx[i] : '-';
        return new string(c);
    }

    public static string Octal(int mode) => Convert.ToString(mode & 0x1FF, 8).PadLeft(3, '0');
}

public static class RemotePath
{
    public static string Combine(string dir, string name) => dir.EndsWith('/') ? dir + name : dir + "/" + name;

    public static string Parent(string path)
    {
        if (path is "/" or "") return "/";
        var trimmed = path.TrimEnd('/');
        var i = trimmed.LastIndexOf('/');
        return i <= 0 ? "/" : trimmed[..i];
    }

    public static string Name(string path)
    {
        var trimmed = path.TrimEnd('/');
        var i = trimmed.LastIndexOf('/');
        return i < 0 ? trimmed : trimmed[(i + 1)..];
    }

    /// <summary>Deletes a directory tree (files first, then folders).</summary>
    public static async Task DeleteTreeAsync(IRemoteFileSystem fs, string path, CancellationToken ct)
    {
        foreach (var item in await fs.ListAsync(path, ct).ConfigureAwait(false))
        {
            if (item.IsDirectory && !item.IsLink) await DeleteTreeAsync(fs, item.FullPath, ct).ConfigureAwait(false);
            else await fs.DeleteFileAsync(item.FullPath, ct).ConfigureAwait(false);
        }

        await fs.DeleteDirectoryAsync(path, ct).ConfigureAwait(false);
    }
}
