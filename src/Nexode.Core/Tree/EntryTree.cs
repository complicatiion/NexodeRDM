using Nexode.Core.Model;

namespace Nexode.Core.Tree;

/// <summary>Read-only index over the live (not deleted) entries of a data source.</summary>
public sealed class EntryTree
{
    private readonly Dictionary<Guid, Entry> _byId;
    private readonly Dictionary<Guid, List<Entry>> _children = [];
    private readonly List<Entry> _roots = [];

    public EntryTree(IEnumerable<Entry> entries, SortMode rootSort = SortMode.Manual, bool foldersFirst = true)
    {
        RootSort = rootSort;
        FoldersFirst = foldersFirst;
        _byId = entries.Where(e => !e.IsDeleted).ToDictionary(e => e.Id);

        foreach (var entry in _byId.Values)
        {
            // Orphans (parent deleted or missing) show at the root.
            if (entry.ParentId is { } parent && _byId.ContainsKey(parent) && parent != entry.Id)
            {
                if (!_children.TryGetValue(parent, out var list))
                {
                    _children[parent] = list = [];
                }
                list.Add(entry);
            }
            else
            {
                _roots.Add(entry);
            }
        }

        Sort(_roots, RootSort);
        foreach (var (parentId, list) in _children)
        {
            Sort(list, EffectiveSort(_byId[parentId]));
        }
    }

    public SortMode RootSort { get; }
    public bool FoldersFirst { get; }
    public int Count => _byId.Count;
    public IEnumerable<Entry> All => _byId.Values;
    public IReadOnlyList<Entry> Roots => _roots;

    public Entry? Get(Guid? id) => id is { } v && _byId.TryGetValue(v, out var e) ? e : null;

    public IReadOnlyList<Entry> Children(Guid? parentId) =>
        parentId is null ? _roots : _children.TryGetValue(parentId.Value, out var list) ? list : [];

    public bool HasChildren(Guid id) => _children.ContainsKey(id);

    public Entry? Parent(Entry entry) => Get(entry.ParentId);

    /// <summary>Parent chain, nearest first.</summary>
    public IEnumerable<Entry> Ancestors(Entry entry)
    {
        var seen = new HashSet<Guid> { entry.Id };
        var current = Parent(entry);
        while (current is not null && seen.Add(current.Id))
        {
            yield return current;
            current = Parent(current);
        }
    }

    public IEnumerable<Entry> Descendants(Guid id)
    {
        var stack = new Stack<Entry>(Children(id).Reverse());
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            yield return e;
            foreach (var c in Children(e.Id).Reverse())
            {
                stack.Push(c);
            }
        }
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="id"/> itself or below it.</summary>
    public bool IsSelfOrDescendant(Guid id, Guid? candidate)
    {
        var current = Get(candidate);
        var guard = 0;
        while (current is not null && guard++ < 10_000)
        {
            if (current.Id == id) return true;
            current = Parent(current);
        }

        return false;
    }

    public string Path(Entry entry, string separator = " / ") =>
        string.Join(separator, Ancestors(entry).Reverse().Select(a => a.Name));

    public int CountItems(Guid id) => Descendants(id).Count(e => !e.IsContainer || e.Kind == EntryKind.Host);

    public SortMode EffectiveSort(Entry container)
    {
        if (container.SortMode is { } own) return own;
        foreach (var a in Ancestors(container))
        {
            if (a.SortMode is { } inherited) return inherited;
        }

        return RootSort;
    }

    private void Sort(List<Entry> list, SortMode mode)
    {
        Comparison<Entry> byMode = mode switch
        {
            SortMode.Name => (a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name),
            SortMode.Kind => (a, b) =>
            {
                var c = ((int)a.Kind).CompareTo((int)b.Kind);
                return c != 0 ? c : NaturalComparer.Instance.Compare(a.Name, b.Name);
            },
            SortMode.LastUsed => (a, b) =>
            {
                var c = (b.LastUsedUtc ?? DateTime.MinValue).CompareTo(a.LastUsedUtc ?? DateTime.MinValue);
                return c != 0 ? c : NaturalComparer.Instance.Compare(a.Name, b.Name);
            },
            _ => (a, b) =>
            {
                var c = string.CompareOrdinal(a.SortKey, b.SortKey);
                return c != 0 ? c : a.Id.CompareTo(b.Id);
            }
        };

        list.Sort((a, b) =>
        {
            if (FoldersFirst)
            {
                var fa = a.Kind == EntryKind.Folder ? 0 : 1;
                var fb = b.Kind == EntryKind.Folder ? 0 : 1;
                if (fa != fb) return fa - fb;
            }

            return byMode(a, b);
        });
    }

    /// <summary>Sort key for inserting after <paramref name="after"/> (null = at the start) under <paramref name="parentId"/>.</summary>
    public string KeyAfter(Guid? parentId, Entry? after, IReadOnlySet<Guid>? exclude = null)
    {
        var siblings = Children(parentId).Where(e => exclude is null || !exclude.Contains(e.Id))
            .OrderBy(e => e.SortKey, StringComparer.Ordinal).ToList();
        if (after is null)
        {
            return SortKeys.Between(null, siblings.FirstOrDefault()?.SortKey);
        }

        var index = siblings.FindIndex(e => e.Id == after.Id);
        var next = index >= 0 && index + 1 < siblings.Count ? siblings[index + 1].SortKey : null;
        return SortKeys.Between(after.SortKey, next);
    }

    public string KeyAtEnd(Guid? parentId, IReadOnlySet<Guid>? exclude = null)
    {
        var last = Children(parentId).Where(e => exclude is null || !exclude.Contains(e.Id))
            .Select(e => e.SortKey).Where(k => k.Length > 0).DefaultIfEmpty(null).Max(StringComparer.Ordinal);
        return SortKeys.Between(last, null);
    }
}
