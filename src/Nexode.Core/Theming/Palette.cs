namespace Nexode.Core.Theming;

/// <summary>Surface family of a theme (the accent is applied on top).</summary>
public enum PaletteVariant
{
    /// <summary>Nexode dark or light (violet-tinted grays).</summary>
    Default = 0,
    /// <summary>True black (dark only).</summary>
    Amoled = 1,
    /// <summary>Neutral dark gray around #202020 (dark only; was called "Shadow" up to 0.0.5).</summary>
    Dark = 2,
    /// <summary>Windows 11 style dark; real Mica backdrop where the system supports it (dark only).</summary>
    Mica = 3,
    /// <summary>Mid-gray light theme (light only).</summary>
    Lightweight = 4,
    /// <summary>Medium dark gray between light and dark, light text (dark only).</summary>
    Shadow = 5
}

/// <summary>All color tokens of one theme variant.</summary>
public sealed record Palette
{
    public required bool IsDark { get; init; }
    public bool IsAmoled { get; init; }
    public PaletteVariant Variant { get; init; }
    public required IReadOnlyDictionary<string, Rgb> Colors { get; init; }

    public Rgb this[string key] => Colors[key];

    /// <summary>Accent presets shown in Settings.</summary>
    public static IReadOnlyList<(string Name, string Hex)> AccentPresets { get; } =
    [
        ("Nexode", "#573CFA"), ("Blue", "#2563EB"), ("Cyan", "#0891B2"), ("Teal", "#0D9488"),
        ("Green", "#16A34A"), ("Amber", "#D97706"), ("Orange", "#EA580C"), ("Rose", "#E11D48"), ("Pink", "#DB2777"),
        ("Purple", "#9333EA"), ("Slate", "#64748B")
    ];

    public const string DefaultAccent = "#573CFA";
    public const string Danger = "#E8083E";
    public const string Success = "#02864A";
    public const string Warning = "#FB8D1A";

    /// <summary>Builds the palette for one variant; accent-derived tokens are contrast-corrected.</summary>
    public static Palette Build(bool dark, string accentHex, bool amoled = false) =>
        Build(dark, accentHex, amoled ? PaletteVariant.Amoled : PaletteVariant.Default);

