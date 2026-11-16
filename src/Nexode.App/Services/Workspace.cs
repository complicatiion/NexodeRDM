using System.Security.Cryptography;
using Nexode.Core.Model;
using Nexode.Core.Security;
using Nexode.Core.Sync;
using Nexode.Core.Theming;
using Nexode.Core.Tree;
using Nexode.Platform;
using Nexode.Storage;

namespace Nexode.App.Services;

/// <summary>Where a dragged or pasted entry lands.</summary>
public readonly record struct DropTarget(Guid? Parent, Guid? Anchor, bool Before)
{
    public static DropTarget Into(Guid? parent) => new(parent, null, false);
}

/// <summary>
/// The open data source of this process: in-memory model, persistence, vault and undo. All windows share one workspace;
/// every change raises <see cref="Changed"/> so trees and tabs refresh.
/// </summary>
public sealed class Workspace : IDisposable
{
    public const string PasswordField = "password";
    private const int UndoDepth = 50;

    private readonly AppSettings _settings;
    private readonly Func<double> _accentHue;
    private readonly Dictionary<Guid, Entry> _all = [];
    private readonly List<List<(Guid Id, Entry? Before)>> _undo = [];
    private HashSet<Guid> _secretIds = [];
    private List<Category> _customCategories = [];
    private List<EnvironmentDef> _environments = [];
    private List<ConnectionProfile> _profiles = [];
    private List<EntryTemplate> _templates = [];
    private PersonalState? _personal;
    private DateTime _lastHeartbeat = DateTime.UtcNow;

    public Workspace(AppSettings settings, Func<double> accentHue)
    {
        _settings = settings;
        _accentHue = accentHue;
        Clock = new HlcClock(settings.DeviceId);
        Tree = new EntryTree([]);
        Resolver = new Resolver(Tree);
    }

    public DataSourceStore? Store { get; private set; }
    public DataSourceInfo? Info { get; private set; }
    public Vault? Vault { get; private set; }
    public HlcClock Clock { get; }
    public EntryTree Tree { get; private set; }
    public Resolver Resolver { get; private set; }
    public bool IsNetworkStore { get; private set; }
    public string? OpenError { get; private set; }

    public IReadOnlyDictionary<string, Category> Categories { get; private set; } = new Dictionary<string, Category>();
    public IReadOnlyDictionary<string, EnvironmentDef> Environments { get; private set; } = new Dictionary<string, EnvironmentDef>();
    public IReadOnlyList<EnvironmentDef> EnvironmentList => [.. _environments.OrderBy(e => e.Order).ThenBy(e => e.Name)];
    public IReadOnlyList<ConnectionProfile> Profiles => [.. _profiles.OrderBy(p => p.Order).ThenBy(p => p.Name)];

