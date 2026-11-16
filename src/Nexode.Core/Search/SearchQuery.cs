using Nexode.Core.Model;
using Nexode.Core.Tree;

namespace Nexode.Core.Search;

/// <summary>
/// Tree filter: free text (fuzzy over name, host, URL, tags, path) plus prefix filters
/// p:rdp · os:linux · env:prod · cat:server · in:folder · #tag · is:fav.
/// </summary>
public sealed class SearchQuery
{
    private SearchQuery() { }

    public IReadOnlyList<string> Terms { get; private init; } = [];
    public IReadOnlyList<EntryKind> Kinds { get; private init; } = [];
    public OsFamily? Os { get; private init; }
    /// <summary>Environment filter: prefix of the id or name ("prod", "dev").</summary>
    public string? Environment { get; private init; }
    public string? Category { get; private init; }
    public string? InFolder { get; private init; }
    public IReadOnlyList<string> Tags { get; private init; } = [];
    public bool FavoritesOnly { get; private init; }

    public bool IsEmpty => Terms.Count == 0 && Kinds.Count == 0 && Os is null && Environment is null && Category is null
        && InFolder is null && Tags.Count == 0 && !FavoritesOnly;

    public static SearchQuery Parse(string? text)
    {
        var terms = new List<string>();
        var kinds = new List<EntryKind>();
        var tags = new List<string>();
        OsFamily? os = null;
        string? env = null;
        string? category = null, folder = null;
        var fav = false;

        foreach (var raw in (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw.ToLowerInvariant();
            if (token.StartsWith('#') && token.Length > 1)
            {
                tags.Add(token[1..]);
                continue;
            }

            var colon = token.IndexOf(':');
            if (colon > 0 && colon < token.Length - 1 && !token.Contains("//", StringComparison.Ordinal))
            {
                var key = token[..colon];
                var value = token[(colon + 1)..];
                switch (key)
                {
                    case "p" or "proto" or "type":
                        if (ParseKind(value) is { } k) { kinds.Add(k); continue; }
                        break;
                    case "os":
                        os = value switch
                        {
                            "win" or "windows" => OsFamily.Windows,
                            "linux" or "lin" => OsFamily.Linux,
                            "mac" or "macos" or "osx" => OsFamily.MacOS,
                            "unix" or "bsd" => OsFamily.Unix,
                            "net" or "network" => OsFamily.Network,
                            _ => os
                        };
                        continue;
                    case "env":
                        env = value switch
                        {
                            "p" => "production",
                            "stage" => "staging",
                            "t" => "test",
                            "d" => "development",
                            _ => value
                        };
                        continue;
                    case "cat" or "category":
                        category = value;
                        continue;
                    case "in":
                        folder = value;
                        continue;
                    case "is" when value is "fav" or "favorite" or "favourite":
                        fav = true;
                        continue;
                }
            }

            terms.Add(token);
        }

        return new SearchQuery
        {
            Terms = terms, Kinds = kinds, Tags = tags, Os = os, Environment = env, Category = category, InFolder = folder,
            FavoritesOnly = fav
        };
    }

    public static EntryKind? ParseKind(string value) => value switch
    {
        "rdp" => EntryKind.Rdp,
        "ssh" => EntryKind.Ssh,
        "sftp" => EntryKind.Sftp,
        "ftp" or "ftps" => EntryKind.Ftp,
        "vnc" => EntryKind.Vnc,
        "telnet" => EntryKind.Telnet,
        "ps" or "powershell" or "winrm" => EntryKind.PowerShell,
        "serial" or "com" => EntryKind.Serial,
        "web" or "http" or "https" => EntryKind.Web,
        "cmd" or "command" => EntryKind.Command,
        "db" or "database" or "sql" => EntryKind.Database,
        "host" => EntryKind.Host,
        "folder" => EntryKind.Folder,
        "cred" or "credential" => EntryKind.Credential,
        "note" => EntryKind.Note,
        _ => null
    };

    /// <summary>Score &gt; 0 when the entry matches (higher = better), 0 otherwise.</summary>
    public int Score(Entry entry, Resolver resolver, Effective? effective = null)
    {
        if (FavoritesOnly && !entry.IsFavorite) return 0;
        if (Kinds.Count > 0 && !Kinds.Contains(entry.Kind)) return 0;
        if (Category is not null && !string.Equals(entry.CategoryId, Category, StringComparison.OrdinalIgnoreCase)) return 0;
        if (Tags.Count > 0 && !Tags.All(t => entry.Tags.Any(et => et.Contains(t, StringComparison.OrdinalIgnoreCase)))) return 0;

        if (Os is not null || Environment is not null)
        {
            effective ??= resolver.Resolve(entry);
            if (Os is { } os && effective.Os != os) return 0;
            if (Environment is { } env && !resolver.EnvironmentMatches(effective.Environment, env)) return 0;
        }

        var tree = resolver.Tree;
        if (InFolder is not null && !tree.Ancestors(entry).Any(a => a.Name.Contains(InFolder, StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        if (Terms.Count == 0) return 1;

        var total = 0;
        foreach (var term in Terms)
        {
            var best = Fuzzy.Score(term, entry.Name) * 3;
            best = Math.Max(best, Fuzzy.Score(term, entry.Host) * 2);
            best = Math.Max(best, Fuzzy.Score(term, entry.Url) * 2);
            best = Math.Max(best, Fuzzy.ContainsScore(term, entry.Description));
            best = Math.Max(best, entry.Tags.Max(t => (int?)Fuzzy.ContainsScore(term, t) * 2) ?? 0);
            best = Math.Max(best, Fuzzy.ContainsScore(term, entry.Username));
            if (best == 0)
            {
                // Path match: "bsag dc01" finds dc01 inside the BSAG folder.
                best = tree.Ancestors(entry).Max(a => (int?)Fuzzy.ContainsScore(term, a.Name)) ?? 0;
            }

            if (best == 0) return 0;
            total += best;
        }

        return total;
    }
}

/// <summary>Small fuzzy matcher: prefix &gt; word start &gt; substring &gt; subsequence.</summary>
public static class Fuzzy
{
    public static int Score(string term, string? text)
    {
        if (string.IsNullOrEmpty(text) || term.Length == 0) return 0;
        var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (index == 0) return 100 - Math.Min(40, text.Length - term.Length);
        if (index > 0) return IsWordStart(text, index) ? 70 : 50;
        return IsSubsequence(term, text) ? 15 : 0;
    }

    public static int ContainsScore(string term, string? text) =>
        !string.IsNullOrEmpty(text) && text.Contains(term, StringComparison.OrdinalIgnoreCase) ? 30 : 0;

    private static bool IsWordStart(string text, int index) => !char.IsLetterOrDigit(text[index - 1]);

    private static bool IsSubsequence(string term, string text)
    {
        if (term.Length < 2) return false;
        var j = 0;
        foreach (var c in text)
        {
            if (char.ToLowerInvariant(c) == term[j] && ++j == term.Length) return true;
        }

        return false;
    }
}
