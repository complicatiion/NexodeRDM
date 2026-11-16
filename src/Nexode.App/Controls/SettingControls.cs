using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexode.App.Controls;

/// <summary>Global UI switches that many views bind to (hint lines).</summary>
public sealed partial class UiState : ObservableObject
{
    public static UiState Instance { get; } = new();

    [ObservableProperty]
    private bool _showHints = true;

    /// <summary>Ids of info cards closed with their X.</summary>
    public HashSet<string> Dismissed { get; } = [];

    /// <summary>Raised when a card was closed or the closed cards were reset.</summary>
    public event Action? DismissedChanged;

    public void Dismiss(string id)
    {
        if (!Dismissed.Add(id)) return;
        var s = Services.AppHost.Settings;
        if (!s.DismissedHints.Contains(id)) s.DismissedHints.Add(id);
        s.Save();
        DismissedChanged?.Invoke();
    }

    public void ResetDismissed()
    {
        Dismissed.Clear();
        Services.AppHost.Settings.DismissedHints.Clear();
        Services.AppHost.Settings.Save();
        DismissedChanged?.Invoke();
    }
}

/// <summary>
/// Small info card (round "i" in the accent color, text, X). Closed cards stay closed; all cards are hidden while hints are
/// switched off. <see cref="Variant"/> "warning" shows a warning card.
/// </summary>
public sealed class InfoCard : ContentControl
{
    public static readonly StyledProperty<string?> HintIdProperty = AvaloniaProperty.Register<InfoCard, string?>(nameof(HintId));
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<InfoCard, string?>(nameof(Title));
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<InfoCard, string?>(nameof(Text));
    public static readonly StyledProperty<string> IconProperty = AvaloniaProperty.Register<InfoCard, string>(nameof(Icon), "info");
    public static readonly StyledProperty<bool> DismissibleProperty = AvaloniaProperty.Register<InfoCard, bool>(nameof(Dismissible), true);
    public static readonly StyledProperty<bool> FollowHintsProperty = AvaloniaProperty.Register<InfoCard, bool>(nameof(FollowHints), true);
    public static readonly StyledProperty<bool> WantedProperty = AvaloniaProperty.Register<InfoCard, bool>(nameof(Wanted), true);

    public InfoCard()
    {
        var weak = new WeakReference<InfoCard>(this);
        void Handler()
        {
            if (weak.TryGetTarget(out var me)) me.UpdateVisibility();
        }

        UiState.Instance.PropertyChanged += (_, _) => Handler();
        UiState.Instance.DismissedChanged += Handler;
        UpdateVisibility();
    }

    /// <summary>Remembers the X; without an id the card cannot be closed for good.</summary>
    public string? HintId { get => GetValue(HintIdProperty); set => SetValue(HintIdProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public bool Dismissible { get => GetValue(DismissibleProperty); set => SetValue(DismissibleProperty, value); }
    /// <summary>Hidden while "Show hints" is off.</summary>
    public bool FollowHints { get => GetValue(FollowHintsProperty); set => SetValue(FollowHintsProperty, value); }
    /// <summary>Extra condition from the view model (e.g. only while something applies).</summary>
    public bool Wanted { get => GetValue(WantedProperty); set => SetValue(WantedProperty, value); }

    public bool HasTitle => !string.IsNullOrEmpty(Title);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HintIdProperty || change.Property == FollowHintsProperty || change.Property == WantedProperty) UpdateVisibility();
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (e.NameScope.Find<Button>("PART_Close") is { } close) close.Click += (_, _) => Close();
    }

    public void Close()
    {
        if (HintId is { } id) UiState.Instance.Dismiss(id);
        else IsVisible = false;
    }

    private void UpdateVisibility() =>
        IsVisible = Wanted && (!FollowHints || UiState.Instance.ShowHints) && (HintId is null || !UiState.Instance.Dismissed.Contains(HintId));
}

/// <summary>One settings line: label (optional info tooltip) left, control right-aligned with a fixed width.</summary>
public sealed class SettingRow : ContentControl
{
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Label));
    public static readonly StyledProperty<string?> InfoProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Info));
    public static readonly StyledProperty<string?> IconProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Icon));

    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    /// <summary>Tooltip of the round "i" next to the label.</summary>
    public string? Info { get => GetValue(InfoProperty); set => SetValue(InfoProperty, value); }
    public string? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
}

