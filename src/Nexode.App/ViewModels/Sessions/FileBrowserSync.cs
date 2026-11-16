using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.App.Views.Sessions;
using Nexode.Core.Files;
using Nexode.Protocols.Files;

namespace Nexode.App.ViewModels.Sessions;

/// <summary>Optional feature: compare the local and the remote folder and synchronize them.</summary>
public sealed partial class FileBrowserTabViewModel
{
    /// <summary>Upper limit of entries per side (a sync is for project folders, not whole disks).</summary>
    private const int ScanLimit = 20000;

    public bool SyncEnabled => Features.On(Features.FolderSync);

    public void RefreshSyncOption() => OnPropertyChanged(nameof(SyncEnabled));

    private CancellationTokenSource? _scanCts;

    /// <summary>Both folders are being read; the compare button cancels it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncButtonText), nameof(SyncButtonIcon))]
    private bool _isScanning;

    public string SyncButtonText => Loc.T(IsScanning ? "Sync.CancelScan" : "Sync.Button");
    public string SyncButtonIcon => IsScanning ? "square-x" : "folder-sync";

    private static bool IgnoreCase => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    [RelayCommand]
    private async Task CompareFolders()
    {
        if (IsScanning)
        {
            _scanCts?.Cancel();
            return;
        }

        if (_fs is not { } fs || Owner?.Window is not { } window) return;
        var localRoot = Local.Path;
        var remoteRoot = Remote.Path;
        IReadOnlyList<SyncDiff> diffs;
        var status = StatusText;
        var excludes = new SyncExcludes(AppHost.Settings.SyncExcludes, IgnoreCase);
        using var cts = new CancellationTokenSource();
        _scanCts = cts;
        IsScanning = true;
        StatusText = Loc.T("Sync.Scanning");
        try
        {
            var localTask = Task.Run(() => ScanLocal(localRoot, excludes, cts.Token));
            var remoteTask = Task.Run(() => ScanRemoteAsync(fs, remoteRoot, excludes, cts.Token));
            await Task.WhenAll(localTask, remoteTask);
            diffs = FolderSync.Compare(localTask.Result, remoteTask.Result, IgnoreCase);
        }
        catch (OperationCanceledException)
        {
            Owner?.ShowToast(Loc.T("Sync.ScanCanceled"));
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Renci.SshNet.Common.SshException or NotSupportedException)
        {
            Remote.Error = ex.Message;
            return;
        }
        finally
        {
            _scanCts = null;
            IsScanning = false;
            StatusText = status;
        }

        var choice = await FolderSyncDialog.ShowAsync(window, localRoot, remoteRoot, diffs, IgnoreCase);
        if (choice is not { } c) return;
        AppHost.Settings.SyncExcludes = c.Excludes;
        AppHost.Settings.Save();
        var actions = FolderSync.Plan(new SyncExcludes(c.Excludes, IgnoreCase).Apply(diffs), c.Direction, c.DeleteExtra);
        await RunSyncAsync(fs, localRoot, remoteRoot, actions);
    }

    private static List<SyncFile> ScanLocal(string root, SyncExcludes excludes, CancellationToken ct)
    {
        var result = new List<SyncFile>();
        var pending = new Stack<string>();
        pending.Push(root);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        while (pending.Count > 0)
        {
            foreach (var info in new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos("*", options))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(root, info.FullName).Replace('\\', '/');
                if (excludes.IsExcluded(rel)) continue;
                result.Add(new SyncFile(rel, info is DirectoryInfo, info is FileInfo f ? f.Length : 0, info.LastWriteTimeUtc));
                if (result.Count > ScanLimit) throw new InvalidOperationException(Loc.F("Sync.TooMany", ScanLimit));
                if (info is DirectoryInfo) pending.Push(info.FullName);
            }
        }

        return result;
    }

    private static async Task<List<SyncFile>> ScanRemoteAsync(IRemoteFileSystem fs, string root, SyncExcludes excludes, CancellationToken ct)
    {
        var result = new List<SyncFile>();
        var queue = new Queue<(string Path, string Rel, int Depth)>();
        queue.Enqueue((root, "", 0));
        while (queue.Count > 0)
        {
            var (path, rel, depth) = queue.Dequeue();
            foreach (var item in await fs.ListAsync(path, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (item.Name is "." or "..") continue;
                var childRel = rel.Length == 0 ? item.Name : rel + "/" + item.Name;
                if (excludes.IsExcluded(childRel)) continue;
                var time = item.Modified is { } m ? m.Kind == DateTimeKind.Local ? m.ToUniversalTime() : DateTime.SpecifyKind(m, DateTimeKind.Utc) : (DateTime?)null;
                result.Add(new SyncFile(childRel, item.IsDirectory, item.Size, time));
                if (result.Count > ScanLimit) throw new InvalidOperationException(Loc.F("Sync.TooMany", ScanLimit));
                if (item.IsDirectory && !item.IsLink && depth < 24) queue.Enqueue((item.FullPath, childRel, depth + 1));
            }
        }

        return result;
    }

    private async Task RunSyncAsync(IRemoteFileSystem fs, string localRoot, string remoteRoot, IReadOnlyList<SyncAction> actions)
    {
        string LocalPath(SyncDiff d) => Path.Combine(localRoot, d.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        string RemoteFull(SyncDiff d) => RemotePath.Combine(remoteRoot, d.RelativePath);
        var items = new List<TransferItem>();
        var failed = 0;
        foreach (var a in actions)
        {
            var d = a.Diff;
            try
            {
                switch (a.Kind)
                {
                    case SyncActionKind.CreateRemoteFolder:
                        await Task.Run(() => fs.CreateDirectoryAsync(RemoteFull(d), CancellationToken.None));
                        break;
                    case SyncActionKind.CreateLocalFolder:
                        Directory.CreateDirectory(LocalPath(d));
                        break;
                    case SyncActionKind.Upload:
                        items.Add(Enqueue(true, d.RelativePath, LocalPath(d), RemoteFull(d), d.Local?.Size ?? 0, remoteTimeUtc: d.Local?.ModifiedUtc));
                        break;
                    case SyncActionKind.Download:
                        items.Add(Enqueue(false, d.RelativePath, RemoteFull(d), LocalPath(d), d.Remote?.Size ?? 0, d.Remote?.ModifiedUtc));
                        break;
                    case SyncActionKind.DeleteRemote:
                        if (d.IsDirectory) await Task.Run(() => RemotePath.DeleteTreeAsync(fs, RemoteFull(d), CancellationToken.None));
                        else await Task.Run(() => fs.DeleteFileAsync(RemoteFull(d), CancellationToken.None));
                        break;
                    case SyncActionKind.DeleteLocal:
                        if (d.IsDirectory) Directory.Delete(LocalPath(d), true);
                        else File.Delete(LocalPath(d));
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Renci.SshNet.Common.SshException or NotSupportedException)
            {
                failed++;
                AppLog.Write($"sync {a.Kind} {d.RelativePath}: {ex.Message}");
            }
        }

        foreach (var i in items) await WaitFor(i);
        failed += items.Count(i => i.State == TransferState.Failed);
        await Local.RefreshAsync();
        await Remote.RefreshAsync();
        Owner?.ShowToast(failed == 0 ? Loc.F("Sync.Done", actions.Count) : Loc.F("Sync.DoneErrors", actions.Count - failed, failed), failed > 0);
    }
}