    /// <summary>Builds the palette; dark-only variants fall back to the default light theme and vice versa.</summary>
    public static Palette Build(bool dark, string accentHex, PaletteVariant variant)
    {
        if (dark && variant == PaletteVariant.Lightweight || !dark && variant != PaletteVariant.Lightweight) variant = PaletteVariant.Default;
        var amoled = variant == PaletteVariant.Amoled;
        if (!Rgb.TryParse(accentHex, out var accent)) accent = Rgb.FromHex(DefaultAccent);

        var c = new Dictionary<string, Rgb>();
        void Set(string key, string hex) => c[key] = Rgb.FromHex(hex);

        if (amoled)
        {
            // True black for OLED panels; cards stay a touch lighter so structure remains visible.
            Set("BgWindow", "#000000");
            Set("BgRail", "#000000");
            Set("BgPanel", "#000000");
            Set("BgContent", "#000000");
            Set("SurfaceCard", "#0F0F12");
            Set("SurfaceRaised", "#17171C");
            Set("SurfaceHover", "#1E1E24");
            Set("Border", "#26262E");
            Set("BorderStrong", "#34343E");
            Set("TextPrimary", "#F2F2F5");
            Set("TextSecondary", "#9A9AA8");
            Set("TextDisabled", "#64646F");
        }
        else if (variant == PaletteVariant.Shadow)
        {
            // Medium gray: brighter than the dark themes, still light text on gray.
            Set("BgWindow", "#333336");
            Set("BgRail", "#2E2E31");
            Set("BgPanel", "#38383B");
            Set("BgContent", "#3B3B3F");
            Set("SurfaceCard", "#45454A");
            Set("SurfaceRaised", "#4B4B50");
            Set("SurfaceHover", "#525258");
            Set("Border", "#55555B");
            Set("BorderStrong", "#6A6A71");
            Set("TextPrimary", "#F3F3F5");
            Set("TextSecondary", "#C0C0C7");
            Set("TextDisabled", "#8A8A91");
        }
        else if (variant == PaletteVariant.Dark)
        {
            // Neutral grays without the violet tint; only the accent adds color.
            Set("BgWindow", "#1A1A1A");
            Set("BgRail", "#161616");
            Set("BgPanel", "#1E1E1E");
            Set("BgContent", "#202020");
            Set("SurfaceCard", "#282828");
            Set("SurfaceRaised", "#2E2E2E");
            Set("SurfaceHover", "#343434");
            Set("Border", "#363636");
            Set("BorderStrong", "#484848");
            Set("TextPrimary", "#EDEDED");
            Set("TextSecondary", "#A3A3A3");
            Set("TextDisabled", "#6E6E6E");
        }
        else if (variant == PaletteVariant.Mica)
        {
            // Windows 11 dark: #202020 base, layered cards; the backdrop replaces window/rail when Mica is active.
            Set("BgWindow", "#202020");
            Set("BgRail", "#1C1C1C");
            Set("BgPanel", "#232323");
            Set("BgContent", "#272727");
            Set("SurfaceCard", "#2D2D2D");
            Set("SurfaceRaised", "#323232");
            Set("SurfaceHover", "#383838");
            Set("Border", "#3A3A3A");
            Set("BorderStrong", "#4B4B4B");
            Set("TextPrimary", "#FFFFFF");
            Set("TextSecondary", "#B8B8B8");
            Set("TextDisabled", "#787878");
        }
        else if (dark)
        {
            Set("BgWindow", "#1C1A27");
            Set("BgRail", "#18161F");
            Set("BgPanel", "#211F2C");
            Set("BgContent", "#24222F");
            Set("SurfaceCard", "#2B2938");
            Set("SurfaceRaised", "#312F3F");
            Set("SurfaceHover", "#353345");
            Set("Border", "#383646");
            Set("BorderStrong", "#4A4759");
            Set("TextPrimary", "#ECEBF2");
            Set("TextSecondary", "#9B98AD");
            Set("TextDisabled", "#6E6B80");
        }
        else if (variant == PaletteVariant.Lightweight)
        {
            // Mid-gray light theme: less glare than the bright default.
            Set("BgWindow", "#C4C4CA");
            Set("BgRail", "#BCBCC2");
            Set("BgPanel", "#CACAD0");
            Set("BgContent", "#D0D0D5");
            Set("SurfaceCard", "#DCDCE0");
            Set("SurfaceRaised", "#C8C8CE");
            Set("SurfaceHover", "#BEBEC5");
            Set("Border", "#B2B2BA");
            Set("BorderStrong", "#9C9CA6");
            Set("TextPrimary", "#16151C");
            Set("TextSecondary", "#43414D");
            Set("TextDisabled", "#73717D");
        }
        else
        {
            Set("BgWindow", "#E9E8EF");
            Set("BgRail", "#E3E2EA");
            Set("BgPanel", "#EFEEF4");
            Set("BgContent", "#F3F2F7");
            Set("SurfaceCard", "#F8F8FB");
            Set("SurfaceRaised", "#EBEAF1");
            Set("SurfaceHover", "#E2E0EA");
            Set("Border", "#D9D7E2");
            Set("BorderStrong", "#C4C1D0");
            Set("TextPrimary", "#1C1A27");
            Set("TextSecondary", "#5F5C71");
            Set("TextDisabled", "#8E8B9C");
        }

        var panel = amoled ? c["SurfaceCard"] : c["BgPanel"];
        var white = new Rgb(1, 1, 1);

        // Filled accent: white text must stay readable (≥ 4.5:1).
        var fill = Darken(accent, white, 4.5);
        c["Accent"] = fill;
        var lch = Oklch.FromRgb(fill);
        c["AccentHover"] = lch.WithL(lch.L + (dark ? 0.05 : -0.05)).ToRgb();
        c["AccentPressed"] = lch.WithL(lch.L + (dark ? -0.05 : -0.1)).ToRgb();
        c["AccentText"] = Readable(accent, panel, dark, 4.5);
        c["AccentSoft"] = panel.Mix(accent, dark ? 0.22 : 0.14);
        c["AccentMenu"] = panel.Mix(accent, dark ? 0.32 : 0.22);
        c["AccentTab"] = c["BgContent"].Mix(accent, dark ? 0.16 : 0.12);
        c["OnAccent"] = white;

        foreach (var (key, hex) in new[] { ("Danger", Danger), ("Success", Success), ("Warning", Warning), ("Info", "#3B82F6") })
        {
            var baseColor = Rgb.FromHex(hex);
            c[key] = baseColor;
            c[key + "Text"] = Readable(baseColor, panel, dark, 4.5);
            c[key + "Soft"] = panel.Mix(baseColor, dark ? 0.2 : 0.13);
        }

        // Danger fill with white text.
        c["DangerFill"] = Darken(Rgb.FromHex(Danger), white, 4.5);
        c["DangerFillHover"] = Oklch.FromRgb(c["DangerFill"]).WithL(Oklch.FromRgb(c["DangerFill"]).L + 0.05).ToRgb();
        c["Shadow"] = dark ? new Rgb(0, 0, 0) : new Rgb(0.2, 0.18, 0.3);

        return new Palette { IsDark = dark, IsAmoled = amoled, Variant = variant, Colors = c };
    }

    /// <summary>Moves the color's lightness until it reaches the contrast ratio against <paramref name="background"/>.</summary>
    public static Rgb Readable(Rgb color, Rgb background, bool darkBackground, double ratio)
    {
        var lch = Oklch.FromRgb(color);
        var current = color;
        for (var i = 0; i < 60 && Rgb.Contrast(current, background) < ratio; i++)
        {
            lch = lch.WithL(lch.L + (darkBackground ? 0.01 : -0.01));
            current = lch.ToRgb();
        }

        return current;
    }

    private static Rgb Darken(Rgb color, Rgb text, double ratio)
    {
        var lch = Oklch.FromRgb(color);
        var current = color;
        for (var i = 0; i < 60 && Rgb.Contrast(current, text) < ratio; i++)
        {
            lch = lch.WithL(lch.L - 0.01);
            current = lch.ToRgb();
        }

        return current;
    }
}
