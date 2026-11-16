using Avalonia.Media;
using Nexode.App.Localization;
using Nexode.Core.Model;

namespace Nexode.App.Services;

/// <summary>Display helpers for environments (names of built-in ones are translated while unchanged).</summary>
public static class Envs
{
    public static EnvironmentDef? Get(string? id) => id is { Length: > 0 } && AppHost.Workspace.Environments.TryGetValue(id, out var e) ? e : null;

    public static string Name(EnvironmentDef e) =>
        e.BuiltIn && EnvironmentDef.Defaults.FirstOrDefault(d => d.Id == e.Id)?.Name == e.Name && Loc.T("Env." + e.Name) is var t && t != "Env." + e.Name ? t : e.Name;

    public static string? Name(string? id) => Get(id) is { } e ? Name(e) : null;

    public static Color ColorOf(EnvironmentDef e) => Color.TryParse(e.Color, out var c) ? c : Colors.Gray;

    public static IBrush? Brush(string? id) => Get(id) is { } e ? new SolidColorBrush(ColorOf(e)) : null;

    /// <summary>Badge background: the color at low opacity.</summary>
    public static IBrush? Background(string? id) => Get(id) is { } e ? new SolidColorBrush(ColorOf(e), 0.18) : null;

    public static string Short(EnvironmentDef e) => string.IsNullOrWhiteSpace(e.Short) ? e.Name.ToUpperInvariant()[..Math.Min(5, e.Name.Length)] : e.Short;
}
