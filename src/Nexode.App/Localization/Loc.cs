using System.Globalization;
using Avalonia;
using Avalonia.Controls;

namespace Nexode.App.Localization;

/// <summary>Runtime language switch (English default; German, French, Spanish). Texts are app resources ("S.Key") bound via DynamicResource.</summary>
public static class Loc
{
    private static IReadOnlyDictionary<string, string> _current = Strings.En;
    private static ResourceDictionary? _applied;

    public const string DefaultLanguage = "en";

    public static string Language { get; private set; } = DefaultLanguage;

    public static event Action? LanguageChanged;

    public static IReadOnlyList<(string Code, string Name)> Languages { get; } = [("en", "English"), ("de", "Deutsch"), ("fr", "Français"), ("es", "Español")];

    private static IReadOnlyDictionary<string, string> Table(string code) => code switch
    {
        "de" => Strings.De,
        "fr" => Strings.Fr,
        "es" => Strings.Es,
        _ => Strings.En
    };

    public static void Apply(string? language)
    {
        var code = language?.ToLowerInvariant() ?? DefaultLanguage;
        if (code.Length > 2) code = code[..2];
        Language = Languages.Any(l => l.Code == code) ? code : DefaultLanguage;
        _current = Table(Language);

        if (Application.Current?.Resources is { } resources)
        {
            var dictionary = new ResourceDictionary();
            foreach (var pair in Strings.En)
            {
                dictionary["S." + pair.Key] = _current.TryGetValue(pair.Key, out var value) ? value : pair.Value;
            }

            if (_applied is not null) resources.MergedDictionaries.Remove(_applied);
            resources.MergedDictionaries.Add(dictionary);
            _applied = dictionary;
        }

        LanguageChanged?.Invoke();
    }

    public static string T(string key) =>
        _current.TryGetValue(key, out var value) ? value
        : Strings.En.TryGetValue(key, out var fallback) ? fallback
        : key;

    public static string F(string key, params object?[] args)
    {
        try
        {
            return string.Format(CultureInfo.CurrentCulture, T(key), args);
        }
        catch (FormatException)
        {
            return T(key);
        }
    }

    public static string Kind(Nexode.Core.Model.EntryKind kind) => T("Kind." + kind);
}
