using System.Text.Json;
using System.Text.Json.Serialization;
using Nexode.Core.Model;

namespace Nexode.App.Services;

/// <summary>Personal part of an entry in a shared data source (kept on this computer only).</summary>
public sealed class PersonalEntry
{
    public bool Fav { get; set; }
    public string FavKey { get; set; } = "";
    public bool Pin { get; set; }
    public string PinKey { get; set; } = "";
    public double? PinHue { get; set; }
    public bool Open { get; set; }
    public DateTime? Used { get; set; }
    public List<int> Days { get; set; } = [];
}

/// <summary>
/// Favorites, pins, expanded folders and usage of a shared data source. Applied over the shared entries on every
/// load, so one person's favorites never show up for the team.
/// </summary>
public sealed class PersonalState
{
    private readonly string _path;
    private Dictionary<Guid, PersonalEntry> _items = [];

    private PersonalState(string path) => _path = path;

    public static PersonalState Load(string path)
    {
        var state = new PersonalState(path);
        try
        {
            if (File.Exists(path)) state._items = JsonSerializer.Deserialize(File.ReadAllText(path), PersonalJson.Default.DictionaryGuidPersonalEntry) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }

        return state;
    }

    public void ApplyTo(IEnumerable<Entry> entries)
    {
        foreach (var e in entries)
        {
            var p = _items.GetValueOrDefault(e.Id);
            e.IsFavorite = p?.Fav ?? false;
            e.FavoriteKey = p?.FavKey ?? "";
            e.IsPinned = p?.Pin ?? false;
            e.PinKey = p?.PinKey ?? "";
            e.PinHue = p?.PinHue;
            e.IsExpanded = p?.Open ?? false;
            e.LastUsedUtc = p?.Used;
            e.UseDays = p is null ? [] : [.. p.Days];
        }
    }

    public void Capture(IEnumerable<Entry> entries)
    {
        foreach (var e in entries)
        {
            if (!e.IsFavorite && !e.IsPinned && !e.IsExpanded && e.LastUsedUtc is null)
            {
                _items.Remove(e.Id);
                continue;
            }

            _items[e.Id] = new PersonalEntry
            {
                Fav = e.IsFavorite, FavKey = e.FavoriteKey, Pin = e.IsPinned, PinKey = e.PinKey, PinHue = e.PinHue, Open = e.IsExpanded, Used = e.LastUsedUtc,
                Days = [.. e.UseDays]
            };
        }
    }

    public void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, PersonalJson.Default.DictionaryGuidPersonalEntry));
            File.Move(tmp, _path, true);
        }
        catch (IOException)
        {
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(Dictionary<Guid, PersonalEntry>))]
internal sealed partial class PersonalJson : JsonSerializerContext;
