using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.Core.Model;
using Nexode.Protocols;
using Nexode.Protocols.Files;

namespace Nexode.App.ViewModels.Sessions;

public sealed class FileRow
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsUp { get; init; }
    public bool IsLink { get; init; }
    public bool IsHidden { get; init; }
    public long Size { get; init; }
    public DateTime? Modified { get; init; }
    public string? Permissions { get; init; }
    public string Icon => IsUp ? "arrow-up" : IsDirectory ? (IsLink ? "folder-input" : "folder") : Kind switch
    {
        "img" => "image",
        "txt" => "file-text",
        "arc" => "archive",
        "cfg" => "file-json",
        "key" => "file-key",
        _ => "file"
    };

    private string Kind => Path.GetExtension(Name).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".webp" or ".ico" => "img",
        ".txt" or ".log" or ".md" or ".csv" => "txt",
        ".zip" or ".tar" or ".gz" or ".tgz" or ".7z" or ".rar" or ".bz2" or ".xz" => "arc",
        ".json" or ".xml" or ".yml" or ".yaml" or ".conf" or ".cfg" or ".ini" or ".toml" => "cfg",
        ".pem" or ".key" or ".crt" or ".cer" or ".pub" or ".ppk" => "key",
        _ => ""
    };

    public string SizeText => IsDirectory || IsUp ? "" : SettingsTabViewModel.FormatSize(Size);
    public string ModifiedText => Modified?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "";

    /// <summary>"Folder" or the extension in upper case ("TXT").</summary>
    public string TypeText => IsUp ? "" : IsDirectory ? Loc.T("Files.TypeFolder")
        : Path.GetExtension(Name) is { Length: > 1 } ext ? ext[1..].ToUpperInvariant() : Loc.T("Files.TypeFile");

    public string PermissionsText => Permissions is { Length: >= 9 } p ? p[^9..] : "";
}

public enum FileSort { Name, Size, Type, Modified, Permissions }

/// <summary>One side of the file browser (local disk or remote server): sorted and filtered view of a folder.</summary>
public sealed partial class FilePane : ObservableObject
{
    private readonly Func<string, Task<IReadOnlyList<FileRow>>> _list;
    private readonly Func<string, string> _parent;
    private IReadOnlyList<FileRow> _all = [];

    public FilePane(bool isRemote, Func<string, Task<IReadOnlyList<FileRow>>> list, Func<string, string> parent)
    {
        IsRemote = isRemote;
        _list = list;
        _parent = parent;
    }

    public bool IsRemote { get; }
    public ObservableCollection<FileRow> Items { get; } = [];

    [ObservableProperty]
    private string _path = "";

    [ObservableProperty]
    private string _pathInput = "";

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private FileSort _sort = FileSort.Name;

    [ObservableProperty]
    private bool _descending;

    [ObservableProperty]
    private string _summary = "";

    public bool ShowHidden
    {
        get => AppHost.Settings.FilesShowHidden;
        set
        {
            if (AppHost.Settings.FilesShowHidden == value) return;
            AppHost.Settings.FilesShowHidden = value;
            AppHost.Settings.Save();
            OnPropertyChanged();
            Apply();
        }
    }

    public bool HasError => !string.IsNullOrEmpty(Error);
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnFilterChanged(string value) => Apply();
    partial void OnSortChanged(FileSort value) => Apply();
    partial void OnDescendingChanged(bool value) => Apply();

    /// <summary>Header click: same column toggles the direction.</summary>
    public void SortBy(FileSort column)
    {
        if (Sort == column) Descending = !Descending;
        else
        {
            Descending = column is FileSort.Modified or FileSort.Size;
            Sort = column;
        }
    }

