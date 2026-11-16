using System.Globalization;
using System.Text;
using Nexode.Protocols.Terminal;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Nexode.Protocols.Files;

/// <summary>
/// SCP for servers without SFTP subsystem (appliances, older embedded systems): transfers via SSH.NET's ScpClient,
/// listing and changes via commands on the SSH channel (ls, mkdir, rm, mv, chmod).
/// </summary>
public sealed class ScpFileSystem(ConnectParams p, ISessionPrompts prompts, KnownHosts knownHosts) : IRemoteFileSystem, IPermissionFileSystem, ITimestampFileSystem
{
    private SshClient? _ssh;
    private ScpClient? _scp;
    private bool? _gnuLs;

    public string Protocol => "SCP";
    public bool IsConnected => _ssh?.IsConnected == true;
    public bool CanChangePermissions => true;
    public IRemoteFileSystem Clone() => new ScpFileSystem(p, prompts, knownHosts);

    public async Task ConnectAsync(CancellationToken ct)
    {
        var info = await SshConnections.CreateConnectionInfoAsync(p, prompts).ConfigureAwait(false);
        _ssh = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        SshConnections.AttachHostKeyCheck(_ssh, p, prompts, knownHosts);
        await _ssh.ConnectAsync(ct).ConfigureAwait(false);
        // Second channel for the transfers (same credentials, host key already trusted).
        _scp = new ScpClient(info, RemotePathTransformation.ShellQuote) { OperationTimeout = TimeSpan.FromMinutes(5) };
        SshConnections.AttachHostKeyCheck(_scp, p, prompts, knownHosts);
        await _scp.ConnectAsync(ct).ConfigureAwait(false);
    }

    private SshClient S => _ssh ?? throw new InvalidOperationException("Not connected.");
    private ScpClient C => _scp ?? throw new InvalidOperationException("Not connected.");

    /// <summary>POSIX shell quoting.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private async Task<(int Status, string Output, string Error)> RunAsync(string command, CancellationToken ct)
    {
        using var cmd = S.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromMinutes(2);
        await cmd.ExecuteAsync(ct).ConfigureAwait(false);
        return (cmd.ExitStatus ?? -1, cmd.Result ?? "", cmd.Error ?? "");
    }

    private async Task RunCheckedAsync(string command, CancellationToken ct)
    {
        var (status, _, error) = await RunAsync(command, ct).ConfigureAwait(false);
        if (status != 0) throw new IOException(string.IsNullOrWhiteSpace(error) ? $"Command failed ({status})." : error.Trim());
    }

    public async Task<string> GetHomeAsync(CancellationToken ct)
    {
        var (status, output, _) = await RunAsync("pwd", ct).ConfigureAwait(false);
        var home = status == 0 ? output.Trim() : "";
        return home.StartsWith('/') ? home : "/";
    }

