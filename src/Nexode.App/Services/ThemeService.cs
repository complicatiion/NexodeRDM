using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Nexode.Core.Theming;

namespace Nexode.App.Services;

/// <summary>
/// Applies the palette (dark/light, accent) as app resources "C.Name" (Color) and "B.Name" (brush); switches the Fluent
/// variant and accent so check marks, radio buttons and selections use the accent instead of system blue.
/// </summary>
public sealed class ThemeService
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(60) };

    public ThemeService(AppSettings settings)
    {
        _settings = settings;
        _timer.Tick += (_, _) => Apply(onlyIfVariantChanged: true);
        if (Application.Current?.PlatformSettings is { } platform)
        {
            platform.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(() => Apply(onlyIfVariantChanged: true));
        }
    }

    public bool IsDark { get; private set; } = true;
    public bool IsAmoled { get; private set; }
    public PaletteVariant Variant { get; private set; }
    /// <summary>Accent in use: the system accent when "use system accent" is on and the system reports one.</summary>
    public string EffectiveAccent { get; private set; } = Palette.DefaultAccent;
    public Palette Palette { get; private set; } = Palette.Build(true, Palette.DefaultAccent);
    public double AccentHue => Oklch.FromRgb(Rgb.TryParse(EffectiveAccent, out var a) ? a : Rgb.FromHex(Palette.DefaultAccent)).H;

    /// <summary>Accent color of the operating system, or null when it does not report one.</summary>
    public static string? SystemAccent()
    {
        try
        {
            if (Application.Current?.PlatformSettings?.GetColorValues() is not { } values) return null;
            var c = values.AccentColor1;
            // Platforms without an accent report transparent/black (or Avalonia's default blue on Linux without a portal).
            if (c.A < 255 || c is { R: 0, G: 0, B: 0 }) return null;
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && c == Color.FromRgb(0, 120, 215)) return null;
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
        catch (Exception)
        {
            return null;
        }
    }

    private PaletteVariant ResolveVariant(bool dark) => _settings.Theme switch
    {
        ThemeMode.Amoled => PaletteVariant.Amoled,
        ThemeMode.Dark => PaletteVariant.Dark,
        ThemeMode.Shadow => PaletteVariant.Shadow,
        ThemeMode.Mica => PaletteVariant.Mica,
        ThemeMode.Lightweight => PaletteVariant.Lightweight,
        ThemeMode.System or ThemeMode.Auto => dark ? _settings.DarkVariant : _settings.LightVariant,
        _ => PaletteVariant.Default
    };

    public event Action? Changed;

    public void Apply(bool onlyIfVariantChanged = false)
    {
        var dark = ResolveDark();
        var variant = ResolveVariant(dark);
        if (dark && variant == PaletteVariant.Lightweight || !dark && variant != PaletteVariant.Lightweight) variant = PaletteVariant.Default;
        var accent = _settings.UseSystemAccent ? SystemAccent() ?? _settings.Accent : _settings.Accent;
        if (onlyIfVariantChanged && dark == IsDark && variant == Variant && accent == EffectiveAccent) return;

        IsDark = dark;
        Variant = variant;
        IsAmoled = variant == PaletteVariant.Amoled;
        EffectiveAccent = accent;
        Palette = Palette.Build(dark, accent, variant);
        var app = Application.Current;
        if (app is null) return;

        foreach (var (key, rgb) in Palette.Colors)
        {
            var color = ToColor(rgb);
            app.Resources["C." + key] = color;
            app.Resources["B." + key] = new SolidColorBrush(color);
        }

        app.Resources["B.AccentGradient"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(ToColor(Oklch.FromRgb(Palette["Accent"]).WithL(Oklch.FromRgb(Palette["Accent"]).L + 0.12).ToRgb()), 0),
                new GradientStop(ToColor(Palette["Accent"]), 1)
            }
        };

        // Popups (dropdowns, menus, flyouts, tooltips) follow every theme, not only Fluent's dark/light.
        void Alias(string key, string token) => app.Resources[key] = app.Resources["B." + token];
        Alias("ComboBoxDropDownBackground", "SurfaceCard");
        Alias("ComboBoxDropDownBorderBrush", "Border");
        Alias("ComboBoxItemBackgroundPointerOver", "SurfaceHover");
        Alias("ComboBoxItemBackgroundPressed", "AccentMenu");
        Alias("ComboBoxItemBackgroundSelected", "AccentSoft");
        Alias("ComboBoxItemBackgroundSelectedPointerOver", "AccentMenu");
        Alias("ComboBoxItemBackgroundSelectedPressed", "AccentMenu");
        Alias("MenuFlyoutPresenterBackground", "SurfaceCard");
        Alias("MenuFlyoutPresenterBorderBrush", "Border");
        Alias("MenuFlyoutItemBackgroundPointerOver", "AccentMenu");
        Alias("FlyoutPresenterBackground", "SurfaceCard");
        Alias("FlyoutBorderThemeBrush", "Border");
        Alias("ToolTipBackground", "SurfaceRaised");
        Alias("ToolTipForeground", "TextPrimary");
        Alias("ToolTipBorderBrush", "Border");
        Alias("AutoCompleteBoxSuggestionsListBackground", "SurfaceCard");
        Alias("AutoCompleteBoxSuggestionsListBorderBrush", "Border");
        Alias("CalendarViewBackground", "SurfaceCard");

        var themeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        app.RequestedThemeVariant = themeVariant;
        Backdrop.ApplyAll(this);
        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent && fluent.Palettes.TryGetValue(themeVariant, out var p))
        {
            p.Accent = ToColor(Palette["Accent"]);
            p.RegionColor = ToColor(Palette["BgContent"]);
            p.AltHigh = ToColor(Palette["BgContent"]);
            p.ChromeLow = ToColor(Palette["BgPanel"]);
            p.ChromeMediumLow = ToColor(Palette["SurfaceCard"]);
            p.ChromeMedium = ToColor(Palette["SurfaceRaised"]);
            p.ListLow = ToColor(Palette["SurfaceHover"]);
            p.ListMedium = ToColor(Palette["Border"]);
            p.BaseHigh = ToColor(Palette["TextPrimary"]);
        }

        var needsTimer = _settings.Theme == ThemeMode.Auto;
        if (needsTimer && !_timer.IsEnabled) _timer.Start();
        if (!needsTimer && _timer.IsEnabled) _timer.Stop();

        // Notify after the current input/binding pass so views never refresh while a control is still updating.
        Dispatcher.UIThread.Post(() => Changed?.Invoke(), DispatcherPriority.Background);
    }

    private bool ResolveDark()
    {
        switch (_settings.Theme)
        {
            case ThemeMode.Light or ThemeMode.Lightweight:
                return false;
            case ThemeMode.System:
                return Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant != PlatformThemeVariant.Light;
            case ThemeMode.Auto:
                var lightFrom = TimeOnly.TryParse(_settings.LightFrom, out var lf) ? lf : new TimeOnly(7, 0);
                var darkFrom = TimeOnly.TryParse(_settings.DarkFrom, out var df) ? df : new TimeOnly(19, 0);
                return !ThemeSchedule.IsLight(DateTime.Now, _settings.AutoSource, lightFrom, darkFrom, _settings.Latitude, _settings.Longitude);
            default:
                return true;
        }
    }

    public static Color ToColor(Rgb rgb, byte alpha = 255) => Color.FromArgb(alpha, rgb.R8, rgb.G8, rgb.B8);

    public IBrush FolderBrush(double hue) => new SolidColorBrush(ToColor(FolderColors.ToRgb(hue, IsDark)));

    public IBrush Brush(string key) =>
        Application.Current?.Resources.TryGetValue("B." + key, out var v) == true && v is IBrush b ? b : Brushes.Gray;
}
