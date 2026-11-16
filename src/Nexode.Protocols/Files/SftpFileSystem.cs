using Nexode.Protocols.Terminal;
using Renci.SshNet;

namespace Nexode.Protocols.Files;

/// <summary>SFTP via SSH.NET.</summary>
public sealed class SftpFileSystem(ConnectParams p, ISessionPrompts prompts, KnownHosts knownHosts) : IRemoteFileSystem, IPermissionFileSystem, ITimestampFileSystem
{
    private SftpClient? _client;

    public string Protocol => "SFTP";
    public bool IsConnected => _client?.IsConnected == true;

    public IRemoteFileSystem Clone() => new SftpFileSystem(p, prompts, knownHosts);

    public async Task ConnectAsync(CancellationToken ct)
    {
        var info = await SshConnections.CreateConnectionInfoAsync(p, prompts).ConfigureAwait(false);
        _client = new SftpClient(info) { OperationTimeout = TimeSpan.FromMinutes(2) };
        SshConnections.AttachHostKeyCheck(_client, p, prompts, knownHosts);
        _client.KeepAliveInterval = TimeSpan.FromSeconds(30);
        await _client.ConnectAsync(ct).ConfigureAwait(false);
    }

    private SftpClient C => _client ?? throw new InvalidOperationException("Not connected.");

    public Task<string> GetHomeAsync(CancellationToken ct) => Task.FromResult(C.WorkingDirectory is { Length: > 0 } wd ? wd : "/");

    public async Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken ct)
    {
        var list = new List<RemoteItem>();
        await foreach (var f in C.ListDirectoryAsync(path, ct).ConfigureAwait(false))
        {
            if (f.Name is "." or "..") continue;
            var isDir = f.IsDirectory;
            if (f.IsSymbolicLink)
            {
                try
                {
                    isDir = (await C.GetAttributesAsync(f.FullName, ct).ConfigureAwait(false)).IsDirectory;
                }
                catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or Renci.SshNet.Common.SftpPathNotFoundException)
                {
                }
            }

            list.Add(new RemoteItem(f.Name, f.FullName, isDir, f.Length, f.LastWriteTime, Permissions(f), f.IsSymbolicLink));
        }

        return list;
    }

    private static string Permissions(Renci.SshNet.Sftp.ISftpFile f) =>
        (f.IsDirectory ? "d" : f.IsSymbolicLink ? "l" : "-") +
        (f.OwnerCanRead ? "r" : "-") + (f.OwnerCanWrite ? "w" : "-") + (f.OwnerCanExecute ? "x" : "-") +
        (f.GroupCanRead ? "r" : "-") + (f.GroupCanWrite ? "w" : "-") + (f.GroupCanExecute ? "x" : "-") +
        (f.OthersCanRead ? "r" : "-") + (f.OthersCanWrite ? "w" : "-") + (f.OthersCanExecute ? "x" : "-");

    public Task DownloadAsync(string remotePath, Stream target, IProgress<long>? progress, CancellationToken ct) =>
        C.DownloadFileAsync(remotePath, target, progress is null ? null : new Progress<Renci.SshNet.DownloadFileProgressReport>(r => progress.Report((long)r.TotalBytesDownloaded)), ct);

    public Task UploadAsync(Stream source, string remotePath, IProgress<long>? progress, CancellationToken ct) =>
        C.UploadFileAsync(source, remotePath, true, progress is null ? null : new Progress<Renci.SshNet.UploadFileProgressReport>(r => progress.Report((long)r.TotalBytesUploaded)), ct);

    public Task DeleteFileAsync(string path, CancellationToken ct) => C.DeleteFileAsync(path, ct);
    public Task DeleteDirectoryAsync(string path, CancellationToken ct) => C.DeleteDirectoryAsync(path, ct);
    public Task CreateDirectoryAsync(string path, CancellationToken ct) => C.CreateDirectoryAsync(path, ct);
    public Task RenameAsync(string from, string to, CancellationToken ct) => C.RenameFileAsync(from, to, ct);

    public bool CanChangePermissions => true;

    public Task SetPermissionsAsync(string path, int mode, CancellationToken ct) =>
        Task.Run(() => C.ChangePermissions(path, short.Parse(Files.Permissions.Octal(mode), System.Globalization.CultureInfo.InvariantCulture)), ct);

    public Task<bool> SetModifiedAsync(string path, DateTime utc, CancellationToken ct) => Task.Run(() =>
    {
        C.SetLastWriteTimeUtc(path, DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        return true;
    }, ct);

    public void Dispose()
    {
        try { _client?.Disconnect(); } catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or ObjectDisposedException or System.Net.Sockets.SocketException) { }
        _client?.Dispose();
    }
}
