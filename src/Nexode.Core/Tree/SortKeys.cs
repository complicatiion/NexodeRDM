namespace Nexode.Core.Tree;

/// <summary>
/// Fractional sort keys (base 62, ordinal order): a key between any two neighbours can always be created,
/// so moving one entry updates exactly one row.
/// </summary>
public static class SortKeys
{
    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>A key strictly between <paramref name="before"/> and <paramref name="after"/> (null = open end).</summary>
    public static string Between(string? before, string? after)
    {
        var a = before ?? "";
        if (after is not null && string.CompareOrdinal(a, after) >= 0)
        {
            // Inconsistent neighbours (duplicate keys): fall back to "after a".
            after = null;
        }

        return Midpoint(a, string.IsNullOrEmpty(after) ? null : after);
    }

    /// <summary>Evenly spread keys for <paramref name="count"/> items (used to renumber a list once).</summary>
    public static IReadOnlyList<string> Spread(int count)
    {
        var keys = new List<string>(count);
        string? last = null;
        for (var i = 0; i < count; i++)
        {
            last = Between(last, null);
            keys.Add(last);
        }

        return keys;
    }

    private static string Midpoint(string a, string? b)
    {
        if (b is not null)
        {
            var n = 0;
            while (n < b.Length && (n < a.Length ? a[n] : '0') == b[n])
            {
                n++;
            }

            if (n > 0)
            {
                return b[..n] + Midpoint(a.Length > n ? a[n..] : "", b[n..]);
            }
        }

        var digitA = a.Length > 0 ? Digits.IndexOf(a[0]) : 0;
        var digitB = b is not null ? Digits.IndexOf(b[0]) : Digits.Length;
        if (digitB - digitA > 1)
        {
            return Digits[(digitA + digitB) / 2].ToString();
        }

        if (b is not null && b.Length > 1)
        {
            return b[..1];
        }

        return Digits[digitA] + Midpoint(a.Length > 1 ? a[1..] : "", null);
    }
}