    public async Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken ct)
    {
        if (_gnuLs != false)
        {
            var (status, output, _) = await RunAsync($"LC_ALL=C ls -la --time-style=+%s -- {Quote(path)}", ct).ConfigureAwait(false);
            if (status == 0)
            {
                _gnuLs = true;
                return ParseLs(output, path, epochTimes: true);
            }

            if (_gnuLs is null) _gnuLs = false;
            else throw new IOException($"Cannot list {path}.");
        }

        var (st, outp, err) = await RunAsync($"LC_ALL=C ls -la -- {Quote(path)}", ct).ConfigureAwait(false);
        if (st != 0) throw new IOException(string.IsNullOrWhiteSpace(err) ? $"Cannot list {path}." : err.Trim());
        return ParseLs(outp, path, epochTimes: false);
    }

    /// <summary>Parses "ls -la" output (GNU with --time-style=+%s or classic "Mon dd hh:mm|yyyy").</summary>
    public static IReadOnlyList<RemoteItem> ParseLs(string output, string dir, bool epochTimes)
    {
        var list = new List<RemoteItem>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 10 || line.StartsWith("total ", StringComparison.Ordinal)) continue;
            var type = line[0];
            if (type is not ('-' or 'd' or 'l' or 'c' or 'b' or 'p' or 's')) continue;
            // perms links owner group size [date…] name
            var parts = SplitFields(line, epochTimes ? 6 : 8, out var rest);
            if (parts is null || rest is null) continue;
            long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            DateTime? modified = null;
            if (epochTimes)
            {
                if (long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)) modified = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime;
            }
            else
            {
                var stamp = $"{parts[5]} {parts[6]} {parts[7]}";
                if (DateTime.TryParseExact(stamp, ["MMM d HH:mm", "MMM dd HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                {
                    if (d > DateTime.Now.AddDays(1)) d = d.AddYears(-1);
                    modified = d;
                }
                else if (DateTime.TryParseExact(stamp, ["MMM d yyyy", "MMM dd yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var y)) modified = y;
            }

            var name = rest;
            var isLink = type == 'l';
            if (isLink && name.IndexOf(" -> ", StringComparison.Ordinal) is var arrow and > 0) name = name[..arrow];
            if (name is "." or "..") continue;
            var isDir = type == 'd';
            list.Add(new RemoteItem(name, RemotePath.Combine(dir, name), isDir, isDir ? 0 : size, modified, parts[0][..Math.Min(10, parts[0].Length)], isLink));
        }

        return list;
    }

    /// <summary>Splits the first <paramref name="count"/> whitespace separated fields; the remainder (file name, may contain spaces) goes to <paramref name="rest"/>.</summary>
    private static string[]? SplitFields(string line, int count, out string? rest)
    {
        var fields = new string[count];
        var i = 0;
        for (var f = 0; f < count; f++)
        {
            while (i < line.Length && line[i] == ' ') i++;
            var start = i;
            while (i < line.Length && line[i] != ' ') i++;
            if (start == i)
            {
                rest = null;
                return null;
            }

            fields[f] = line[start..i];
        }

        if (i < line.Length && line[i] == ' ') i++;
        rest = i < line.Length ? line[i..] : null;
        return fields;
    }

    public Task DownloadAsync(string remotePath, Stream target, IProgress<long>? progress, CancellationToken ct) => Task.Run(() =>
    {
        void OnProgress(object? _, ScpDownloadEventArgs e) => progress?.Report(e.Downloaded);
        C.Downloading += OnProgress;
        try
        {
            using var reg = ct.Register(() => { try { _scp?.Disconnect(); } catch (Exception ex) when (ex is SshException or ObjectDisposedException) { } });
            C.Download(remotePath, target);
        }
        finally
        {
            C.Downloading -= OnProgress;
        }
    }, ct);

    public Task UploadAsync(Stream source, string remotePath, IProgress<long>? progress, CancellationToken ct) => Task.Run(() =>
    {
        void OnProgress(object? _, ScpUploadEventArgs e) => progress?.Report(e.Uploaded);
        C.Uploading += OnProgress;
        try
        {
            using var reg = ct.Register(() => { try { _scp?.Disconnect(); } catch (Exception ex) when (ex is SshException or ObjectDisposedException) { } });
            C.Upload(source, remotePath);
        }
        finally
        {
            C.Uploading -= OnProgress;
        }
    }, ct);

    public Task DeleteFileAsync(string path, CancellationToken ct) => RunCheckedAsync($"rm -f -- {Quote(path)}", ct);
    public Task DeleteDirectoryAsync(string path, CancellationToken ct) => RunCheckedAsync($"rmdir -- {Quote(path)}", ct);
    public Task CreateDirectoryAsync(string path, CancellationToken ct) => RunCheckedAsync($"mkdir -- {Quote(path)}", ct);
    public Task RenameAsync(string from, string to, CancellationToken ct) => RunCheckedAsync($"mv -n -- {Quote(from)} {Quote(to)}", ct);

    public Task SetPermissionsAsync(string path, int mode, CancellationToken ct) =>
        RunCheckedAsync($"chmod {Permissions.Octal(mode)} -- {Quote(path)}", ct);

    public async Task<bool> SetModifiedAsync(string path, DateTime utc, CancellationToken ct)
    {
        // GNU and BSD touch both accept -t [[CC]YY]MMDDhhmm[.ss]; TZ=UTC makes it independent of the server's zone.
        var stamp = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyyMMddHHmm.ss", System.Globalization.CultureInfo.InvariantCulture);
        var (status, _, _) = await RunAsync($"TZ=UTC touch -m -t {stamp} -- {Quote(path)}", ct).ConfigureAwait(false);
        return status == 0;
    }

    public void Dispose()
    {
        foreach (var c in new BaseClient?[] { _scp, _ssh })
        {
            if (c is null) continue;
            try { c.Disconnect(); } catch (Exception ex) when (ex is SshException or ObjectDisposedException or System.Net.Sockets.SocketException) { }
            c.Dispose();
        }
    }
}
