using System.Globalization;
using System.Text.Json;

namespace Nexode.Core.Updates;

/// <summary>Latest release as reported by the GitHub releases API.</summary>
public sealed record ReleaseInfo(string Tag, string Version, string Url, string? Name, DateTime? PublishedUtc)
{
    /// <summary>Parses the answer of GET /repos/{owner}/{repo}/releases/latest; null when it is not a release.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { Length: > 0 } t) return null;
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            // Only links to GitHub are opened.
            if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) url = "";
            var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            DateTime? published = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                                  && DateTime.TryParse(p.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
            return new ReleaseInfo(t, Normalize(t), url, name, published);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"v0.0.4" / "nexoderdm_v0.0.4" / "0.0.4-beta" → "0.0.4".</summary>
    public static string Normalize(string tag)
    {
        var start = 0;
        while (start < tag.Length && !char.IsDigit(tag[start])) start++;
        var end = start;
        while (end < tag.Length && (char.IsDigit(tag[end]) || tag[end] == '.')) end++;
        return tag[start..end].Trim('.');
    }

    /// <summary>True when <paramref name="candidate"/> is a higher version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string candidate, string current) =>
        System.Version.TryParse(Pad(Normalize(candidate)), out var a) && System.Version.TryParse(Pad(Normalize(current)), out var b) && a > b;

    private static string Pad(string v)
    {
        var parts = v.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count < 3) parts.Add("0");
        return string.Join('.', parts.Take(4));
    }
}
