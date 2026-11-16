using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Nexode.App.Localization;
using Nexode.App.Views;
using Nexode.Core.Security;
using Nexode.Platform;
using Nexode.Storage;

namespace Nexode.App.Services;

/// <summary>Creates, opens and switches data sources (one SQLite file each).</summary>
public static class DataSources
{
    public static DataSourceInfo? Active =>
        AppHost.Settings.DataSources.FirstOrDefault(d => d.Id == AppHost.Settings.ActiveDataSource) ?? AppHost.Settings.DataSources.FirstOrDefault();

    public static DataSourceInfo CreateInfo(string name)
    {
        var slug = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            slug.Append(char.IsLetterOrDigit(c) ? c : '-');
        }

        var baseName = slug.ToString().Trim('-');
        if (baseName.Length == 0) baseName = "source";
        var path = Path.Combine(AppPaths.SourcesDirectory, baseName + ".db");
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(AppPaths.SourcesDirectory, $"{baseName}-{i}.db");
        return new DataSourceInfo { Name = name, Path = path };
    }

    /// <summary>Asks for a master password when the new vault must have one (portable mode) or the user wants one.</summary>
    public static async Task<bool> OpenAsync(Window? owner, DataSourceInfo info, bool isNew)
    {
        string? password = null;
        if (isNew && (AppPaths.IsPortable || info.Shared) && owner is not null)
        {
            // Portable and team sources: the key must not depend on this PC / Windows account.
            password = await Dialog.PasswordAsync(owner, Loc.T(info.Shared ? "Shared.PasswordTitle" : "Vault.PortableTitle"),
                Loc.T(info.Shared ? "Shared.PasswordText" : "Vault.PortableText"), confirm: true, minLength: 8);
            if (password is null) return false;
        }

        AppHost.Workspace.Open(info, password);
        if (!AppHost.Settings.DataSources.Contains(info)) AppHost.Settings.DataSources.Add(info);
        AppHost.Settings.ActiveDataSource = info.Id;
        if (!info.IsSample) AppHost.Settings.LastRealSource = info.Id;
        AppHost.Settings.Save();
        return true;
    }

    public static string BackupFolder(string storeId) => Path.Combine(AppPaths.BackupDirectory, storeId);

    /// <summary>Backups of the active data source, newest first.</summary>
    public static IReadOnlyList<FileInfo> Backups()
    {
        if (AppHost.Workspace.Store is not { } store) return [];
        var dir = BackupFolder(store.Id);
        return Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles("*.db").OrderByDescending(f => f.LastWriteTimeUtc).ToList() : [];
    }

    /// <summary>Replaces the active data source with a backup (a backup of the current state is made first).</summary>
    public static async Task<bool> RestoreAsync(Window owner, FileInfo backup)
    {
        var ws = AppHost.Workspace;
        if (ws.Info is not { } info || ws.Store is null) return false;
        try { ws.BackupNow(); } catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) { }
        var temp = backup.FullName + ".restore";
        File.Copy(backup.FullName, temp, true);
        ws.Close();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(info.Path + suffix)) File.Delete(info.Path + suffix);
        }

        File.Move(temp, info.Path, true);
        await OpenAsync(owner, info, isNew: false);
        return true;
    }

    /// <summary>Removes a data source from the list and deletes its file (and WAL files).</summary>
    public static void DeleteFile(DataSourceInfo info)
    {
        AppHost.Settings.DataSources.Remove(info);
        AppHost.Settings.Save();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { info.Path, info.Path + "-wal", info.Path + "-shm" })
        {
            if (File.Exists(f)) File.Delete(f);
        }
    }

    /// <summary>Opens another data source next to the active one (move/copy entries). Asks for its master password when needed.</summary>
    public static async Task<(DataSourceStore Store, Vault Vault)?> OpenSecondaryAsync(Window owner, DataSourceInfo info)
    {
        var store = DataSourceStore.Open(info.Path, info.Name, Shell.IsNetworkPath(info.Path));
        var header = store.ReadVaultHeader();
        Vault vault;
        try
        {
            if (header is null)
            {
                vault = Vault.CreateSystem(store.Id, KeyProtectors.Create(AppPaths.KeyDirectory));
                store.WriteVaultHeader(vault.Header);
            }
            else
            {
                var protector = header.Mode == VaultMode.System ? KeyProtectors.ById(header.ProtectorId, AppPaths.KeyDirectory) : KeyProtectors.Create(AppPaths.KeyDirectory);
                vault = Vault.Open(header, store.Id, protector);
            }
        }
        catch (CryptographicException)
        {
            store.Dispose();
            return null;
        }

        if (!vault.IsUnlocked)
        {
            var pw = await Dialog.PasswordAsync(owner, Loc.T("Vault.UnlockTitle"), Loc.F("Vault.UnlockText", info.Name), false,
                validate: p => vault.TryUnlockQuiet(p), icon: "lock-open");
            if (pw is null)
            {
                vault.Dispose();
                store.Dispose();
                return null;
            }
        }

        if (store.IsFullyEncrypted) store.Cipher = new SecondaryCipher(vault);
        return (store, vault);
    }

    private sealed class SecondaryCipher(Vault vault) : IEntryCipher
    {
        public string Protect(Guid id, string json) => vault.EncryptText(id, "entry", json);
        public string? Unprotect(Guid id, string data) => vault.DecryptText(id, "entry", data);
    }
}
