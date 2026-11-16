using Nexode.Core.Model;
using Nexode.Core.Sync;

namespace Nexode.Core.Files;

public enum ImportStatus
{
    New,
    Changed,
    Identical
}

public enum ConflictPolicy
{
    Skip = 0,
    Overwrite = 1,
    NewerWins = 2,
    KeepBoth = 3
}

public sealed record ImportItem(Entry Incoming, Entry? Existing, ImportStatus Status);

public sealed class ImportResult
{
    public List<Entry> Upserts { get; } = [];
    /// <summary>(entry id, field, plain value) to store encrypted.</summary>
    public List<(Guid EntryId, string Field, string Value)> Secrets { get; } = [];
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int NeedsReview { get; set; }
}

/// <summary>Compares an import with the existing entries and applies a conflict policy.</summary>
public static class ImportPlanner
{
    public static IReadOnlyList<ImportItem> Plan(ConnectionsPayload payload, IReadOnlyDictionary<Guid, Entry> existing)
    {
        return payload.Entries.Select(e =>
        {
            if (!existing.TryGetValue(e.Id, out var current) || current.IsDeleted) return new ImportItem(e, null, ImportStatus.New);
            var probe = e.Clone();
            probe.ParentId = current.ParentId;
            probe.SortKey = current.SortKey;
            probe.IsFavorite = current.IsFavorite;
            probe.NeedsReview = current.NeedsReview;
            return new ImportItem(e, current, probe.ContentEquals(current) ? ImportStatus.Identical : ImportStatus.Changed);
        }).ToList();
    }

    /// <param name="selected">Ids to import (null = all).</param>
    /// <param name="targetParent">Parent of the import roots (null = data source root).</param>
    /// <param name="nextSortKey">Returns a sort key at the end of a parent.</param>
    public static ImportResult Apply(ConnectionsPayload payload, IReadOnlyList<ImportItem> plan, ISet<Guid>? selected,
        Guid? targetParent, ConflictPolicy policy, Func<Hlc> stamp, Func<Guid?, string> nextSortKey)
    {
        var result = new ImportResult();
        var importIds = payload.Entries.Select(e => e.Id).ToHashSet();
        var remap = new Dictionary<Guid, Guid>();

        if (policy == ConflictPolicy.KeepBoth)
        {
            foreach (var item in plan.Where(i => i.Existing is not null && i.Status != ImportStatus.Identical))
            {
                remap[item.Incoming.Id] = Guid.CreateVersion7();
            }
        }

        Guid Map(Guid id) => remap.GetValueOrDefault(id, id);

        foreach (var item in plan)
        {
            if (selected is not null && !selected.Contains(item.Incoming.Id))
            {
                result.Skipped++;
                continue;
            }

            if (item.Status == ImportStatus.Identical)
            {
                result.Skipped++;
                continue;
            }

            var keepBoth = remap.ContainsKey(item.Incoming.Id);
            if (item.Existing is not null && !keepBoth)
            {
                var take = policy switch
                {
                    ConflictPolicy.Skip => false,
                    ConflictPolicy.Overwrite => true,
                    ConflictPolicy.NewerWins => item.Incoming.Version > item.Existing.Version,
                    _ => true
                };
                if (!take)
                {
                    result.Skipped++;
                    continue;
                }
            }

            var e = item.Incoming.Clone();
            e.Id = Map(e.Id);
            e.DeletedUtc = null;
            var isRoot = e.ParentId is null || !importIds.Contains(e.ParentId.Value);
            if (isRoot)
            {
                e.ParentId = item.Existing is not null && !keepBoth ? item.Existing.ParentId : targetParent;
            }
            else
            {
                e.ParentId = Map(e.ParentId!.Value);
            }

            if (e.CredentialId is { } cid) e.CredentialId = Map(cid);
            if (isRoot && (item.Existing is null || keepBoth)) e.SortKey = nextSortKey(e.ParentId);
            if (keepBoth) e.Name += " (2)";
            e.Version = stamp();

            // Anything that runs local programs needs a review after import.
            if (e.Kind == EntryKind.Command || e.Settings.Keys.Any(k => k.StartsWith("cmd.", StringComparison.Ordinal)))
            {
                e.NeedsReview = true;
                result.NeedsReview++;
            }

            result.Upserts.Add(e);
            if (item.Existing is null || keepBoth) result.Added++;
            else result.Updated++;

            foreach (var s in payload.Secrets.Where(s => s.EntryId == item.Incoming.Id && !string.IsNullOrEmpty(s.Value)))
            {
                result.Secrets.Add((e.Id, s.Field, s.Value!));
            }
        }

        return result;
    }
}
