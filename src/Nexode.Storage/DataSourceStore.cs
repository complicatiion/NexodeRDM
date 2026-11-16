using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Nexode.Core.Files;
using Nexode.Core.Model;
using Nexode.Core.Security;

namespace Nexode.Storage;

/// <summary>Encrypts whole entries ("full data source encryption"); provided by the unlocked vault.</summary>
public interface IEntryCipher
{
    string Protect(Guid id, string json);
    /// <summary>Null when the data cannot be decrypted.</summary>
    string? Unprotect(Guid id, string data);
}

/// <summary>
/// One data source = one SQLite file (WAL). Entries are stored as JSON with indexed key columns, secrets as
/// AES-GCM blobs. All access goes through one connection owned by the app process.
/// </summary>
public sealed class DataSourceStore : IDisposable
{
    public const int SchemaVersion = 3;
    private readonly SqliteConnection _db;
    private readonly Lock _gate = new();

    private DataSourceStore(string path, SqliteConnection db)
    {
        Path = path;
        _db = db;
    }

    public string Path { get; }

    /// <summary>Set while full encryption is active and the vault is unlocked; null = entries are stored readable.</summary>
    public IEntryCipher? Cipher { get; set; }

    /// <summary>True when the entries are stored encrypted (meta "encryption" = "full").</summary>
    public bool IsFullyEncrypted => GetMeta("encryption") == "full";

    private const string EncryptedPrefix = "enc:";
    public string Id => GetMeta("id") ?? "";
    public string Name
    {
        get => GetMeta("name") ?? System.IO.Path.GetFileNameWithoutExtension(Path);
        set => SetMeta("name", value);
    }

    /// <summary>Opens or creates a data source file.</summary>
    public static DataSourceStore Open(string path, string? nameForNew = null, bool networkSafe = false)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        var db = new SqliteConnection(builder.ToString());
        db.Open();
        var store = new DataSourceStore(path, db);
        // WAL does not work on network file systems: exclusive rollback journal there.
        store.Exec(networkSafe ? "PRAGMA journal_mode=DELETE; PRAGMA locking_mode=EXCLUSIVE;" : "PRAGMA journal_mode=WAL;");
        store.Exec("PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
        store.Migrate(nameForNew);
        return store;
    }

