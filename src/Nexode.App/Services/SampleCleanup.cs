using System.Net;
using Nexode.Core.Model;

namespace Nexode.App.Services;

/// <summary>
/// Finds entries of the sample data in a normal data source (older versions could add them there): the known
/// sample folder names and entries whose address uses reserved example names or documentation IP ranges only.
/// </summary>
public static class SampleCleanup
{
    /// <summary>Top-level folders created by the sample data.</summary>
    public static readonly string[] SampleFolders = ["Example Corp", "This computer", "Web hosting", "Credentials", "Branch offices", "Cloud & storage", "Customers"];

    public static bool IsExampleHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host is "example.com" or "example.org" or "example.net" || host.EndsWith(".example.com", StringComparison.Ordinal) || host.EndsWith(".example.org", StringComparison.Ordinal)
            || host.EndsWith(".example.net", StringComparison.Ordinal) || host.EndsWith(".example.local", StringComparison.Ordinal) || host == "example.local") return true;
        if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return (b[0], b[1], b[2]) is (192, 0, 2) or (198, 51, 100) or (203, 0, 113);
    }

    private static string? Address(Entry e) =>
        EntryKinds.HasUrl(e.Kind) ? Uri.TryCreate(e.Url, UriKind.Absolute, out var u) ? u.Host : null : e.Host;

    /// <summary>Candidates in the active data source: sample folders (with everything below) and single example entries.</summary>
    public static IReadOnlyList<Entry> Find()
    {
        var tree = AppHost.Workspace.Tree;
        var result = new Dictionary<Guid, Entry>();
        foreach (var root in tree.Children(null))
        {
            if (!root.IsContainer || !SampleFolders.Contains(root.Name)) continue;
            var below = tree.Descendants(root.Id).ToList();
            // Only when everything in it still looks like sample data (nothing of the user's own was added).
            var addresses = below.Select(Address).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
            if (addresses.Any(a => !IsExampleHost(a) && a is not ("localhost" or "127.0.0.1"))) continue;
            result[root.Id] = root;
            foreach (var d in below) result[d.Id] = d;
        }

        foreach (var e in tree.All)
        {
            if (result.ContainsKey(e.Id) || e.IsContainer) continue;
            if (IsExampleHost(Address(e))) result[e.Id] = e;
        }

        // Sample connection profiles reference these entries; the profiles are removed separately.
        return [.. result.Values.OrderBy(e => tree.Path(e), StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Moves the entries to the trash (can be restored) and removes the sample profiles.</summary>
    public static int Remove(IReadOnlyList<Entry> entries)
    {
        var ws = AppHost.Workspace;
        var ids = entries.Select(e => e.Id).ToHashSet();
        // Delete top-most only (children follow their folder).
        var roots = entries.Where(e => e.ParentId is not { } p || !ids.Contains(p)).Select(e => e.Id).ToList();
        ws.Delete(roots);
        var profiles = ws.Profiles.Where(p => !p.Id.StartsWith("p-sample-", StringComparison.Ordinal) || p.Entries.Any(id => !ids.Contains(id))).ToList();
        if (profiles.Count != ws.Profiles.Count) ws.SaveProfiles(profiles);
        return entries.Count;
    }
}