    /// <summary>Entry templates of this data source (built-in ones come from <see cref="BuiltInTemplates"/>).</summary>
    public IReadOnlyList<EntryTemplate> Templates => [.. _templates.OrderBy(t => t.Order).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Team data source: local working copy and editor lock (null for ordinary sources).</summary>
    public SharedSource? Shared { get; private set; }
    /// <summary>Shared source opened by someone else: changes are blocked, personal state (favorites …) still works.</summary>
    public bool IsReadOnly => Shared is { IsEditor: false };
    /// <summary>Local copy has changes the share does not have yet.</summary>
    public bool PendingPublish { get; private set; }
    /// <summary>Raised when a change was refused because the source is read-only.</summary>
    public event Action? ReadOnlyBlocked;
    /// <summary>Raised when the editor role or the shared copy changed.</summary>
    public event Action? SharedStateChanged;
    public IEnumerable<Entry> Deleted => _all.Values.Where(e => e.IsDeleted).OrderByDescending(e => e.DeletedUtc);

    /// <summary>All entries including deleted ones (tombstones).</summary>
    public IReadOnlyCollection<Entry> AllEntries => _all.Values;
    public bool CanUndo => _undo.Count > 0;
    public bool IsLocked => Vault is not { IsUnlocked: true };
    public bool HasPassword => Vault?.HasPassword == true;
    /// <summary>Entries are stored encrypted; the tree stays empty while the vault is locked.</summary>
    public bool IsFullyEncrypted { get; private set; }
    /// <summary>Full encryption is active and the vault is locked: the structure is hidden.</summary>
    public bool IsContentHidden => IsFullyEncrypted && IsLocked;
    /// <summary>Failed unlock attempts in a row (drives the back-off delay).</summary>
    public int FailedUnlocks { get; private set; }

    /// <summary>Raised after every model change (on the calling thread).</summary>
    public event Action? Changed;
    public event Action? VaultChanged;
    public event Action? SourceChanged;

    // ------------------------------------------------------------------ open / close

    /// <summary>Opens a data source. <paramref name="newPassword"/> protects a new vault with a master password.</summary>
    public void Open(DataSourceInfo info, string? newPassword = null)
    {
        Close();
        OpenError = null;
        IsNetworkStore = Shell.IsNetworkPath(info.Path);
        string storePath = info.Path;
        if (info.Shared)
        {
            // Never open SQLite on the share: work on a local copy, publish it back.
            Shared = new SharedSource(info.Path, AppPaths.CacheDirectory, _settings.DeviceId + ":" + Environment.ProcessId,
                Environment.UserName, Environment.MachineName);
            try { Shared.Pull(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { OpenError = "SharedUnavailable"; }
            Shared.TryAcquire();
            storePath = Shared.CachePath;
            if (!Shared.RemoteExists) PendingPublish = true;
        }

        var store = DataSourceStore.Open(storePath, info.Name, IsNetworkStore && Shared is null);
        Store = store;
        Info = info;
        info.Name = store.Name;

        var header = store.ReadVaultHeader();
        var keyDir = AppPaths.KeyDirectory;
        if (header is null)
        {
            var protector = KeyProtectors.Create(keyDir);
            Vault = string.IsNullOrEmpty(newPassword)
                ? Vault.CreateSystem(store.Id, protector)
                : Vault.CreateWithPassword(store.Id, newPassword, protector);
            store.WriteVaultHeader(Vault.Header);
        }
        else
        {
            try
            {
                var protector = header.Mode == VaultMode.System ? KeyProtectors.ById(header.ProtectorId, keyDir) : KeyProtectors.Create(keyDir);
                Vault = Vault.Open(header, store.Id, protector);
            }
            catch (CryptographicException)
            {
                // The OS key belongs to another user or device: secrets stay unreadable, the structure opens.
                Vault = Vault.Open(header, store.Id, null);
                OpenError = "VaultKeyUnavailable";
            }
        }

        Vault.StateChanged += OnVaultStateChanged;
        IsFullyEncrypted = store.IsFullyEncrypted;
        FailedUnlocks = 0;
        store.Cipher = IsFullyEncrypted && Vault.IsUnlocked ? new VaultEntryCipher(Vault) : null;

        _personal = Shared is null ? null : PersonalState.Load(Path.Combine(AppPaths.StateDirectory, store.Id + ".json"));
        LoadEntries();
        LoadLists(store);
        _secretIds = store.EntriesWithSecrets();
        _undo.Clear();

        // Trash retention and the daily backup.
        if (_settings.TrashRetentionDays > 0 && !IsReadOnly)
        {
            var purged = store.PurgeDeleted(DateTime.UtcNow.AddDays(-_settings.TrashRetentionDays));
            if (purged > 0)
            {
                foreach (var id in _all.Values.Where(e => e.DeletedUtc < DateTime.UtcNow.AddDays(-_settings.TrashRetentionDays)).Select(e => e.Id).ToList())
                {
                    _all.Remove(id);
                }
            }
        }

        BackupIfDue();
        Rebuild();
        SourceChanged?.Invoke();
        VaultChanged?.Invoke();
    }

    /// <summary>Text for the vault state ("vault locked", "protected by system" …) is built by the UI; this is the raw mode.</summary>
    public string EncryptionMode => IsFullyEncrypted ? "full" : "secrets";

    private void LoadEntries()
    {
        _all.Clear();
        if (Store is null) return;
        foreach (var e in Store.LoadEntries()) _all[e.Id] = e;
        foreach (var e in _all.Values) Clock.Observe(e.Version);
        _personal?.ApplyTo(_all.Values);
    }

    private void LoadLists(DataSourceStore store)
    {
        _customCategories = LoadOrSeedCategories(store);
        _environments = store.LoadEnvironments();
        if (store.GetMeta("environments.seeded") is null && !IsReadOnly)
        {
            foreach (var d in EnvironmentDef.Defaults)
            {
                if (_environments.All(e => e.Id != d.Id))
                {
                    _environments.Add(new EnvironmentDef { Id = d.Id, Name = d.Name, Short = d.Short, Color = d.Color, BuiltIn = true, Order = d.Order });
                }
            }

            store.SaveEnvironments(_environments);
            store.SetMeta("environments.seeded", "1");
            PendingPublish |= Shared is not null;
        }

        _profiles = store.LoadProfiles();
        _templates = store.LoadTemplates();
    }

    /// <summary>Full encryption: unlocking loads the entries, locking removes them from memory.</summary>
    private void OnVaultStateChanged()
    {
        if (IsFullyEncrypted && Store is not null && Vault is not null)
        {
            Store.Cipher = Vault.IsUnlocked ? new VaultEntryCipher(Vault) : null;
            if (Vault.IsUnlocked)
            {
                LoadEntries();
                BackupIfDue();
            }
            else
            {
                _all.Clear();
            }

            _undo.Clear();
            Rebuild();
        }

        VaultChanged?.Invoke();
    }

    /// <summary>Built-in categories are copied into the data source once, so they can be renamed or deleted.</summary>
    private static List<Category> LoadOrSeedCategories(DataSourceStore store)
    {
        var list = store.LoadCategories();
        // "1" = the 11 categories of v0.0.2, "2" = with Container, Identity and Backup.
        var seeded = store.GetMeta("categories.seeded");
        if (seeded != "2")
        {
            foreach (var d in Category.Defaults.Where(d => seeded is null || d.Order >= 11))
            {
                if (list.All(c => c.Id != d.Id)) list.Add(new Category { Id = d.Id, Name = d.Name, Icon = d.Icon, BuiltIn = true, Order = d.Order });
            }

            try
            {
                store.SaveCategories(list);
                store.SetMeta("categories.seeded", "2");
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
            }
        }

        return list;
    }

    public void Close()
    {
        if (Shared is not null && Store is not null)
        {
            if (PendingPublish) PublishNow();
            Shared.Release();
        }

        Vault?.Dispose();
        Vault = null;
        Store?.Dispose();
        Store = null;
        Shared = null;
        PendingPublish = false;
        _personal = null;
        Info = null;
        _all.Clear();
        _undo.Clear();
    }

    public string BackupNow() =>
        Store?.Backup(Path.Combine(AppPaths.BackupDirectory, Store.Id), _settings.BackupKeep) ?? throw new InvalidOperationException();

    private void BackupIfDue()
    {
        if (Store is null || _settings.BackupKeep <= 0) return;
        var dir = Path.Combine(AppPaths.BackupDirectory, Store.Id);
        var last = Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles("*.db").Select(f => f.LastWriteTimeUtc).DefaultIfEmpty().Max() : default;
        if (_all.Count > 0 && DateTime.UtcNow - last > TimeSpan.FromHours(24))
        {
            try { BackupNow(); } catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) { }
        }
    }

    public void Rebuild()
    {
        Tree = new EntryTree(_all.Values, _settings.RootSort, _settings.FoldersFirst);
        Categories = _customCategories.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.Last());
        Environments = _environments.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.Last());
        Resolver = new Resolver(Tree, Categories, Environments);
        Changed?.Invoke();
    }

