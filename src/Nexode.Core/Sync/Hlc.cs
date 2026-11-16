using System.Globalization;

namespace Nexode.Core.Sync;

/// <summary>
/// Hybrid logical clock stamp: wall time (ms), counter, device. Totally ordered and robust against small clock skew
/// between devices – the basis of last-writer-wins merging for the later LAN sync.
/// </summary>
public readonly record struct Hlc(long WallMs, int Counter, string Device) : IComparable<Hlc>
{
    public static readonly Hlc Zero = new(0, 0, "");

    public int CompareTo(Hlc other)
    {
        var c = WallMs.CompareTo(other.WallMs);
        if (c != 0) return c;
        c = Counter.CompareTo(other.Counter);
        return c != 0 ? c : string.CompareOrdinal(Device, other.Device);
    }

    public static bool operator <(Hlc a, Hlc b) => a.CompareTo(b) < 0;
    public static bool operator >(Hlc a, Hlc b) => a.CompareTo(b) > 0;

    /// <summary>Sortable text form: 0001730000000000-00000-device.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{WallMs:D16}-{Counter:D5}-{Device}");

    public static Hlc Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Zero;
        var parts = text.Split('-', 3);
        if (parts.Length < 2 ||
            !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var wall) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var counter))
        {
            return Zero;
        }

        return new Hlc(wall, counter, parts.Length > 2 ? parts[2] : "");
    }
}

/// <summary>Issues monotonic HLC stamps for one device.</summary>
public sealed class HlcClock(string device, Func<long>? now = null)
{
    private readonly Func<long> _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private readonly Lock _gate = new();
    private Hlc _last = Hlc.Zero;

    public string Device { get; } = device;

    public Hlc Next()
    {
        lock (_gate)
        {
            var wall = _now();
            _last = wall > _last.WallMs ? new Hlc(wall, 0, Device) : new Hlc(_last.WallMs, _last.Counter + 1, Device);
            return _last;
        }
    }

    /// <summary>Merges a received stamp so later local stamps sort after it.</summary>
    public void Observe(Hlc remote)
    {
        lock (_gate)
        {
            if (remote > _last) _last = remote with { Device = Device };
        }
    }
}