    public async Task LoadAsync(string path)
    {
        Busy = true;
        Error = null;
        try
        {
            var rows = await _list(path);
            _all = rows;
            if (Path != path) Filter = "";
            Path = path;
            PathInput = path;
            Apply();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Renci.SshNet.Common.SshException or ArgumentException
                                       or System.Net.Http.HttpRequestException or NotSupportedException or System.Xml.XmlException)
        {
            Error = ex.Message;
            PathInput = Path;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Applies hidden files, filter and sort order to the loaded folder.</summary>
    public void Apply()
    {
        IEnumerable<FileRow> rows = _all;
        if (!ShowHidden) rows = rows.Where(r => !r.IsHidden && !r.Name.StartsWith('.'));
        if (Filter.Trim() is { Length: > 0 } f)
        {
            if (f.Contains('*') || f.Contains('?'))
            {
                var regex = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(f).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                rows = rows.Where(r => regex.IsMatch(r.Name));
            }
            else
            {
                rows = rows.Where(r => r.Name.Contains(f, StringComparison.CurrentCultureIgnoreCase));
            }
        }

        var cmp = Core.Tree.NaturalComparer.Instance;
        IOrderedEnumerable<FileRow> ordered = rows.OrderByDescending(r => r.IsDirectory);
        ordered = Sort switch
        {
            FileSort.Size => Descending ? ordered.ThenByDescending(r => r.Size) : ordered.ThenBy(r => r.Size),
            FileSort.Type => Descending ? ordered.ThenByDescending(r => r.TypeText, StringComparer.CurrentCultureIgnoreCase) : ordered.ThenBy(r => r.TypeText, StringComparer.CurrentCultureIgnoreCase),
            FileSort.Modified => Descending ? ordered.ThenByDescending(r => r.Modified) : ordered.ThenBy(r => r.Modified),
            FileSort.Permissions => Descending ? ordered.ThenByDescending(r => r.PermissionsText, StringComparer.Ordinal) : ordered.ThenBy(r => r.PermissionsText, StringComparer.Ordinal),
            _ => ordered
        };
        ordered = Sort == FileSort.Name && Descending ? ordered.ThenByDescending(r => r.Name, cmp) : ordered.ThenBy(r => r.Name, cmp);
        var list = ordered.ToList();

        Items.Clear();
        if (_parent(Path) != Path) Items.Add(new FileRow { Name = "..", FullPath = _parent(Path), IsDirectory = true, IsUp = true });
        foreach (var r in list) Items.Add(r);
        var files = list.Count(r => !r.IsDirectory);
        Summary = Loc.F("Files.Summary", list.Count(r => r.IsDirectory), files, SettingsTabViewModel.FormatSize(list.Where(r => !r.IsDirectory).Sum(r => r.Size)))
                  + (_all.Count != list.Count ? " · " + Loc.F("Files.Hidden", _all.Count - list.Count) : "");
    }

    public Task RefreshAsync() => LoadAsync(Path);
    public Task UpAsync() => LoadAsync(_parent(Path));
}

public enum TransferState { Waiting, Running, Done, Failed, Cancelled }

public sealed partial class TransferItem : ObservableObject
{
    public required string Name { get; init; }
    public required bool Upload { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public long Total { get; init; }
    /// <summary>Download: modification time to give the local copy (folder sync keeps both sides comparable).</summary>
    public DateTime? LocalTimeUtc { get; init; }
    /// <summary>Upload: modification time to give the remote copy, where the protocol allows it.</summary>
    public DateTime? RemoteTimeUtc { get; init; }
    public CancellationTokenSource Cts { get; } = new();
    public Task? Task { get; set; }
    public string Icon => Upload ? "upload" : "download";

    [ObservableProperty]
    private long _done;

    [ObservableProperty]
    private TransferState _state;

    [ObservableProperty]
    private string? _error;

    public double Percent => Total <= 0 ? (State == TransferState.Done ? 100 : 0) : Math.Min(100, Done * 100.0 / Total);
    public string StateText => State switch
    {
        TransferState.Running => $"{Percent:0} %",
        TransferState.Done => Loc.T("Transfer.Done"),
        TransferState.Failed => Error ?? Loc.T("Transfer.Failed"),
        TransferState.Cancelled => Loc.T("Transfer.Cancelled"),
        _ => Loc.T("Transfer.Waiting")
    };

    public bool CanCancel => State is TransferState.Waiting or TransferState.Running;
    partial void OnDoneChanged(long value) => OnPropertyChanged(nameof(Percent));

    partial void OnStateChanged(TransferState value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(CanCancel));
    }
}

/// <summary>SFTP / FTP / FTPS: local and remote panes with a transfer queue on a separate connection.</summary>
public sealed partial class FileBrowserTabViewModel : SessionTabViewModel
{
    private IRemoteFileSystem? _fs;
    private IRemoteFileSystem? _transferFs;
    private readonly SemaphoreSlim _queueGate = new(1, 1);

    public FileBrowserTabViewModel(Guid entryId, IReadOnlyDictionary<string, string>? overrides = null) : base(entryId, overrides)
    {
        Local = new FilePane(false, ListLocal, p => Directory.GetParent(p)?.FullName ?? p);
        Remote = new FilePane(true, ListRemote, RemotePath.Parent);
        _ = Local.LoadAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    public FilePane Local { get; }
    public FilePane Remote { get; }
    public ObservableCollection<TransferItem> Transfers { get; } = [];
    public bool HasTransfers => Transfers.Count > 0;
    public string ProtocolText => _fs?.Protocol ?? Loc.Kind(Entry?.Kind ?? EntryKind.Sftp);
    public override TabViewModel Recreate() => new FileBrowserTabViewModel(EntryId, Overrides);

    protected override async Task ConnectCoreAsync(ConnectParams p, ISessionPrompts prompts, CancellationToken ct)
    {
        _fs?.Dispose();
        _fs = Overrides?.ContainsKey("session.kind") == true ? new SftpFileSystem(p, prompts, AppHost.KnownHosts) : Entry?.Kind switch
        {
            EntryKind.Ftp => new FtpClient(p, prompts, AppHost.KnownHosts),
            EntryKind.Scp => new ScpFileSystem(p, prompts, AppHost.KnownHosts),
            EntryKind.WebDav => new WebDavFileSystem(p, prompts, AppHost.KnownHosts),
            EntryKind.S3 => new S3FileSystem(p, prompts, AppHost.KnownHosts),
            _ => new SftpFileSystem(p, prompts, AppHost.KnownHosts)
        };
        await _fs.ConnectAsync(ct);
        var start = Entry?.Kind is EntryKind.S3 or EntryKind.WebDav ? await _fs.GetHomeAsync(ct) : p.Setting(SettingKeys.RemotePath) ?? await _fs.GetHomeAsync(ct);
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            OnPropertyChanged(nameof(ProtocolText));
            await Remote.LoadAsync(start);
        });
    }

    protected override void DisconnectCore()
    {
        var fs = _fs;
        var tfs = _transferFs;
        _fs = null;
        _transferFs = null;
        foreach (var t in Transfers.Where(t => t.CanCancel)) t.Cts.Cancel();
        Task.Run(() =>
        {
            fs?.Dispose();
            tfs?.Dispose();
        });
    }

    private Task<IReadOnlyList<FileRow>> ListLocal(string path) => Task.Run(() =>
    {
        var dir = new DirectoryInfo(path);
        var rows = new List<FileRow>();
        foreach (var info in dir.EnumerateFileSystemInfos())
        {
            rows.Add(new FileRow
            {
                Name = info.Name,
                IsHidden = (info.Attributes & FileAttributes.Hidden) != 0,
                Permissions = OperatingSystem.IsWindows() ? null : UnixPermissions(info),
                FullPath = info.FullName,
                IsDirectory = info is DirectoryInfo,
                Size = info is FileInfo f ? f.Length : 0,
                Modified = info.LastWriteTime
            });
        }

        return (IReadOnlyList<FileRow>)rows;
    });

    private static string? UnixPermissions(FileSystemInfo info)
    {
        if (OperatingSystem.IsWindows()) return null;
        try
        {
            return Nexode.Protocols.Files.Permissions.Format((int)info.UnixFileMode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<FileRow>> ListRemote(string path)
    {
        var fs = _fs ?? throw new InvalidOperationException(Loc.T("Session.Disconnected"));
        var items = await Task.Run(() => fs.ListAsync(path, CancellationToken.None));
        return items.Select(i => new FileRow
        {
            Name = i.Name,
            FullPath = i.FullPath,
            IsDirectory = i.IsDirectory,
            IsLink = i.IsLink,
            Size = i.Size,
            Modified = i.Modified,
            Permissions = i.Permissions
        }).ToList();
    }

    // ------------------------------------------------------------------ navigation

    public async Task OpenAsync(FilePane pane, FileRow row)
    {
        if (row.IsDirectory)
        {
            await pane.LoadAsync(row.FullPath);
            return;
        }

        if (!pane.IsRemote)
        {
            Platform.Shell.Open(row.FullPath);
            return;
        }

        await EditAsync(row);
    }

    // ------------------------------------------------------------------ remote edit

    private readonly List<RemoteEdit> _edits = [];

    public int EditCount => _edits.Count;
    public bool HasEdits => _edits.Count > 0;

    /// <summary>
    /// Downloads a remote file to a private temporary folder, opens it with the associated program and uploads it
    /// again every time it is saved (until the tab closes).
    /// </summary>
    public async Task EditAsync(FileRow row)
    {
        if (_edits.FirstOrDefault(e => e.RemotePath == row.FullPath) is { } existing && File.Exists(existing.LocalPath))
        {
            Platform.Shell.Open(existing.LocalPath);
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "Nexode", "edit", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, row.Name);
        var item = Enqueue(false, row.Name, row.FullPath, target, row.Size);
        await WaitFor(item);
        if (item.State != TransferState.Done) return;
        var edit = new RemoteEdit(row.FullPath, target, this);
        _edits.Add(edit);
        OnPropertyChanged(nameof(EditCount));
        OnPropertyChanged(nameof(HasEdits));
        Platform.Shell.Open(target);
    }

    /// <summary>Called by a watcher (UI thread) when the local copy was saved.</summary>
    internal async Task UploadEditAsync(RemoteEdit edit)
    {
        if (!IsConnected) return;
        var item = Enqueue(true, RemotePath.Name(edit.RemotePath), edit.LocalPath, edit.RemotePath, new FileInfo(edit.LocalPath).Length);
        await WaitFor(item);
        if (item.State == TransferState.Done)
        {
            Owner?.ShowToast(Loc.F("Files.EditUploaded", RemotePath.Name(edit.RemotePath)));
            if (Remote.Path == RemotePath.Parent(edit.RemotePath)) await Remote.RefreshAsync();
        }
        else
        {
            Owner?.ShowToast(Loc.F("Files.EditFailed", RemotePath.Name(edit.RemotePath), item.Error ?? ""), true);
        }
    }

    private void StopEdits()
    {
        foreach (var e in _edits) e.Dispose();
        _edits.Clear();
    }

    // ------------------------------------------------------------------ clipboard (cut / copy / paste)

    private (FilePane Pane, List<FileRow> Rows, bool Cut)? _clip;

    public bool CanPaste => _clip is not null;

    public void SetClipboard(FilePane pane, IReadOnlyList<FileRow> rows, bool cut)
    {
        _clip = rows.Count == 0 ? null : (pane, rows.Where(r => !r.IsUp).ToList(), cut);
        OnPropertyChanged(nameof(CanPaste));
    }

    /// <summary>
    /// Paste into <paramref name="target"/>: same side = move (cut) or copy, other side = transfer (cut deletes the
    /// source afterwards). Remote copies use the server (WebDAV) or go through a temporary local copy.
    /// </summary>
    public async Task PasteAsync(FilePane target, string? intoDir = null)
    {
        if (_clip is not { } clip) return;
        var dir = intoDir ?? target.Path;
        try
        {
            if (clip.Pane == target)
            {
                foreach (var row in clip.Rows)
                {
                    var dest = target.IsRemote ? RemotePath.Combine(dir, row.Name) : Path.Combine(dir, row.Name);
                    if (dest == row.FullPath)
                    {
                        if (clip.Cut) continue;
                        dest = UniqueName(target, dir, row.Name);
                    }

                    if (clip.Cut)
                    {
                        if (target.IsRemote) await Task.Run(() => _fs!.RenameAsync(row.FullPath, dest, CancellationToken.None));
                        else if (row.IsDirectory) Directory.Move(row.FullPath, dest);
                        else File.Move(row.FullPath, dest);
                    }
                    else if (!target.IsRemote)
                    {
                        await Task.Run(() => CopyLocal(row.FullPath, dest, row.IsDirectory));
                    }
                    else
                    {
                        await CopyRemoteAsync(row, dest);
                    }
                }

                if (clip.Cut) _clip = null;
                await target.RefreshAsync();
                OnPropertyChanged(nameof(CanPaste));
                return;
            }

            // Across sides: a transfer.
            if (target.IsRemote) await UploadAsync(clip.Rows.Select(r => r.FullPath), dir);
            else await DownloadAsync(clip.Rows, dir);
            if (clip.Cut)
            {
                await DeleteAsync(clip.Pane, clip.Rows);
                _clip = null;
                OnPropertyChanged(nameof(CanPaste));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Renci.SshNet.Common.SshException or NotSupportedException)
        {
            target.Error = ex.Message;
        }
    }

    private static string UniqueName(FilePane pane, string dir, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({Loc.T("Files.CopySuffix")}{(i > 2 ? " " + (i - 1).ToString(CultureInfo.InvariantCulture) : "")}){ext}";
            if (pane.Items.All(r => r.Name != candidate)) return pane.IsRemote ? RemotePath.Combine(dir, candidate) : Path.Combine(dir, candidate);
        }
    }

    private static void CopyLocal(string from, string to, bool directory)
    {
        if (!directory)
        {
            File.Copy(from, to, false);
            return;
        }

        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), false);
        foreach (var sub in Directory.EnumerateDirectories(from)) CopyLocal(sub, Path.Combine(to, Path.GetFileName(sub)), true);
    }

    private async Task CopyRemoteAsync(FileRow row, string dest)
    {
        if (_fs is WebDavFileSystem dav)
        {
            await Task.Run(() => dav.CopyAsync(row.FullPath, dest, CancellationToken.None));
            return;
        }

        // Download into a temporary folder, upload under the new name.
        var temp = Path.Combine(Path.GetTempPath(), "Nexode", "copy", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(temp);
        try
        {
            await DownloadAsync([row], temp);
            var local = Path.Combine(temp, row.Name);
            if (row.IsDirectory)
            {
                var staged = Path.Combine(temp, RemotePath.Name(dest));
                if (staged != local) Directory.Move(local, staged);
                await UploadAsync([staged], RemotePath.Parent(dest));
            }
            else
            {
                var item = Enqueue(true, RemotePath.Name(dest), local, dest, new FileInfo(local).Length);
                await WaitFor(item);
            }
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }
    }

    // ------------------------------------------------------------------ new file, permissions

    public async Task NewFileAsync(FilePane pane, string name)
    {
        try
        {
            if (pane.IsRemote)
            {
                if (_fs is { } fs) await Task.Run(() => fs.UploadAsync(new MemoryStream(), RemotePath.Combine(pane.Path, name), null, CancellationToken.None));
            }
            else
            {
                var path = Path.Combine(pane.Path, name);
                if (File.Exists(path)) throw new IOException(Loc.F("Files.Exists", name));
                await File.WriteAllBytesAsync(path, []);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Renci.SshNet.Common.SshException)
        {
            pane.Error = ex.Message;
        }

        await pane.RefreshAsync();
    }

    /// <summary>Unix permissions can be changed (SFTP, SCP; local files on Linux/macOS).</summary>
    public bool CanChangePermissions(FilePane pane) => pane.IsRemote ? _fs is IPermissionFileSystem { CanChangePermissions: true } : !OperatingSystem.IsWindows();

    public async Task SetPermissionsAsync(FilePane pane, IReadOnlyList<FileRow> rows, int mode)
    {
        try
        {
            foreach (var row in rows)
            {
                if (pane.IsRemote)
                {
                    if (_fs is IPermissionFileSystem pfs) await Task.Run(() => pfs.SetPermissionsAsync(row.FullPath, mode, CancellationToken.None));
                }
                else if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(row.FullPath, (UnixFileMode)mode);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Renci.SshNet.Common.SshException or NotSupportedException)
        {
            pane.Error = ex.Message;
        }

        await pane.RefreshAsync();
    }

    [RelayCommand]
    private Task GoLocal() => Local.LoadAsync(Local.PathInput);

    [RelayCommand]
    private Task GoRemote() => Remote.LoadAsync(Remote.PathInput);

    [RelayCommand]
    private Task LocalUp() => Local.UpAsync();

    [RelayCommand]
    private Task RemoteUp() => Remote.UpAsync();

    [RelayCommand]
    private Task LocalRefresh() => Local.RefreshAsync();

    [RelayCommand]
    private Task RemoteRefresh() => Remote.RefreshAsync();

    [RelayCommand]
    private Task LocalHome() => Local.LoadAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    // ------------------------------------------------------------------ file operations

    public async Task NewFolderAsync(FilePane pane, string name)
    {
        try
        {
            if (pane.IsRemote)
            {
                if (_fs is { } fs) await Task.Run(() => fs.CreateDirectoryAsync(RemotePath.Combine(pane.Path, name), CancellationToken.None));
            }
            else
            {
                Directory.CreateDirectory(Path.Combine(pane.Path, name));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Renci.SshNet.Common.SshException)
        {
            pane.Error = ex.Message;
        }

        await pane.RefreshAsync();
    }

    public async Task RenameAsync(FilePane pane, FileRow row, string name)
    {
        try
        {
            if (pane.IsRemote)
            {
                if (_fs is { } fs) await Task.Run(() => fs.RenameAsync(row.FullPath, RemotePath.Combine(pane.Path, name), CancellationToken.None));
            }
            else if (row.IsDirectory)
            {
                Directory.Move(row.FullPath, Path.Combine(pane.Path, name));
            }
            else
            {
                File.Move(row.FullPath, Path.Combine(pane.Path, name));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Renci.SshNet.Common.SshException)
        {
            pane.Error = ex.Message;
        }

        await pane.RefreshAsync();
    }

    public async Task DeleteAsync(FilePane pane, IReadOnlyList<FileRow> rows)
    {
        try
        {
            foreach (var row in rows.Where(r => !r.IsUp))
            {
                if (pane.IsRemote)
                {
                    if (_fs is not { } fs) break;
                    await Task.Run(() => row.IsDirectory && !row.IsLink
                        ? RemotePath.DeleteTreeAsync(fs, row.FullPath, CancellationToken.None)
                        : fs.DeleteFileAsync(row.FullPath, CancellationToken.None));
                }
                else if (row.IsDirectory)
                {
                    Directory.Delete(row.FullPath, true);
                }
                else
                {
                    File.Delete(row.FullPath);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Renci.SshNet.Common.SshException)
        {
            pane.Error = ex.Message;
        }

        await pane.RefreshAsync();
    }

    // ------------------------------------------------------------------ transfers

    /// <summary>Uploads local files/folders into the current remote folder.</summary>
    public async Task UploadAsync(IEnumerable<string> localPaths, string? remoteDir = null)
    {
        remoteDir ??= Remote.Path;
        var items = new List<TransferItem>();
        foreach (var path in localPaths)
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(Path.GetDirectoryName(path)!, file).Replace('\\', '/');
                    items.Add(Enqueue(true, rel, file, RemotePath.Combine(remoteDir, rel), new FileInfo(file).Length));
                }
            }
            else if (File.Exists(path))
            {
                items.Add(Enqueue(true, Path.GetFileName(path), path, RemotePath.Combine(remoteDir, Path.GetFileName(path)), new FileInfo(path).Length));
            }
        }

        foreach (var i in items) await WaitFor(i);
        await Remote.RefreshAsync();
    }

    /// <summary>Downloads remote files/folders into the current local folder.</summary>
    public async Task DownloadAsync(IEnumerable<FileRow> rows, string? localDir = null)
    {
        localDir ??= Local.Path;
        var items = new List<TransferItem>();
        foreach (var row in rows.Where(r => !r.IsUp))
        {
            if (row.IsDirectory)
            {
                await CollectRemote(row.FullPath, Path.Combine(localDir, row.Name), row.Name, items);
            }
            else
            {
                items.Add(Enqueue(false, row.Name, row.FullPath, Path.Combine(localDir, row.Name), row.Size));
            }
        }

        foreach (var i in items) await WaitFor(i);
        await Local.RefreshAsync();
    }

    private async Task CollectRemote(string remote, string local, string display, List<TransferItem> items)
    {
        if (_fs is not { } fs) return;
        foreach (var child in await Task.Run(() => fs.ListAsync(remote, CancellationToken.None)))
        {
            if (child.IsDirectory && !child.IsLink) await CollectRemote(child.FullPath, Path.Combine(local, child.Name), display + "/" + child.Name, items);
            else items.Add(Enqueue(false, display + "/" + child.Name, child.FullPath, Path.Combine(local, child.Name), child.Size));
        }
    }

    private TransferItem Enqueue(bool upload, string name, string source, string target, long size, DateTime? localTimeUtc = null, DateTime? remoteTimeUtc = null)
    {
        var item = new TransferItem { Name = name, Upload = upload, Source = source, Target = target, Total = size, LocalTimeUtc = localTimeUtc, RemoteTimeUtc = remoteTimeUtc };
        Transfers.Insert(0, item);
        while (Transfers.Count > 200) Transfers.RemoveAt(Transfers.Count - 1);
        OnPropertyChanged(nameof(HasTransfers));
        item.Task = Task.Run(() => RunTransfer(item));
        return item;
    }

    private static Task WaitFor(TransferItem item) => item.Task ?? Task.CompletedTask;

    private async Task RunTransfer(TransferItem item)
    {
        await _queueGate.WaitAsync();
        try
        {
            if (item.Cts.IsCancellationRequested)
            {
                Post(() => item.State = TransferState.Cancelled);
                return;
            }

            Post(() => item.State = TransferState.Running);
            if (_transferFs is not { IsConnected: true })
            {
                _transferFs?.Dispose();
                _transferFs = _fs?.Clone() ?? throw new InvalidOperationException(Loc.T("Session.Disconnected"));
                await _transferFs.ConnectAsync(item.Cts.Token);
            }

            var progress = new UiProgress(n => item.Done = n);
            if (item.Upload)
            {
                await using var src = File.OpenRead(item.Source);
                var dir = RemotePath.Parent(item.Target);
                await EnsureRemoteDir(dir, item.Cts.Token);
                await _transferFs.UploadAsync(src, item.Target, progress, item.Cts.Token);
                if (item.RemoteTimeUtc is { } remoteTime && _transferFs is ITimestampFileSystem ts)
                {
                    try
                    {
                        await ts.SetModifiedAsync(item.Target, remoteTime, item.Cts.Token);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or Renci.SshNet.Common.SshException)
                    {
                        // Not fatal: the next comparison just sees the remote copy as newer.
                        AppLog.Write($"set remote time: {ex.Message}");
                    }
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!);
                var temp = item.Target + ".part";
                await using (var dst = File.Create(temp))
                {
                    await _transferFs.DownloadAsync(item.Source, dst, progress, item.Cts.Token);
                }

                File.Move(temp, item.Target, true);
                if (item.LocalTimeUtc is { } time) File.SetLastWriteTimeUtc(item.Target, time);
            }

            Post(() =>
            {
                item.Done = item.Total;
                item.State = TransferState.Done;
            });
        }
        catch (OperationCanceledException)
        {
            Post(() => item.State = TransferState.Cancelled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException)
        {
            Post(() =>
            {
                item.Error = ex.Message;
                item.State = TransferState.Failed;
            });
            if (ex is not (FtpException or Renci.SshNet.Common.SftpPathNotFoundException or Renci.SshNet.Common.SftpPermissionDeniedException or UnauthorizedAccessException))
            {
                _transferFs?.Dispose();
                _transferFs = null;
            }
        }
        finally
        {
            _queueGate.Release();
        }
    }

    private readonly HashSet<string> _knownDirs = [];

    private async Task EnsureRemoteDir(string dir, CancellationToken ct)
    {
        if (dir is "/" or "" || _knownDirs.Contains(dir) || _transferFs is null) return;
        await EnsureRemoteDir(RemotePath.Parent(dir), ct);
        try
        {
            await _transferFs.CreateDirectoryAsync(dir, ct);
        }
        catch (Exception ex) when (ex is IOException or Renci.SshNet.Common.SshException)
        {
            // Exists already (or no permission – the upload will report it).
        }

        _knownDirs.Add(dir);
    }

    private static void Post(Action a) => Dispatcher.UIThread.Post(a);

    [RelayCommand]
    private void CancelTransfer(TransferItem item) => item.Cts.Cancel();

    [RelayCommand]
    private void ClearTransfers()
    {
        foreach (var t in Transfers.Where(t => !t.CanCancel).ToList()) Transfers.Remove(t);
        OnPropertyChanged(nameof(HasTransfers));
    }

    public override void OnClosed()
    {
        base.OnClosed();
        foreach (var t in Transfers) t.Cts.Cancel();
        StopEdits();
    }
}

/// <summary>Local copy of a remote file opened for editing: every save is uploaded again (debounced).</summary>
internal sealed class RemoteEdit : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromSeconds(1.2) };
    private DateTime _lastWrite;

    public RemoteEdit(string remotePath, string localPath, FileBrowserTabViewModel owner)
    {
        RemotePath = remotePath;
        LocalPath = localPath;
        _lastWrite = File.GetLastWriteTimeUtc(localPath);
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(localPath)!)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        // Editors often save via a temp file + rename: watch the folder, compare the write time.
        void Changed(object? _, FileSystemEventArgs e)
        {
            if (!string.Equals(Path.GetFileName(e.FullPath), Path.GetFileName(localPath), StringComparison.OrdinalIgnoreCase)) return;
            Dispatcher.UIThread.Post(() =>
            {
                _debounce.Stop();
                _debounce.Start();
            });
        }

        _watcher.Changed += Changed;
        _watcher.Created += Changed;
        _watcher.Renamed += (s, e) => Changed(s, e);
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            DateTime write;
            try
            {
                write = File.GetLastWriteTimeUtc(localPath);
            }
            catch (IOException)
            {
                return;
            }

            if (write == _lastWrite || !File.Exists(localPath)) return;
            _lastWrite = write;
            await owner.UploadEditAsync(this);
        };
    }

    public string RemotePath { get; }
    public string LocalPath { get; }

    public void Dispose()
    {
        _debounce.Stop();
        _watcher.Dispose();
        try
        {
            Directory.Delete(Path.GetDirectoryName(LocalPath)!, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Progress reports marshalled to the UI thread, at most every 100 ms.</summary>
internal sealed class UiProgress(Action<long> report) : IProgress<long>
{
    private long _last;
    private long _value;
    private int _posted;

    public void Report(long value)
    {
        Interlocked.Exchange(ref _value, value);
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _last) < 100 || Interlocked.Exchange(ref _posted, 1) == 1) return;
        Interlocked.Exchange(ref _last, now);
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _posted, 0);
            report(Interlocked.Read(ref _value));
        });
    }
}