    public IReadOnlyList<Category> CategoryList => [.. Categories.Values.OrderBy(c => c.Order).ThenBy(c => c.Name)];

    /// <summary>Replaces all categories (built-in ones included – they are editable). Entries of removed categories fall back to none.</summary>
    public void SaveCategories(IEnumerable<Category> categories)
    {
        if (BlockedReadOnly()) return;
        _customCategories = categories.ToList();
        Store?.SaveCategories(_customCategories);
        Touched();
        var ids = _customCategories.Select(c => c.Id).ToHashSet();
        var orphans = _all.Values.Where(e => e.CategoryId is { } c && !ids.Contains(c)).Select(e => e.Id).ToList();
        if (orphans.Count > 0) Modify(orphans, e => e.CategoryId = null);
        else Rebuild();
    }

    /// <summary>Replaces all environments. Entries of removed environments fall back to "inherit".</summary>
    public void SaveEnvironments(IEnumerable<EnvironmentDef> environments)
    {
        if (BlockedReadOnly()) return;
        _environments = environments.ToList();
        Store?.SaveEnvironments(_environments);
        Touched();
        var ids = _environments.Select(c => c.Id).ToHashSet();
        var orphans = _all.Values.Where(e => e.EnvironmentId is { Length: > 0 } v && !ids.Contains(v)).Select(e => e.Id).ToList();
        if (orphans.Count > 0) Modify(orphans, e => e.EnvironmentId = null);
        else Rebuild();
    }

    public void SaveTemplates(IEnumerable<EntryTemplate> templates)
    {
        if (BlockedReadOnly()) return;
        _templates = templates.ToList();
        Store?.SaveTemplates(_templates);
        Touched();
        Rebuild();
    }

    public void SaveProfiles(IEnumerable<ConnectionProfile> profiles)
    {
        if (BlockedReadOnly()) return;
        _profiles = profiles.ToList();
        Store?.SaveProfiles(_profiles);
        Touched();
        Rebuild();
    }

    /// <summary>Entries of a profile that still exist (in profile order).</summary>
    public IReadOnlyList<Entry> ProfileEntries(ConnectionProfile p) => [.. p.Entries.Select(id => Tree.Get(id)).OfType<Entry>()];

    public Entry? Get(Guid? id) => id is { } v && _all.TryGetValue(v, out var e) ? e : null;

    // ------------------------------------------------------------------ create / update

    public Entry NewEntry(EntryKind kind, Guid? parentId)
    {
        var parent = Tree.Get(parentId);
        var entry = new Entry
        {
            Kind = kind,
            ParentId = parent?.IsContainer == true ? parent.Id : parent?.ParentId,
            Name = ""
        };
        if (kind == EntryKind.Folder && _settings.AutoFolderColors)
        {
            // Distinct from the siblings and from the folder it sits in.
            var used = Tree.Children(entry.ParentId).Where(e => e.Kind == EntryKind.Folder && e.ColorHue is not null).Select(e => e.ColorHue!.Value).ToList();
            if (Tree.Get(entry.ParentId) is { } container && Resolver.Resolve(container).ColorHue is { } parentHue) used.Add(parentHue);
            entry.ColorHue = FolderColors.Next(used, _accentHue());
        }

        if (kind == EntryKind.Credential) entry.CredentialMode = CredentialMode.Own;
        return entry;
    }

