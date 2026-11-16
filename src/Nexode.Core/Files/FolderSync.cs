using System.Text.RegularExpressions;

namespace Nexode.Core.Files;

/// <summary>A file or folder of one side, relative to the compared root ("/" separators).</summary>
public sealed record SyncFile(string RelativePath, bool IsDirectory, long Size, DateTime? ModifiedUtc);

public enum SyncState
{
    Same,
    LocalOnly,
    RemoteOnly,
    LocalNewer,
    RemoteNewer,
    /// <summary>Different size, times equal or unknown.</summary>
    Different
}

public enum SyncDirection
{
    /// <summary>Local → remote: the remote folder becomes a copy of the local one.</summary>
    Upload,
    /// <summary>Remote → local.</summary>
    Download,
    /// <summary>Both ways: the newer file wins, files missing on one side are copied over.</summary>
    Both
}

public enum SyncActionKind
{
    Upload,
    Download,
    CreateRemoteFolder,
    CreateLocalFolder,
    DeleteRemote,
    DeleteLocal
}

public sealed record SyncDiff(string RelativePath, bool IsDirectory, SyncState State, SyncFile? Local, SyncFile? Remote);

public sealed record SyncAction(SyncActionKind Kind, SyncDiff Diff);

/// <summary>
/// Folder compare and sync planning (no I/O). Files are equal when the size matches and the modification times are within
/// <see cref="Tolerance"/> (FTP servers and FAT disks round times). One-way sync never overwrites a newer file of the
/// same size on the target (that is usually the copy made by the previous sync).
/// </summary>
public static class FolderSync
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    public static IReadOnlyList<SyncDiff> Compare(IEnumerable<SyncFile> local, IEnumerable<SyncFile> remote, bool ignoreCase = false)
    {
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var l = new Dictionary<string, SyncFile>(comparer);
        foreach (var f in local) l[Normalize(f.RelativePath)] = f;
        var r = new Dictionary<string, SyncFile>(comparer);
        foreach (var f in remote) r[Normalize(f.RelativePath)] = f;

        var result = new List<SyncDiff>();
        foreach (var path in l.Keys.Union(r.Keys, comparer).Order(StringComparer.OrdinalIgnoreCase))
        {
            var a = l.GetValueOrDefault(path);
            var b = r.GetValueOrDefault(path);
            var dir = (a ?? b)!.IsDirectory;
            if (a is not null && b is not null && a.IsDirectory != b.IsDirectory)
            {
                // A file on one side, a folder on the other: reported, never synced automatically.
                result.Add(new SyncDiff(path, false, SyncState.Different, a, b));
                continue;
            }

            result.Add(new SyncDiff(path, dir, State(a, b), a, b));
        }

        return result;
    }

    private static SyncState State(SyncFile? a, SyncFile? b)
    {
        if (a is null) return SyncState.RemoteOnly;
        if (b is null) return SyncState.LocalOnly;
        if (a.IsDirectory) return SyncState.Same;
        var newer = Newer(a.ModifiedUtc, b.ModifiedUtc);
        if (a.Size == b.Size && newer == 0) return SyncState.Same;
        return newer switch
        {
            > 0 => SyncState.LocalNewer,
            < 0 => SyncState.RemoteNewer,
            _ => SyncState.Different
        };
    }

    /// <summary>+1 local newer, -1 remote newer, 0 equal within tolerance or unknown.</summary>
    private static int Newer(DateTime? local, DateTime? remote)
    {
        if (local is not { } a || remote is not { } b) return 0;
        var delta = a - b;
        return delta > Tolerance ? 1 : delta < -Tolerance ? -1 : 0;
    }

    public static IReadOnlyList<SyncAction> Plan(IReadOnlyList<SyncDiff> diffs, SyncDirection direction, bool deleteExtra)
    {
        var actions = new List<SyncAction>();
        foreach (var d in diffs)
        {
            if (d.State == SyncState.Same) continue;
            if (d.Local is not null && d.Remote is not null && d.Local.IsDirectory != d.Remote.IsDirectory) continue;
            switch (direction)
            {
                case SyncDirection.Upload:
                    if (d.State == SyncState.RemoteOnly)
                    {
                        if (deleteExtra) actions.Add(new SyncAction(SyncActionKind.DeleteRemote, d));
                    }
                    else if (d.State == SyncState.LocalOnly && d.IsDirectory) actions.Add(new SyncAction(SyncActionKind.CreateRemoteFolder, d));
                    else if (!d.IsDirectory && (d.State != SyncState.RemoteNewer || d.Local!.Size != d.Remote!.Size))
                        actions.Add(new SyncAction(SyncActionKind.Upload, d));
                    break;
                case SyncDirection.Download:
                    if (d.State == SyncState.LocalOnly)
                    {
                        if (deleteExtra) actions.Add(new SyncAction(SyncActionKind.DeleteLocal, d));
                    }
                    else if (d.State == SyncState.RemoteOnly && d.IsDirectory) actions.Add(new SyncAction(SyncActionKind.CreateLocalFolder, d));
                    else if (!d.IsDirectory && (d.State != SyncState.LocalNewer || d.Local!.Size != d.Remote!.Size))
                        actions.Add(new SyncAction(SyncActionKind.Download, d));
                    break;
                default:
                    // Both ways: nothing is deleted.
                    if (d.State == SyncState.LocalOnly)
                        actions.Add(new SyncAction(d.IsDirectory ? SyncActionKind.CreateRemoteFolder : SyncActionKind.Upload, d));
                    else if (d.State == SyncState.RemoteOnly)
                        actions.Add(new SyncAction(d.IsDirectory ? SyncActionKind.CreateLocalFolder : SyncActionKind.Download, d));
                    else if (d.State == SyncState.LocalNewer) actions.Add(new SyncAction(SyncActionKind.Upload, d));
                    else if (d.State == SyncState.RemoteNewer) actions.Add(new SyncAction(SyncActionKind.Download, d));
                    // Different size with unknown/equal time: ambiguous, left to the user.
                    break;
            }
        }

        // Deletions: deepest first, and nothing below a folder that is deleted as a whole.
        var deleted = actions.Where(a => a.Kind is SyncActionKind.DeleteLocal or SyncActionKind.DeleteRemote && a.Diff.IsDirectory)
            .Select(a => a.Diff.RelativePath + "/").ToList();
        actions.RemoveAll(a => a.Kind is SyncActionKind.DeleteLocal or SyncActionKind.DeleteRemote &&
                               deleted.Any(p => a.Diff.RelativePath.StartsWith(p, StringComparison.Ordinal)));
        return actions;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
}