/// <summary>Explanatory line with a round "i"; hidden when hints are switched off (unless <see cref="Always"/>).</summary>
public sealed class Hint : TemplatedControl
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<Hint, string?>(nameof(Text));
    public static readonly StyledProperty<bool> AlwaysProperty = AvaloniaProperty.Register<Hint, bool>(nameof(Always));

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool Always { get => GetValue(AlwaysProperty); set => SetValue(AlwaysProperty, value); }

    public Hint()
    {
        UiState.Instance.PropertyChanged += (_, _) => UpdateVisibility();
        UpdateVisibility();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AlwaysProperty) UpdateVisibility();
    }

    private void UpdateVisibility() => IsVisible = Always || UiState.Instance.ShowHints;
}

/// <summary>Small vector flag for the language selection (generic drawing, no image assets).</summary>
public sealed class FlagIcon : Control
{
    public static readonly StyledProperty<string?> CodeProperty = AvaloniaProperty.Register<FlagIcon, string?>(nameof(Code));

    static FlagIcon() => AffectsRender<FlagIcon>(CodeProperty);

    public string? Code { get => GetValue(CodeProperty); set => SetValue(CodeProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(22, 15);

    public override void Render(DrawingContext context)
    {
        var r = new Rect(0, 0, Bounds.Width, Bounds.Height);
        using var clip = context.PushClip(new RoundedRect(r, 2.5));
        switch (Code)
        {
            case "fr":
            {
                var w = r.Width / 3;
                context.FillRectangle(new SolidColorBrush(Color.Parse("#002654")), new Rect(0, 0, w, r.Height));
                context.FillRectangle(Brushes.White, new Rect(w, 0, w, r.Height));
                context.FillRectangle(new SolidColorBrush(Color.Parse("#CE1126")), new Rect(2 * w, 0, r.Width - 2 * w, r.Height));
                break;
            }
            case "es":
            {
                var h = r.Height / 4;
                var red = new SolidColorBrush(Color.Parse("#AA151B"));
                context.FillRectangle(red, new Rect(0, 0, r.Width, h));
                context.FillRectangle(new SolidColorBrush(Color.Parse("#F1BF00")), new Rect(0, h, r.Width, 2 * h));
                context.FillRectangle(red, new Rect(0, 3 * h, r.Width, r.Height - 3 * h));
                break;
            }
            case "de":
            {
                var h = r.Height / 3;
                context.FillRectangle(new SolidColorBrush(Color.Parse("#000000")), new Rect(0, 0, r.Width, h));
                context.FillRectangle(new SolidColorBrush(Color.Parse("#DD0000")), new Rect(0, h, r.Width, h));
                context.FillRectangle(new SolidColorBrush(Color.Parse("#FFCE00")), new Rect(0, 2 * h, r.Width, r.Height - 2 * h));
                break;
            }
            default:
            {
                // United Kingdom
                var blue = new SolidColorBrush(Color.Parse("#012169"));
                var red = new SolidColorBrush(Color.Parse("#C8102E"));
                context.FillRectangle(blue, r);
                var w = r.Width;
                var h = r.Height;
                context.DrawLine(new Pen(Brushes.White, h * 0.2), new Point(0, 0), new Point(w, h));
                context.DrawLine(new Pen(Brushes.White, h * 0.2), new Point(w, 0), new Point(0, h));
                context.DrawLine(new Pen(red, h * 0.08), new Point(0, 0), new Point(w, h));
                context.DrawLine(new Pen(red, h * 0.08), new Point(w, 0), new Point(0, h));
                context.FillRectangle(Brushes.White, new Rect(w / 2 - h * 0.17, 0, h * 0.34, h));
                context.FillRectangle(Brushes.White, new Rect(0, h / 2 - h * 0.17, w, h * 0.34));
                context.FillRectangle(red, new Rect(w / 2 - h * 0.1, 0, h * 0.2, h));
                context.FillRectangle(red, new Rect(0, h / 2 - h * 0.1, w, h * 0.2));
                break;
            }
        }

        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)), 1), new RoundedRect(r.Deflate(0.5), 2.5));
    }
}

/// <summary>Read-only path/text in a pill: scrolls horizontally, selectable, with a copy button.</summary>
public sealed class PathPill : TemplatedControl
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<PathPill, string?>(nameof(Text));

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (e.NameScope.Find<Button>("PART_Copy") is { } copy)
        {
            copy.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb && Text is { } t) await cb.SetTextAsync(t);
                if (TopLevel.GetTopLevel(this)?.DataContext is ViewModels.MainWindowViewModel vm) vm.ShowToast(Localization.Loc.T("Toast.PathCopied"));
            };
        }
    }
}