    /// <summary>Inserts or updates one entry (editor save). <paramref name="password"/>: null = unchanged, "" = remove.</summary>
    public void Save(Entry entry, string? password = null)
    {
        var existing = Get(entry.Id);
        if (existing is null && string.IsNullOrEmpty(entry.SortKey))
        {
            entry.SortKey = Tree.KeyAtEnd(entry.ParentId);
        }

        Commit([entry], [(entry.Id, existing?.Clone())]);
        if (password is not null) SetSecret(entry.Id, PasswordField, password);
    }

    /// <summary>Applies a change to several entries (color, icon, favorite …) as one undo step.</summary>
    public void Modify(IEnumerable<Guid> ids, Action<Entry> change)
    {
        var targets = ids.Select(id => Get(id)).OfType<Entry>().ToList();
        if (targets.Count == 0) return;
        var before = targets.Select(t => (t.Id, (Entry?)t.Clone())).ToList();
        var changed = targets.Select(t =>
        {
            var copy = t.Clone();
            change(copy);
            return copy;
        }).ToList();
        Commit(changed, before);
    }

    /// <summary>Persists entries without an undo step (UI state such as expanded folders).</summary>
    public void SaveState(Entry entry)
    {
        _all[entry.Id] = entry;
        SavePersonal([entry]);
    }

    public void MarkUsed(Guid id)
    {
        if (Get(id) is not { } e) return;
        e.LastUsedUtc = DateTime.UtcNow;
        Usage.Record(e);
        SavePersonal([e]);
        Rebuild();
    }

    /// <summary>
    /// Personal state (favorite, pin, expanded, usage): ordinary sources store it with the entry (one undo step),
    /// shared sources keep it on this computer only – also while read-only.
    /// </summary>
    public void ModifyPersonal(IReadOnlyList<Guid> ids, Action<Entry> change)
    {
        if (_personal is null)
        {
            Modify(ids, change);
            return;
        }

        var targets = ids.Select(id => Get(id)).OfType<Entry>().ToList();
        foreach (var t in targets) change(t);
        SavePersonal(targets);
        Rebuild();
    }

    private void SavePersonal(IReadOnlyList<Entry> entries)
    {
        if (_personal is not null)
        {
            _personal.Capture(entries);
            _personal.Save();
        }
        else if (!IsContentHidden)
        {
            Store?.Save(entries);
        }
    }

    // ------------------------------------------------------------------ shared data source

    private bool BlockedReadOnly()
    {
        if (!IsReadOnly) return false;
        ReadOnlyBlocked?.Invoke();
        return true;
    }

    private void Touched()
    {
        if (Shared is not null) PendingPublish = true;
    }

    /// <summary>Writes the local copy to the share (editor only).</summary>
    public PublishResult PublishNow()
    {
        if (Shared is null || Store is null) return PublishResult.NotEditor;
        var snapshot = Shared.CachePath + ".snapshot";
        try
        {
            Store.SnapshotTo(snapshot);
            var result = Shared.Publish(snapshot);
            if (result == PublishResult.Published) PendingPublish = false;
            if (result is PublishResult.Conflict or PublishResult.NotEditor) SharedStateChanged?.Invoke();
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return PublishResult.Failed;
        }
        finally
        {
            try { File.Delete(snapshot); } catch (IOException) { }
        }
    }

    /// <summary>Periodic work for shared sources: heartbeat (editor) or pulling changes (read-only).</summary>
    public void SharedTick()
    {
        if (Shared is null || Store is null) return;
        if (Shared.IsEditor)
        {
            if (DateTime.UtcNow - _lastHeartbeat > TimeSpan.FromSeconds(30))
            {
                _lastHeartbeat = DateTime.UtcNow;
                if (!Shared.Heartbeat())
                {
                    SharedStateChanged?.Invoke();
                    return;
                }
            }

            if (PendingPublish) PublishNow();
        }
        else if (Shared.RemoteChanged())
        {
            ReloadShared();
        }
    }

    /// <summary>Tries to become the editor (<paramref name="force"/>: take over a foreign lock). Pulls the latest copy first.</summary>
    public bool TakeOverEditing(bool force)
    {
        if (Shared is null) return false;
        if (Shared.RemoteChanged()) ReloadShared();
        var ok = Shared.TryAcquire(force);
        SharedStateChanged?.Invoke();
        return ok;
    }