/// <summary>
/// Exclusions for folder compare &amp; sync, separated by ";" or new lines. A pattern without "/" matches any file or
/// folder name at any depth (".git", "*.tmp"); a pattern with "/" matches the relative path ("build/output", "docs/**/*.pdf").
/// Excluding a folder excludes everything below it. Wildcards: * (within a name), ** (across folders), ?.
/// </summary>
public sealed class SyncExcludes
{
    public const string Default = ".git; .svn; node_modules; .DS_Store; Thumbs.db; desktop.ini; *.tmp; ~$*";

    private readonly List<(Regex Regex, bool IsPath)> _patterns = [];

    public SyncExcludes(string? text, bool ignoreCase)
    {
        var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        foreach (var raw in (text ?? "").Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = raw.Replace('\\', '/').Trim('/');
            if (p.Length == 0) continue;
            _patterns.Add((new Regex("^" + ToRegex(p) + "$", options), p.Contains('/')));
        }
    }

    public bool IsEmpty => _patterns.Count == 0;

    private static string ToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                sb.Append(".*");
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/') i++;
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }

        return sb.ToString();
    }

    /// <summary>True when the path or one of its parent folders is excluded.</summary>
    public bool IsExcluded(string relativePath)
    {
        if (_patterns.Count == 0) return false;
        var path = relativePath.Replace('\\', '/').Trim('/');
        var segments = path.Split('/');
        for (var depth = 1; depth <= segments.Length; depth++)
        {
            var name = segments[depth - 1];
            var prefix = string.Join('/', segments.Take(depth));
            foreach (var (regex, isPath) in _patterns)
            {
                if (regex.IsMatch(isPath ? prefix : name)) return true;
            }
        }

        return false;
    }

    public IReadOnlyList<SyncDiff> Apply(IReadOnlyList<SyncDiff> diffs) => IsEmpty ? diffs : diffs.Where(d => !IsExcluded(d.RelativePath)).ToList();
}
