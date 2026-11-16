using System.Globalization;

namespace Nexode.Core.Rdp;

/// <summary>A user session on a Remote Desktop server (for shadowing).</summary>
public sealed record RdpUserSession(int Id, string User, string? SessionName, string State, bool IsCurrent);

/// <summary>Parses the output of <c>qwinsta /server:host</c> (any language: the columns are found by position).</summary>
public static class RdpSessions
{
    public static IReadOnlyList<RdpUserSession> ParseQwinsta(string output)
    {
        var lines = output.Replace("\r", "", StringComparison.Ordinal).Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 2) return [];

        // Header: SESSIONNAME USERNAME ID STATE … (localized) – the second word marks where the user name column starts.
        var header = lines[0];
        var userColumn = WordStarts(header).ElementAtOrDefault(1);
        if (userColumn <= 0) return [];

        var result = new List<RdpUserSession>();
        foreach (var line in lines.Skip(1))
        {
            var current = line.StartsWith('>');
            var words = WordStarts(line, skipFirstChar: true).Select(start => (Start: start, Text: WordAt(line, start))).ToList();
            var idIndex = words.FindIndex(w => w.Text.All(char.IsAsciiDigit));
            if (idIndex < 1) continue;
            if (!int.TryParse(words[idIndex].Text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id >= 65536) continue;
            var before = words.Take(idIndex).ToList();
            string? sessionName = null;
            string? user = null;
            foreach (var w in before)
            {
                // Tolerance of two columns: qwinsta aligns words, but long session names can push them.
                if (w.Start >= userColumn - 2 && user is null) user = w.Text;
                else sessionName ??= w.Text;
            }

            if (string.IsNullOrEmpty(user)) continue;
            var state = words.Count > idIndex + 1 ? words[idIndex + 1].Text : "";
            result.Add(new RdpUserSession(id, user, sessionName, state, current));
        }

        return result;
    }

    private static IEnumerable<int> WordStarts(string line, bool skipFirstChar = false)
    {
        for (var i = skipFirstChar ? 1 : 0; i < line.Length; i++)
        {
            if (!char.IsWhiteSpace(line[i]) && (i == 0 || char.IsWhiteSpace(line[i - 1]) || (skipFirstChar && i == 1))) yield return i;
        }
    }

    private static string WordAt(string line, int start)
    {
        var end = start;
        while (end < line.Length && !char.IsWhiteSpace(line[end])) end++;
        return line[start..end];
    }
}