    /// <summary>Re-reads the shared file into the local copy (read-only clients).</summary>
    public void ReloadShared()
    {
        if (Shared is null || Store is null || Info is null) return;
        var cipher = Store.Cipher;
        Store.Dispose();
        try
        {
            Shared.Pull();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        Store = DataSourceStore.Open(Shared.CachePath, Info.Name);
        IsFullyEncrypted = Store.IsFullyEncrypted;
        Store.Cipher = IsFullyEncrypted && Vault is { IsUnlocked: true } v ? cipher ?? new VaultEntryCipher(v) : null;
        LoadEntries();
        LoadLists(Store);
        _secretIds = Store.EntriesWithSecrets();
        _undo.Clear();
        Rebuild();
        SharedStateChanged?.Invoke();
    }

    // ------------------------------------------------------------------ move / copy / delete

    /// <summary>True when the target is allowed (no folder into itself or its descendants).</summary>
    public bool CanDrop(IReadOnlyCollection<Guid> ids, DropTarget target) =>
        target.Parent is not { } parent || ids.All(id => !Tree.IsSelfOrDescendant(id, parent));

    public void Move(IReadOnlyList<Guid> ids, DropTarget target)
    {
        if (!CanDrop(ids, target)) return;
        var moving = OrderLikeTree(ids).ToList();
        if (moving.Count == 0) return;
        var keys = KeysFor(target, moving.Count, moving.Select(m => m.Id).ToHashSet());
        var before = moving.Select(m => (m.Id, (Entry?)m.Clone())).ToList();
        var changed = moving.Select((m, i) =>
        {
            var copy = m.Clone();
            copy.ParentId = target.Parent;
            copy.SortKey = keys[i];
            return copy;
        }).ToList();
        Commit(changed, before);
    }

    /// <summary>Deep copy (with children and secrets). Returns the new top-level copies.</summary>
    public List<Entry> Copy(IReadOnlyList<Guid> ids, DropTarget target, bool markAsCopy)
    {
        var roots = OrderLikeTree(ids).Where(r => !ids.Any(o => o != r.Id && Tree.IsSelfOrDescendant(o, r.ParentId))).ToList();
        var keys = KeysFor(target, roots.Count, null);
        var created = new List<Entry>();
        var tops = new List<Entry>();
        var secretCopies = new List<(Guid From, Guid To)>();

        for (var i = 0; i < roots.Count; i++)
        {
            var map = new Dictionary<Guid, Guid>();
            foreach (var source in new[] { roots[i] }.Concat(Tree.Descendants(roots[i].Id)))
            {
                var copy = source.Clone();
                copy.Id = Guid.CreateVersion7();
                map[source.Id] = copy.Id;
                copy.ParentId = source.Id == roots[i].Id ? target.Parent : map[source.ParentId!.Value];
                copy.IsFavorite = false;
                copy.LastUsedUtc = null;
                copy.UseDays = [];
                copy.CreatedUtc = DateTime.UtcNow;
                if (source.Id == roots[i].Id)
                {
                    copy.SortKey = keys[i];
                    if (markAsCopy) copy.Name = Utils.CopyName(copy.Name, Tree.Children(target.Parent).Select(c => c.Name));
                    tops.Add(copy);
                }

                created.Add(copy);
                if (_secretIds.Contains(source.Id)) secretCopies.Add((source.Id, copy.Id));
            }
        }

        Commit(created, created.Select(c => (c.Id, (Entry?)null)).ToList());
        if (!IsLocked)
        {
            foreach (var (from, to) in secretCopies)
            {
                if (GetSecret(from, PasswordField) is { } pw) SetSecret(to, PasswordField, pw);
            }
        }

        return tops;
    }

    public void Delete(IEnumerable<Guid> ids)
    {
        var set = new HashSet<Guid>();
        foreach (var id in ids)
        {
            if (Tree.Get(id) is not { } e) continue;
            set.Add(e.Id);
            foreach (var d in Tree.Descendants(e.Id)) set.Add(d.Id);
        }

        if (set.Count == 0) return;
        var now = DateTime.UtcNow;
        var before = set.Select(id => (id, (Entry?)_all[id].Clone())).ToList();
        var changed = set.Select(id =>
        {
            var c = _all[id].Clone();
            c.DeletedUtc = now;
            return c;
        }).ToList();
        Commit(changed, before);
    }

    public void Restore(IEnumerable<Guid> ids)
    {
        var restore = new List<Entry>();
        var before = new List<(Guid, Entry?)>();
        foreach (var id in ids)
        {
            if (Get(id) is not { IsDeleted: true } e) continue;
            var batch = e.DeletedUtc;
            // Restore the entry with everything deleted together with it.
            foreach (var item in _all.Values.Where(x => x.DeletedUtc == batch && (x.Id == e.Id || IsBelow(x, e.Id))))
            {
                before.Add((item.Id, item.Clone()));
                var c = item.Clone();
                c.DeletedUtc = null;
                if (c.Id == e.Id && c.ParentId is { } p && Get(p) is not { IsDeleted: false }) c.ParentId = null;
                restore.Add(c);
            }
        }

        Commit(restore, before);
    }

    private bool IsBelow(Entry e, Guid ancestor)
    {
        var current = Get(e.ParentId);
        var guard = 0;
        while (current is not null && guard++ < 10_000)
        {
            if (current.Id == ancestor) return true;
            current = Get(current.ParentId);
        }

        return false;
    }

    public int EmptyTrash()
    {
        if (Store is null || BlockedReadOnly()) return 0;
        Touched();
        var n = Store.PurgeDeleted(DateTime.UtcNow.AddMinutes(1));
        foreach (var id in _all.Values.Where(e => e.IsDeleted).Select(e => e.Id).ToList())
        {
            _all.Remove(id);
            _secretIds.Remove(id);
        }

        _undo.Clear();
        Rebuild();
        return n;
    }

    // ------------------------------------------------------------------ undo

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        var restored = new List<Entry>();
        foreach (var (id, before) in step)
        {
            Entry e;
            if (before is null)
            {
                if (Get(id) is not { } created) continue;
                e = created.Clone();
                e.DeletedUtc = DateTime.UtcNow;
            }
            else
            {
                e = before.Clone();
            }

            e.Version = Clock.Next();
            restored.Add(e);
        }

        if (BlockedReadOnly()) return false;
        Touched();
        foreach (var e in restored) _all[e.Id] = e;
        Store?.Save(restored);
        Rebuild();
        return true;
    }