    private void Migrate(string? nameForNew)
    {
        lock (_gate)
        {
            var version = Convert.ToInt32(Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);
            if (version >= SchemaVersion) return;

            using var tx = _db.BeginTransaction();
            if (version < 1)
            {
                Exec("""
                     CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
                     CREATE TABLE IF NOT EXISTS entries (
                         id TEXT PRIMARY KEY,
                         parent_id TEXT,
                         kind INTEGER NOT NULL,
                         name TEXT NOT NULL,
                         host TEXT,
                         sort_key TEXT NOT NULL DEFAULT '',
                         version TEXT NOT NULL DEFAULT '',
                         deleted_utc TEXT,
                         data TEXT NOT NULL);
                     CREATE INDEX IF NOT EXISTS ix_entries_parent ON entries(parent_id);
                     CREATE INDEX IF NOT EXISTS ix_entries_version ON entries(version);
                     CREATE TABLE IF NOT EXISTS secrets (
                         entry_id TEXT NOT NULL,
                         field TEXT NOT NULL,
                         blob BLOB NOT NULL,
                         version TEXT NOT NULL DEFAULT '',
                         PRIMARY KEY (entry_id, field));
                     CREATE TABLE IF NOT EXISTS categories (id TEXT PRIMARY KEY, data TEXT NOT NULL);
                     """, tx);
                SetMeta("id", Guid.CreateVersion7().ToString("N"), tx);
                SetMeta("name", nameForNew ?? System.IO.Path.GetFileNameWithoutExtension(Path), tx);
                SetMeta("created", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), tx);
            }

            if (version < 2)
            {
                Exec("""
                     CREATE TABLE IF NOT EXISTS environments (id TEXT PRIMARY KEY, data TEXT NOT NULL);
                     CREATE TABLE IF NOT EXISTS profiles (id TEXT PRIMARY KEY, data TEXT NOT NULL);
                     """, tx);
            }

            if (version < 3)
            {
                Exec("CREATE TABLE IF NOT EXISTS templates (id TEXT PRIMARY KEY, data TEXT NOT NULL);", tx);
            }

            Exec($"PRAGMA user_version={SchemaVersion};", tx);
            tx.Commit();
        }
    }

    // ------------------------------------------------------------------ meta / vault

    public string? GetMeta(string key)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT value FROM meta WHERE key=$k");
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string? value, SqliteTransaction? tx = null)
    {
        lock (_gate)
        {
            using var cmd = Command(value is null ? "DELETE FROM meta WHERE key=$k" : "INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v", tx);
            cmd.Parameters.AddWithValue("$k", key);
            if (value is not null) cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    public VaultHeader? ReadVaultHeader()
    {
        var json = GetMeta("vault");
        return json is null ? null : JsonSerializer.Deserialize(json, NexodeJson.Default.VaultHeader);
    }

    public void WriteVaultHeader(VaultHeader header) =>
        SetMeta("vault", JsonSerializer.Serialize(header, NexodeJson.Default.VaultHeader));

    // ------------------------------------------------------------------ entries

    public List<Entry> LoadEntries()
    {
        lock (_gate)
        {
            var list = new List<Entry>();
            using var cmd = Command("SELECT id, data FROM entries");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    var data = reader.GetString(1);
                    if (data.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
                    {
                        if (Cipher is null || !Guid.TryParse(reader.GetString(0), out var id)) continue;
                        data = Cipher.Unprotect(id, data[EncryptedPrefix.Length..]) ?? "";
                        if (data.Length == 0) continue;
                    }

                    if (JsonSerializer.Deserialize(data, NexodeJson.Default.Entry) is { } e) list.Add(e);
                }
                catch (JsonException)
                {
                    // A damaged row must not stop the data source from opening.
                }
            }

            return list;
        }
    }

    public void Save(IEnumerable<Entry> entries)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = Command("""
                                    INSERT INTO entries(id,parent_id,kind,name,host,sort_key,version,deleted_utc,data)
                                    VALUES($id,$p,$k,$n,$h,$s,$v,$d,$data)
                                    ON CONFLICT(id) DO UPDATE SET parent_id=$p,kind=$k,name=$n,host=$h,sort_key=$s,version=$v,deleted_utc=$d,data=$data
                                    """, tx);
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pParent = cmd.Parameters.Add("$p", SqliteType.Text);
            var pKind = cmd.Parameters.Add("$k", SqliteType.Integer);
            var pName = cmd.Parameters.Add("$n", SqliteType.Text);
            var pHost = cmd.Parameters.Add("$h", SqliteType.Text);
            var pSort = cmd.Parameters.Add("$s", SqliteType.Text);
            var pVersion = cmd.Parameters.Add("$v", SqliteType.Text);
            var pDeleted = cmd.Parameters.Add("$d", SqliteType.Text);
            var pData = cmd.Parameters.Add("$data", SqliteType.Text);
            foreach (var e in entries)
            {
                pId.Value = e.Id.ToString();
                pParent.Value = (object?)e.ParentId?.ToString() ?? DBNull.Value;
                pKind.Value = (int)e.Kind;
                var json = JsonSerializer.Serialize(e, NexodeJson.Default.Entry);
                pName.Value = Cipher is null ? e.Name : "";
                pHost.Value = Cipher is null ? (object?)e.Host ?? DBNull.Value : DBNull.Value;
                pSort.Value = e.SortKey;
                pVersion.Value = e.Version.ToString();
                pDeleted.Value = (object?)e.DeletedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value;
                pData.Value = Cipher is null ? json : EncryptedPrefix + Cipher.Protect(e.Id, json);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>Rewrites every entry with the given cipher (null = readable) in one transaction and records the mode.</summary>
    public void Reencrypt(IReadOnlyList<Entry> all, IEntryCipher? cipher)
    {
        lock (_gate)
        {
            var previous = Cipher;
            Cipher = cipher;
            try
            {
                Save(all);
                SetMeta("encryption", cipher is null ? null : "full");
                Exec("VACUUM;");
            }
            catch
            {
                Cipher = previous;
                throw;
            }
        }
    }

    /// <summary>Number of stored entries (also when they cannot be read).</summary>
    public int CountEntries()
    {
        lock (_gate) return Convert.ToInt32(Scalar("SELECT COUNT(*) FROM entries WHERE deleted_utc IS NULL"), CultureInfo.InvariantCulture);
    }

    /// <summary>Removes tombstones (and their secrets) deleted before <paramref name="olderThanUtc"/>.</summary>
    public int PurgeDeleted(DateTime olderThanUtc)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            var cutoff = olderThanUtc.ToString("O", CultureInfo.InvariantCulture);
            using (var del = Command("DELETE FROM secrets WHERE entry_id IN (SELECT id FROM entries WHERE deleted_utc IS NOT NULL AND deleted_utc < $c)", tx))
            {
                del.Parameters.AddWithValue("$c", cutoff);
                del.ExecuteNonQuery();
            }

            using var cmd = Command("DELETE FROM entries WHERE deleted_utc IS NOT NULL AND deleted_utc < $c", tx);
            cmd.Parameters.AddWithValue("$c", cutoff);
            var n = cmd.ExecuteNonQuery();
            tx.Commit();
            return n;
        }
    }

    // ------------------------------------------------------------------ secrets

    public byte[]? GetSecret(Guid entryId, string field)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT blob FROM secrets WHERE entry_id=$e AND field=$f");
            cmd.Parameters.AddWithValue("$e", entryId.ToString());
            cmd.Parameters.AddWithValue("$f", field);
            return cmd.ExecuteScalar() as byte[];
        }
    }

    public bool HasSecret(Guid entryId, string field) => GetSecret(entryId, field) is { Length: > 0 };

    public void SetSecret(Guid entryId, string field, byte[]? blob, string version = "")
    {
        lock (_gate)
        {
            using var cmd = Command(blob is null
                ? "DELETE FROM secrets WHERE entry_id=$e AND field=$f"
                : "INSERT INTO secrets(entry_id,field,blob,version) VALUES($e,$f,$b,$v) ON CONFLICT(entry_id,field) DO UPDATE SET blob=$b,version=$v");
            cmd.Parameters.AddWithValue("$e", entryId.ToString());
            cmd.Parameters.AddWithValue("$f", field);
            if (blob is not null)
            {
                cmd.Parameters.AddWithValue("$b", blob);
                cmd.Parameters.AddWithValue("$v", version);
            }

            cmd.ExecuteNonQuery();
        }
    }

    public HashSet<Guid> EntriesWithSecrets()
    {
        lock (_gate)
        {
            var set = new HashSet<Guid>();
            using var cmd = Command("SELECT DISTINCT entry_id FROM secrets");
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (Guid.TryParse(r.GetString(0), out var id)) set.Add(id);
            }

            return set;
        }
    }

    // ------------------------------------------------------------------ categories

    public List<Category> LoadCategories() => LoadList("categories", NexodeJson.Default.Category);

    public void SaveCategories(IEnumerable<Category> categories) => SaveList("categories", categories, c => c.Id, NexodeJson.Default.Category);

    public List<EnvironmentDef> LoadEnvironments() => LoadList("environments", NexodeJson.Default.EnvironmentDef);

    public void SaveEnvironments(IEnumerable<EnvironmentDef> items) => SaveList("environments", items, c => c.Id, NexodeJson.Default.EnvironmentDef);

    public List<ConnectionProfile> LoadProfiles() => LoadList("profiles", NexodeJson.Default.ConnectionProfile);

    public void SaveProfiles(IEnumerable<ConnectionProfile> items) => SaveList("profiles", items, c => c.Id, NexodeJson.Default.ConnectionProfile);

    public List<EntryTemplate> LoadTemplates() => LoadList("templates", NexodeJson.Default.EntryTemplate);

    public void SaveTemplates(IEnumerable<EntryTemplate> items) => SaveList("templates", items, c => c.Id, NexodeJson.Default.EntryTemplate);

    private List<T> LoadList<T>(string table, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        lock (_gate)
        {
            var list = new List<T>();
            using var cmd = Command($"SELECT data FROM {table}");
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                try
                {
                    if (JsonSerializer.Deserialize(r.GetString(0), info) is { } c) list.Add(c);
                }
                catch (JsonException)
                {
                }
            }

            return list;
        }
    }

    private void SaveList<T>(string table, IEnumerable<T> items, Func<T, string> id, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            Exec($"DELETE FROM {table}", tx);
            using var cmd = Command($"INSERT OR REPLACE INTO {table}(id,data) VALUES($i,$d)", tx);
            var pi = cmd.Parameters.Add("$i", SqliteType.Text);
            var pd = cmd.Parameters.Add("$d", SqliteType.Text);
            foreach (var c in items)
            {
                pi.Value = id(c);
                pd.Value = JsonSerializer.Serialize(c, info);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    // ------------------------------------------------------------------ backup

    /// <summary>Consistent copy of the open database (VACUUM INTO); keeps the newest <paramref name="keep"/> files.</summary>
    public string Backup(string directory, int keep)
    {
        Directory.CreateDirectory(directory);
        var baseName = System.IO.Path.GetFileNameWithoutExtension(Path);
        var target = System.IO.Path.Combine(directory, $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}.db");
        lock (_gate)
        {
            using var cmd = Command("VACUUM INTO $t");
            cmd.Parameters.AddWithValue("$t", target);
            cmd.ExecuteNonQuery();
        }

        foreach (var old in new DirectoryInfo(directory).GetFiles($"{baseName}_*.db").OrderByDescending(f => f.Name).Skip(Math.Max(1, keep)))
        {
            try { old.Delete(); } catch (IOException) { }
        }

        return target;
    }

    /// <summary>Integrity check plus VACUUM; "ok" or the first problem SQLite reports.</summary>
    public string CheckAndOptimize()
    {
        lock (_gate)
        {
            var result = Scalar("PRAGMA integrity_check;") as string ?? "?";
            if (result == "ok") Exec("VACUUM;");
            return result;
        }
    }

    /// <summary>Consistent copy of the database into <paramref name="target"/> (overwritten).</summary>
    public void SnapshotTo(string target)
    {
        if (File.Exists(target)) File.Delete(target);
        lock (_gate)
        {
            using var cmd = Command("VACUUM INTO $t");
            cmd.Parameters.AddWithValue("$t", target);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Flushes the WAL into the main file (before copying the file elsewhere).</summary>
    public void Checkpoint()
    {
        lock (_gate) Exec("PRAGMA wal_checkpoint(TRUNCATE);");
    }

    // ------------------------------------------------------------------ helpers

    private SqliteCommand Command(string sql, SqliteTransaction? tx = null)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private void Exec(string sql, SqliteTransaction? tx = null)
    {
        using var cmd = Command(sql, tx);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var cmd = Command(sql);
        return cmd.ExecuteScalar();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _db.Dispose();
        }
    }
}
