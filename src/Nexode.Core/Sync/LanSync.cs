using Nexode.Core.Model;

namespace Nexode.Core.Sync;

/// <summary>
/// Everything one computer sends to the other in a LAN sync (inside the encrypted channel): the entries of the open
/// personal data source including deleted ones (tombstones), the plain passwords (both vaults are unlocked, each side
/// encrypts with its own master password again), and the lists.
/// </summary>
public sealed class LanSnapshot
{
    public int Protocol { get; set; } = LanSyncMerge.ProtocolVersion;
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string AppVersion { get; set; } = "";

    /// <summary>Set instead of data when this side cannot sync ("locked", "shared", "busy", "readonly").</summary>
    public string? Refused { get; set; }

    public List<Entry> Entries { get; set; } = [];
    public Dictionary<Guid, string> Passwords { get; set; } = [];
    public List<Category> Categories { get; set; } = [];
    public List<EnvironmentDef> Environments { get; set; } = [];
    public List<ConnectionProfile> Profiles { get; set; } = [];
    public List<EntryTemplate> Templates { get; set; } = [];
}

/// <summary>Changes to apply locally after comparing with the other computer's snapshot.</summary>
public sealed class LanMergeResult
{
    /// <summary>Entries to write as they are (remote version is newer, or new to this computer).</summary>
    public List<Entry> Take { get; } = [];

    /// <summary>Passwords to set (value) or remove (null) for taken entries.</summary>
    public List<(Guid Id, string? Password)> Passwords { get; } = [];

    public List<Category> NewCategories { get; } = [];
    public List<EnvironmentDef> NewEnvironments { get; } = [];
    public List<ConnectionProfile> NewProfiles { get; } = [];
    public List<EntryTemplate> NewTemplates { get; } = [];

    /// <summary>Remote entries skipped because they were removed for good on this computer since the last sync.</summary>
    public int SkippedPurged { get; set; }

    /// <summary>Ids both sides know after the sync (input for the next sync's purge detection).</summary>
    public HashSet<Guid> KnownIds { get; } = [];

    public int Added { get; set; }
    public int Updated { get; set; }
    public int Deleted { get; set; }

    public bool IsEmpty => Take.Count == 0 && Passwords.Count == 0 && NewCategories.Count == 0 && NewEnvironments.Count == 0 && NewProfiles.Count == 0 && NewTemplates.Count == 0;
}

/// <summary>
/// Merge of two personal data sources. Per entry the newer version (hybrid logical clock) wins, deletions travel as
/// tombstones. An entry the other side has but this side does not is new – unless both sides knew it at the last sync
/// (then it was removed for good here and is not brought back). Lists (categories, environments, connection profiles,
/// templates) are joined by id; on the same id this side's version stays.
/// </summary>
public static class LanSyncMerge
{
    public const int ProtocolVersion = 1;

    public static LanMergeResult Merge(IReadOnlyCollection<Entry> local, LanSnapshot remote, IReadOnlySet<Guid>? knownAtLastSync,
        IReadOnlyCollection<Category> categories, IReadOnlyCollection<EnvironmentDef> environments,
        IReadOnlyCollection<ConnectionProfile> profiles, IReadOnlyCollection<EntryTemplate> templates)
    {
        var result = new LanMergeResult();
        var byId = local.ToDictionary(e => e.Id);
        foreach (var r in remote.Entries)
        {
            if (r.Id == Guid.Empty) continue;
            result.KnownIds.Add(r.Id);
            if (byId.TryGetValue(r.Id, out var l))
            {
                if (r.Version.CompareTo(l.Version) <= 0) continue;
                if (r.IsDeleted && !l.IsDeleted) result.Deleted++;
                else result.Updated++;
            }
            else
            {
                if (knownAtLastSync?.Contains(r.Id) == true)
                {
                    result.SkippedPurged++;
                    continue;
                }

                if (r.IsDeleted)
                {
                    // Deleted over there before this computer ever saw it: keep the tombstone anyway, so a third
                    // copy of the entry cannot come back through this side.
                }
                else
                {
                    result.Added++;
                }
            }

            var copy = r.Clone();
            result.Take.Add(copy);
            result.Passwords.Add((r.Id, remote.Passwords.TryGetValue(r.Id, out var pw) && !string.IsNullOrEmpty(pw) ? pw : null));
        }

        foreach (var e in local) result.KnownIds.Add(e.Id);

        // Parents that exist on neither side any more: the entry moves to the root instead of vanishing.
        var present = new HashSet<Guid>(byId.Keys);
        foreach (var t in result.Take) present.Add(t.Id);
        foreach (var t in result.Take)
        {
            if (t.ParentId is { } p && !present.Contains(p)) t.ParentId = null;
        }

        var catIds = categories.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        result.NewCategories.AddRange(remote.Categories.Where(c => !string.IsNullOrEmpty(c.Id) && catIds.Add(c.Id)));
        var envIds = environments.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        result.NewEnvironments.AddRange(remote.Environments.Where(c => !string.IsNullOrEmpty(c.Id) && envIds.Add(c.Id)));
        var profileIds = profiles.Select(c => c.Id).ToHashSet();
        result.NewProfiles.AddRange(remote.Profiles.Where(c => profileIds.Add(c.Id)));
        var templateIds = templates.Select(c => c.Id).ToHashSet();
        result.NewTemplates.AddRange(remote.Templates.Where(c => templateIds.Add(c.Id)));
        return result;
    }
}