    // ------------------------------------------------------------------ secrets

    public bool HasSecret(Guid? id) => id is { } v && _secretIds.Contains(v);

    /// <summary>Plain secret; null when missing or the vault is locked.</summary>
    public string? GetSecret(Guid id, string field)
    {
        if (Vault is not { IsUnlocked: true } vault || Store?.GetSecret(id, field) is not { } blob) return null;
        try
        {
            return vault.DecryptSecret(id, field, blob);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void SetSecret(Guid id, string field, string? value)
    {
        if (Store is null || BlockedReadOnly()) return;
        Touched();
        if (string.IsNullOrEmpty(value))
        {
            Store.SetSecret(id, field, null);
            _secretIds.Remove(id);
            return;
        }

        if (Vault is not { IsUnlocked: true } vault) throw new InvalidOperationException("Vault is locked.");
        Store.SetSecret(id, field, vault.EncryptSecret(id, field, value), Clock.Next().ToString());
        _secretIds.Add(id);
    }

    public bool TryUnlock(string password)
    {
        var ok = Vault?.TryUnlock(password) == true;
        FailedUnlocks = ok ? 0 : FailedUnlocks + 1;
        return ok;
    }

    /// <summary>Unlock off the UI thread (Argon2id takes ~0.7 s) with an increasing delay after failed attempts.</summary>
    public async Task<bool> TryUnlockAsync(string password)
    {
        if (Vault is not { } vault) return false;
        var delay = FailedUnlocks < 3 ? 0 : Math.Min(30, 1 << Math.Min(5, FailedUnlocks - 3));
        if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay));
        var ok = await Task.Run(() => vault.TryUnlockQuiet(password));
        FailedUnlocks = ok ? 0 : FailedUnlocks + 1;
        if (ok) vault.RaiseStateChanged();
        return ok;
    }

    /// <summary>Verifies the master password without changing the lock state (re-authentication).</summary>
    public Task<bool> VerifyPasswordAsync(string password) => Vault is { } v ? Task.Run(() => v.VerifyPassword(password)) : Task.FromResult(false);

