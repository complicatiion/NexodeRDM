namespace Nexode.Core.Tree;

/// <summary>Case-insensitive comparison with numeric digit runs: "srv2" before "srv10".</summary>
public sealed class NaturalComparer : IComparer<string?>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var si = i;
                var sj = j;
                while (si < x.Length && x[si] == '0') si++;
                while (sj < y.Length && y[sj] == '0') sj++;
                var ei = si;
                var ej = sj;
                while (ei < x.Length && char.IsDigit(x[ei])) ei++;
                while (ej < y.Length && char.IsDigit(y[ej])) ej++;

                var lenDiff = (ei - si) - (ej - sj);
                if (lenDiff != 0) return lenDiff;

                var c = string.CompareOrdinal(x, si, y, sj, ei - si);
                if (c != 0) return c;

                i = ei;
                j = ej;
                continue;
            }

            var cx = char.ToLowerInvariant(x[i]);
            var cy = char.ToLowerInvariant(y[j]);
            if (cx != cy) return cx.CompareTo(cy);
            i++;
            j++;
        }

        var rest = (x.Length - i) - (y.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(x, y);
    }
}
