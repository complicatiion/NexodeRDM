using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Nexode.App.Controls;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.Core.Model;
using Nexode.Core.Theming;

namespace Nexode.App.Views;

/// <summary>Helpers for code-built menus: every item gets an icon left of the text.</summary>
public static class MenuBuilder
{
    public static MenuItem Item(string textKey, string icon, Action onClick, string? gesture = null, bool enabled = true, bool danger = false, bool bold = false, bool raw = false)
    {
        var item = new MenuItem
        {
            Header = raw ? textKey : Loc.T(textKey),
            Icon = new LineIcon { Icon = icon, Size = 15 },
            IsEnabled = enabled,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal
        };
        if (gesture is not null) item.InputGesture = KeyGesture.Parse(gesture);
        if (danger) item.Classes.Add("danger");
        item.Click += (_, _) => onClick();
        return item;
    }

    public static MenuItem Sub(string textKey, string icon, IEnumerable<Control> children, bool enabled = true)
    {
        var item = new MenuItem { Header = Loc.T(textKey), Icon = new LineIcon { Icon = icon, Size = 15 }, IsEnabled = enabled };
        foreach (var c in children) item.Items.Add(c);
        return item;
    }

    public static MenuItem Check(string text, bool isChecked, Action onClick, Control? icon = null)
    {
        var item = new MenuItem
        {
            Header = text,
            Icon = icon ?? (isChecked ? new LineIcon { Icon = "check", Size = 15 } : null),
            FontWeight = isChecked ? FontWeight.SemiBold : FontWeight.Normal
        };
        item.Click += (_, _) => onClick();
        return item;
    }

    public static Control Dot(IBrush brush) => new Ellipse { Width = 12, Height = 12, Fill = brush };

    public static IEnumerable<Control> NewItems(Func<EntryKind, Task> create) =>
        Features.CreatableKinds().Select(k => (Control)Item("Kind." + k, EntryKinds.Icon(k), () => _ = create(k)));

    /// <summary>"New from template" submenu: built-in templates, then the data source's own.</summary>
    public static MenuItem TemplateMenu(Func<EntryTemplate, Task> create)
    {
        var templates = Templates.Available();
        var items = new List<Control>();
        var builtIn = true;
        foreach (var t in templates)
        {
            if (builtIn && !Templates.IsBuiltIn(t))
            {
                if (items.Count > 0) items.Add(new Separator());
                builtIn = false;
            }

            var template = t;
            var item = new MenuItem { Header = t.Name, Icon = new LineIcon { Icon = t.Entry.Icon ?? EntryKinds.Icon(t.Entry.Kind), Size = 15 } };
            if (!string.IsNullOrEmpty(t.Description)) ToolTip.SetTip(item, t.Description);
            item.Click += (_, _) => _ = create(template);
            items.Add(item);
        }

        return Sub("Menu.FromTemplate", "layout-template", items, items.Count > 0);
    }

    public static IEnumerable<Control> ColorItems(double? current, Action<double?> set)
    {
        var theme = AppHost.Theme;
        yield return Check(Loc.T("Color.None"), current is null, () => set(null), new LineIcon { Icon = "circle-x", Size = 15 });
        yield return new Separator();
        foreach (var hue in FolderColors.Swatches)
        {
            var selected = current is { } c && FolderColors.Distance(c, hue) < 1;
            yield return Check(Loc.T("Color.H" + (int)hue), selected, () => set(hue), Dot(theme.FolderBrush(hue)));
        }
    }

    /// <summary>Personal pin color: entry color (default) or a preset.</summary>
    public static IEnumerable<Control> PinColorItems(double? current, Action<double?> set)
    {
        var theme = AppHost.Theme;
        yield return Check(Loc.T("Menu.PinColorEntry"), current is null, () => set(null), new LineIcon { Icon = "rotate-ccw", Size = 15 });
        yield return new Separator();
        foreach (var hue in FolderColors.Swatches)
        {
            var selected = current is { } c && FolderColors.Distance(c, hue) < 1;
            yield return Check(Loc.T("Color.H" + (int)hue), selected, () => set(hue), Dot(theme.FolderBrush(hue)));
        }
    }
}