    /// <summary>Switches full encryption on or off (requires an unlocked vault with a master password). Backup first.</summary>
    public void SetFullEncryption(bool on)
    {
        if (Store is null || Vault is not { IsUnlocked: true } vault) throw new InvalidOperationException("Vault is locked.");
        if (on && !vault.HasPassword) throw new InvalidOperationException("A master password is required.");
        if (on == IsFullyEncrypted) return;
        if (BlockedReadOnly()) return;
        Touched();
        try { BackupNow(); } catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) { }
        Store.Reencrypt([.. _all.Values], on ? new VaultEntryCipher(vault) : null);
        IsFullyEncrypted = on;
        VaultChanged?.Invoke();
    }

    public void Lock() => Vault?.Lock();

    public void SetMasterPassword(string password)
    {
        if (Vault is null || Store is null || BlockedReadOnly()) return;
        Store.WriteVaultHeader(Vault.SetPassword(password));
        Touched();
    }

    public void RemoveMasterPassword()
    {
        if (Vault is null || Store is null || BlockedReadOnly()) return;
        Touched();
        if (IsFullyEncrypted) SetFullEncryption(false);
        Store.WriteVaultHeader(Vault.RemovePassword());
    }

    // ------------------------------------------------------------------ bulk import

    public void ImportEntries(IReadOnlyList<Entry> entries, IEnumerable<(Guid Id, string Field, string Value)> secrets)
    {
        var before = entries.Select(e => (e.Id, Get(e.Id)?.Clone())).ToList();
        Commit(entries, before, stamp: false);
        foreach (var (id, field, value) in secrets) SetSecret(id, field, value);
    }

    // ------------------------------------------------------------------ LAN sync

    /// <summary>
    /// Everything for a LAN sync: all entries including tombstones, plain passwords (vault must be unlocked) and the
    /// lists. Null with a reason when this source cannot sync.
    /// </summary>
    public (LanSnapshot? Snapshot, string? Refused) BuildLanSnapshot(string deviceName, string appVersion)
    {
        if (Store is null) return (null, "nosource");
        if (Shared is not null) return (null, "shared");
        if (IsLocked) return (null, "locked");
        var snap = new LanSnapshot
        {
            DeviceId = _settings.DeviceId,
            DeviceName = deviceName,
            SourceName = Info?.Name ?? "",
            AppVersion = appVersion,
            Entries = _all.Values.Select(e => e.Clone()).ToList(),
            Categories = [.. CategoryList],
            Environments = [.. _environments],
            Profiles = [.. _profiles],
            Templates = [.. _templates]
        };
        foreach (var id in _secretIds)
        {
            if (_all.ContainsKey(id) && GetSecret(id, PasswordField) is { Length: > 0 } pw) snap.Passwords[id] = pw;
        }

        return (snap, null);
    }

    /// <summary>Writes a LAN sync result (remote versions are kept, so both sides end up equal).</summary>
    public void ApplyLanMerge(LanMergeResult r)
    {
        if (Store is null || BlockedReadOnly()) return;
        foreach (var e in r.Take) Clock.Observe(e.Version);
        if (r.Take.Count > 0)
        {
            var before = r.Take.Select(e => (e.Id, Get(e.Id)?.Clone())).ToList();
            Commit(r.Take, before, stamp: false);
        }

        foreach (var (id, pw) in r.Passwords)
        {
            if (pw is not null) SetSecret(id, PasswordField, pw);
            else if (HasSecret(id)) SetSecret(id, PasswordField, null);
        }

        if (r.NewCategories.Count > 0) SaveCategories([.. CategoryList, .. r.NewCategories]);
        if (r.NewEnvironments.Count > 0) SaveEnvironments([.. _environments, .. r.NewEnvironments]);
        if (r.NewProfiles.Count > 0) SaveProfiles([.. _profiles, .. r.NewProfiles]);
        if (r.NewTemplates.Count > 0) SaveTemplates([.. _templates, .. r.NewTemplates]);
        Changed?.Invoke();
    }

    public void ReplaceAll()
    {
        var now = DateTime.UtcNow;
        var live = _all.Values.Where(e => !e.IsDeleted).Select(e =>
        {
            var c = e.Clone();
            c.DeletedUtc = now;
            return c;
        }).ToList();
        Commit(live, live.Select(e => (e.Id, Get(e.Id)?.Clone())).ToList());
    }

    // ------------------------------------------------------------------ helpers

    private void Commit(IReadOnlyList<Entry> changed, List<(Guid, Entry?)> before, bool stamp = true)
    {
        if (changed.Count == 0 || IsContentHidden) return;
        if (BlockedReadOnly()) return;
        Touched();
        foreach (var e in changed)
        {
            if (stamp || e.Version == Hlc.Zero) e.Version = Clock.Next();
            _all[e.Id] = e;
        }

        Store?.Save(changed);
        _personal?.Capture(changed);
        _undo.Add(before);
        if (_undo.Count > UndoDepth) _undo.RemoveAt(0);
        Rebuild();
    }

    private IEnumerable<Entry> OrderLikeTree(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        var order = new Dictionary<Guid, int>();
        var i = 0;
        void Walk(Guid? parent)
        {
            foreach (var c in Tree.Children(parent))
            {
                order[c.Id] = i++;
                Walk(c.Id);
            }
        }

        Walk(null);
        return set.Select(id => Tree.Get(id)).OfType<Entry>().OrderBy(e => order.GetValueOrDefault(e.Id, int.MaxValue));
    }

    private List<string> KeysFor(DropTarget target, int count, IReadOnlySet<Guid>? exclude)
    {
        var siblings = Tree.Children(target.Parent).Where(e => exclude is null || !exclude.Contains(e.Id))
            .OrderBy(e => e.SortKey, StringComparer.Ordinal).ToList();
        string? low = siblings.Count > 0 ? siblings[^1].SortKey : null;
        string? high = null;
        if (target.Anchor is { } anchor)
        {
            var index = siblings.FindIndex(s => s.Id == anchor);
            if (index >= 0)
            {
                if (target.Before)
                {
                    low = index > 0 ? siblings[index - 1].SortKey : null;
                    high = siblings[index].SortKey;
                }
                else
                {
                    low = siblings[index].SortKey;
                    high = index + 1 < siblings.Count ? siblings[index + 1].SortKey : null;
                }
            }
        }

        var keys = new List<string>(count);
        for (var k = 0; k < count; k++)
        {
            low = SortKeys.Between(low, high);
            keys.Add(low);
        }

        return keys;
    }

    // ------------------------------------------------------------------ pinning

    public IReadOnlyList<Entry> Pinned => [.. Tree.All.Where(e => e.IsPinned).OrderBy(e => e.PinKey, StringComparer.Ordinal).ThenBy(e => e.Name)];

    /// <summary>Personal pin color (null = entry color).</summary>
    public void SetPinHue(Guid id, double? hue) => ModifyPersonal([id], e => e.PinHue = hue);

    public void SetPinned(IReadOnlyList<Guid> ids, bool pinned, Guid? before = null)
    {
        var order = Pinned.Where(e => !ids.Contains(e.Id)).ToList();
        if (!pinned)
        {
            ModifyPersonal(ids, e =>
            {
                e.IsPinned = false;
                e.PinKey = "";
                e.PinHue = null;
            });
            return;
        }

        var index = before is { } b ? order.FindIndex(e => e.Id == b) : -1;
        var low = index > 0 ? order[index - 1].PinKey : index == 0 ? null : order.LastOrDefault()?.PinKey;
        var high = index >= 0 ? order[index].PinKey : null;
        if (string.IsNullOrEmpty(low)) low = null;
        if (string.IsNullOrEmpty(high)) high = null;
        var keys = new Dictionary<Guid, string>();
        foreach (var id in ids)
        {
            low = SortKeys.Between(low, high);
            keys[id] = low;
        }

        ModifyPersonal(ids, e =>
        {
            e.IsPinned = true;
            e.PinKey = keys[e.Id];
        });
    }

    // ------------------------------------------------------------------ transfer between data sources

    /// <summary>Copies entries (with descendants and passwords) into another data source; removes them here when <paramref name="move"/>.</summary>
    public int TransferTo(DataSourceStore target, Vault targetVault, IReadOnlyList<Guid> ids, bool move)
    {
        if (Vault is not { IsUnlocked: true }) throw new InvalidOperationException("Vault is locked.");
        var roots = OrderLikeTree(ids).Where(r => !ids.Any(o => o != r.Id && IsBelow(r, o))).ToList();
        var copies = new List<Entry>();
        var secrets = new List<(Guid, string)>();
        var targetEntries = target.LoadEntries();
        string? last = targetEntries.Where(e => e.ParentId is null && !e.IsDeleted).Select(e => e.SortKey).DefaultIfEmpty(null).Max(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            var map = new Dictionary<Guid, Guid>();
            foreach (var source in new[] { root }.Concat(Tree.Descendants(root.Id)))
            {
                var copy = source.Clone();
                copy.Id = Guid.CreateVersion7();
                map[source.Id] = copy.Id;
                copy.ParentId = source.Id == root.Id ? null : map[source.ParentId!.Value];
                if (source.Id == root.Id)
                {
                    // Inherited credentials do not exist in the target: take them along.
                    var eff = Resolver.Resolve(source);
                    if (source.CredentialMode == CredentialMode.Inherit && eff.CredentialFrom is not null)
                    {
                        copy.CredentialMode = CredentialMode.Own;
                        copy.Username = eff.Username;
                        copy.Domain = eff.Domain;
                    }

                    last = SortKeys.Between(last, null);
                    copy.SortKey = last;
                    if (Resolver.Resolve(source).SecretOwner is { } owner && GetSecret(owner, PasswordField) is { } pw) secrets.Add((copy.Id, pw));
                }
                else if (_secretIds.Contains(source.Id) && GetSecret(source.Id, PasswordField) is { } pw)
                {
                    secrets.Add((copy.Id, pw));
                }

                copy.IsPinned = false;
                copy.PinHue = null;
                copy.CredentialId = null;
                copy.Version = Clock.Next();
                copies.Add(copy);
            }
        }

        target.Save(copies);
        foreach (var (id, pw) in secrets) target.SetSecret(id, PasswordField, targetVault.EncryptSecret(id, PasswordField, pw), Clock.Next().ToString());
        if (move) Delete(roots.Select(r => r.Id));
        return copies.Count;
    }

    public void Dispose() => Close();
}

/// <summary>Full data source encryption with the vault's data key.</summary>
internal sealed class VaultEntryCipher(Vault vault) : IEntryCipher
{
    private const string Purpose = "entry";
    public string Protect(Guid id, string json) => vault.EncryptText(id, Purpose, json);
    public string? Unprotect(Guid id, string data) => vault.DecryptText(id, Purpose, data);
}

public static class Utils
{
    public static string CopyName(string name, IEnumerable<string> existing)
    {
        var set = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = name + " (2)";
        for (var i = 2; set.Contains(candidate) && i < 1000; i++) candidate = $"{name} ({i})";
        return candidate;
    }
}
