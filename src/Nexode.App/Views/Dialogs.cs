using System.Security.Cryptography;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Nexode.App.Controls;
using Nexode.App.Localization;

namespace Nexode.App.Views;

/// <summary>Small modal dialogs built in code: title, content, buttons (main action right, cancel left of it).</summary>
public class DialogWindow : Window
{
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ContentControl _body = new();

    public DialogWindow(string title, string? icon = null, double width = 460)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = Application.Current is { } ? new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Nexode/Assets/nexode.ico"))) : null;
        Background = Application.Current?.FindResource("B.BgContent") as IBrush;

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 14) };
        if (icon is not null)
        {
            header.Children.Add(new LineIcon { Icon = icon, Size = 20, [!LineIcon.ForegroundProperty] = new DynamicResourceExtension("B.AccentText") });
        }

        header.Children.Add(new TextBlock { Text = title, Classes = { "h2" }, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });

        var root = new DockPanel { Margin = new Thickness(22, 20, 22, 18) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_buttons, Dock.Bottom);
        _buttons.Margin = new Thickness(0, 18, 0, 0);
        root.Children.Add(header);
        root.Children.Add(_buttons);
        root.Children.Add(_body);
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(null);
        };
    }

    public object? Body
    {
        get => _body.Content;
        set => _body.Content = value;
    }

    public Button AddButton(string text, string? icon, bool primary, bool danger, Func<bool>? onClick, object? result)
    {
        var button = new Button
        {
            Content = new IconLabel { Icon = icon, Text = text },
            IsDefault = primary,
            IsCancel = !primary && result is null or false,
            MinWidth = 96,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        if (primary) button.Classes.Add(danger ? "danger" : "primary");
        button.Click += (_, _) =>
        {
            if (onClick?.Invoke() == false) return;
            Close(result);
        };
        _buttons.Children.Add(button);
        return button;
    }

    /// <summary>Button whose async handler may keep the dialog open (returns false); buttons are disabled while it runs.</summary>
    public Button AddAsyncButton(string text, string? icon, Func<Task<bool>> onClick, object? result)
    {
        var button = new Button
        {
            Content = new IconLabel { Icon = icon, Text = text },
            IsDefault = true,
            MinWidth = 96,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Classes = { "primary" }
        };
        button.Click += async (_, _) =>
        {
            foreach (var b in _buttons.Children.OfType<Button>()) b.IsEnabled = false;
            bool ok;
            try
            {
                ok = await onClick();
            }
            finally
            {
                foreach (var b in _buttons.Children.OfType<Button>()) b.IsEnabled = true;
            }

            if (ok) Close(result);
        };
        _buttons.Children.Add(button);
        return button;
    }

    public static TextBlock Text(string text, bool muted = false) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { muted ? "muted" : "" }, LineHeight = 19 };
}

public static class Dialog
{
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string okText, bool danger = false, string? icon = null)
    {
        var dlg = new DialogWindow(title, icon ?? (danger ? "triangle-alert" : "circle-help")) { Body = DialogWindow.Text(message) };
        dlg.AddButton(Loc.T("Cancel"), "x", false, false, null, false);
        dlg.AddButton(okText, danger ? "trash-2" : "check", true, danger, null, true);
        return await dlg.ShowDialog<object?>(owner) is true;
    }

    public static async Task InfoAsync(Window owner, string title, string message, string icon = "info")
    {
        var dlg = new DialogWindow(title, icon) { Body = DialogWindow.Text(message) };
        dlg.AddButton(Loc.T("Ok"), "check", true, false, null, true);
        await dlg.ShowDialog<object?>(owner);
    }

    public static async Task<string?> InputAsync(Window owner, string title, string label, string? initial, string icon = "pencil")
    {
        var box = new TextBox { Text = initial ?? "" };
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = label, Classes = { "label" } });
        panel.Children.Add(box);
        var dlg = new DialogWindow(title, icon) { Body = panel };
        dlg.AddButton(Loc.T("Cancel"), "x", false, false, null, null);
        dlg.AddButton(Loc.T("Ok"), "check", true, false, () => !string.IsNullOrWhiteSpace(box.Text), "ok");
        dlg.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return await dlg.ShowDialog<object?>(owner) is "ok" ? box.Text!.Trim() : null;
    }

    /// <summary>Password with an async check (Argon2 off the UI thread); the dialog stays open on failure.</summary>
    public static async Task<string?> PasswordCheckAsync(Window owner, string title, string? message, Func<string, Task<bool>> validate, string icon = "lock-open")
    {
        var box = new TextBox { PasswordChar = '•', PlaceholderText = Loc.T("Password") };
        var status = new TextBlock { Classes = { "small" }, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 8 };
        if (!string.IsNullOrEmpty(message)) panel.Children.Add(DialogWindow.Text(message, muted: true));
        panel.Children.Add(box);
        panel.Children.Add(status);
        var dlg = new DialogWindow(title, icon) { Body = panel };
        dlg.AddButton(Loc.T("Cancel"), "x", false, false, null, null);
        dlg.AddAsyncButton(Loc.T("Vault.Unlock"), "lock-open", async () =>
        {
            var pw = box.Text ?? "";
            if (pw.Length == 0) return false;
            status.Classes.Remove("danger");
            status.Text = Loc.T("Vault.Checking");
            status.IsVisible = true;
            box.IsEnabled = false;
            var ok = await validate(pw);
            box.IsEnabled = true;
            if (ok) return true;
            status.Classes.Add("danger");
            status.Text = Loc.T("PasswordWrong");
            box.SelectAll();
            box.Focus();
            return false;
        }, "ok");
        dlg.Opened += (_, _) => box.Focus();
        return await dlg.ShowDialog<object?>(owner) is "ok" ? box.Text : null;
    }

    /// <summary>User name and password for "ask on connect" entries. Null = cancelled.</summary>
    public static async Task<(string User, string Password)?> CredentialsAsync(Window owner, string title, string target, string? user)
    {
        var userBox = new TextBox { Text = user ?? "", PlaceholderText = Loc.T("Field.Username") };
        var pwBox = new TextBox { PasswordChar = '•', PlaceholderText = Loc.T("Password") };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(DialogWindow.Text(target, muted: true));
        panel.Children.Add(userBox);
        panel.Children.Add(pwBox);
        var dlg = new DialogWindow(title, "key-round") { Body = panel };
        dlg.AddButton(Loc.T("Cancel"), "x", false, false, null, null);
        dlg.AddButton(Loc.T("Session.Connect"), "plug-zap", true, false, null, "ok");
        dlg.Opened += (_, _) =>
        {
            if (string.IsNullOrEmpty(userBox.Text)) userBox.Focus();
            else pwBox.Focus();
        };
        return await dlg.ShowDialog<object?>(owner) is "ok" ? (userBox.Text ?? "", pwBox.Text ?? "") : null;
    }

    /// <summary>Unknown or changed server identity (SSH host key, TLS certificate).</summary>
    public static async Task<bool> HostKeyAsync(Window owner, Nexode.Protocols.HostKeyCheck check)
    {
        var panel = new StackPanel { Spacing = 10 };
        if (check.IsChanged)
        {
            panel.Children.Add(new Border
            {
                Classes = { "notice", "danger" },
                Child = DialogWindow.Text(Loc.F("HostKey.ChangedText", check.Host, check.Port))
            });
        }
        else
        {
            panel.Children.Add(DialogWindow.Text(Loc.F(check.Kind == "tls" ? "HostKey.NewTlsText" : "HostKey.NewText", check.Host, check.Port)));
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        void Row(int r, string label, string value)
        {
            var l = new TextBlock { Text = label, Classes = { "label" }, Margin = new Thickness(0, 0, 14, 6) };
            var v = new SelectableTextBlock { Text = value, Classes = { "mono" }, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            Grid.SetRow(l, r);
            Grid.SetRow(v, r);
            Grid.SetColumn(v, 1);
            grid.Children.Add(l);
            grid.Children.Add(v);
        }

        Row(0, check.Kind == "tls" ? Loc.T("HostKey.Subject") : Loc.T("HostKey.Algorithm"), check.Algorithm);
        Row(1, Loc.T("HostKey.Fingerprint"), check.Fingerprint);
        if (check.KnownFingerprint is { } known) Row(2, Loc.T("HostKey.Known"), known);
        panel.Children.Add(grid);
        var dlg = new DialogWindow(Loc.T(check.IsChanged ? "HostKey.ChangedTitle" : "HostKey.NewTitle"), check.IsChanged ? "shield-alert" : "shield-question", 560) { Body = panel };
        dlg.AddButton(Loc.T("Cancel"), "x", false, false, null, false);
        dlg.AddButton(Loc.T(check.IsChanged ? "HostKey.TrustChanged" : "HostKey.Trust"), "shield-check", true, check.IsChanged, null, true);
        return await dlg.ShowDialog<object?>(owner) is true;
    }

    /// <summary>Several actions as buttons; returns the index or null when cancelled.</summary>
    public static async Task<int?> ChoiceAsync(Window owner, string title, string message, IReadOnlyList<(string Text, string Icon, bool Danger)> options)
    {
        var dlg = new DialogWindow(title, "circle-help", 520) { Body = DialogWindow.Text(message) };
        dlg.AddButton(Loc.T("Cancel"), "x", false, false, null, null);
        for (var i = 0; i < options.Count; i++)
        {
            var (text, icon, danger) = options[i];
            var b = dlg.AddButton(text, icon, i == options.Count - 1, danger, null, i);
            if (i < options.Count - 1) b.IsCancel = false;
        }

        return await dlg.ShowDialog<object?>(owner) is int index ? index : null;
    }

    /// <summary>Vertical list of options (icon, title, optional description); returns the index or null.</summary>
    public static async Task<int?> PickAsync(Window owner, string title, string message, IReadOnlyList<(string Text, string Icon, string? Description)> options)
    {
        var list = new StackPanel { Spacing = 6, Margin = new Thickness(0, string.IsNullOrEmpty(message) ? 0 : 12, 0, 0) };
        DialogWindow? dlg = null;
        for (var i = 0; i < options.Count; i++)
        {
            var (text, icon, description) = options[i];
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            g.Children.Add(new LineIcon { Icon = icon, Size = 18, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                [!LineIcon.ForegroundProperty] = new DynamicResourceExtension("B.AccentText") });
            var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = text, FontWeight = FontWeight.SemiBold });
            if (!string.IsNullOrEmpty(description)) texts.Children.Add(new TextBlock { Text = description, Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(texts, 1);
            g.Children.Add(texts);
            var chevron = new LineIcon { Icon = "chevron-right", Size = 15, VerticalAlignment = VerticalAlignment.Center, [!LineIcon.ForegroundProperty] = new DynamicResourceExtension("B.TextSecondary") };
            Grid.SetColumn(chevron, 2);
            g.Children.Add(chevron);
            var index = i;
            var b = new Button { Classes = { "tilebtn" }, Content = new Border { Classes = { "card", "tilecard" }, Padding = new Thickness(12, 9), Child = g } };
            b.Click += (_, _) => dlg?.Close(index);
            list.Children.Add(b);
        }

        var body = new StackPanel();
        if (!string.IsNullOrEmpty(message)) body.Children.Add(DialogWindow.Text(message, true));
        body.Children.Add(list);
        dlg = new DialogWindow(title, "file-down", 520) { Body = body };
        dlg.AddButton(Loc.T("Cancel"), "x", false, false, null, null);
        return await dlg.ShowDialog<object?>(owner) is int r ? r : null;
    }

    /// <summary>Password generator; returns the chosen password or null.</summary>
    public static async Task<string?> GeneratePasswordAsync(Window owner)
    {
        var length = new Slider { Minimum = 8, Maximum = 64, Value = 24, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 260 };
        var lengthText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Width = 28, Classes = { "mono" } };
        var upper = new ToggleSwitch { IsChecked = true, OnContent = "A–Z", OffContent = "A–Z" };
        var lower = new ToggleSwitch { IsChecked = true, OnContent = "a–z", OffContent = "a–z" };
        var digits = new ToggleSwitch { IsChecked = true, OnContent = "0–9", OffContent = "0–9" };
        var symbols = new ToggleSwitch { IsChecked = true, OnContent = "!#%", OffContent = "!#%" };
        var ambiguous = new CheckBox { Content = Loc.T("Generator.NoAmbiguous"), IsChecked = true };
        var output = new TextBox { Classes = { "mono" }, FontSize = 15, IsReadOnly = true };
        var regen = new Button { Classes = { "icon" }, Content = new LineIcon { Icon = "refresh-cw", Size = 16 } };
        ToolTip.SetTip(regen, Loc.T("Generator.New"));

        void Generate()
        {
            lengthText.Text = ((int)length.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            output.Text = PasswordGenerator.Create((int)length.Value, upper.IsChecked == true, lower.IsChecked == true, digits.IsChecked == true,
                symbols.IsChecked == true, ambiguous.IsChecked == true);
        }

        length.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) Generate();
        };
        foreach (var t in new ToggleButton[] { upper, lower, digits, symbols, ambiguous }) t.IsCheckedChanged += (_, _) => Generate();
        regen.Click += (_, _) => Generate();
        Generate();

        var lengthRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        lengthRow.Children.Add(new TextBlock { Text = Loc.T("Generator.Length"), VerticalAlignment = VerticalAlignment.Center, Width = 70 });
        lengthRow.Children.Add(length);
        lengthRow.Children.Add(lengthText);
        var sets = new WrapPanel { ItemSpacing = 14 };
        sets.Children.Add(upper);
        sets.Children.Add(lower);
        sets.Children.Add(digits);
        sets.Children.Add(symbols);
        var outRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(regen, 1);
        regen.Margin = new Thickness(6, 0, 0, 0);
        outRow.Children.Add(output);
        outRow.Children.Add(regen);
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(outRow);
        panel.Children.Add(lengthRow);
        panel.Children.Add(sets);
        panel.Children.Add(ambiguous);
        var dlg = new DialogWindow(Loc.T("Generator.Title"), "wand-sparkles", 500) { Body = panel };
        dlg.AddButton(Loc.T("Cancel"), "x", false, false, null, null);
        dlg.AddButton(Loc.T("Generator.Use"), "check", true, false, null, "ok");
        return await dlg.ShowDialog<object?>(owner) is "ok" ? output.Text : null;
    }

    /// <summary>Asks for a password; with <paramref name="confirm"/> twice plus a minimum length.</summary>
    public static async Task<string?> PasswordAsync(Window owner, string title, string? message, bool confirm, int minLength = 0,
        Func<string, bool>? validate = null, string icon = "lock")
    {
        var first = new TextBox { PasswordChar = '•', RevealPassword = false, PlaceholderText = Loc.T("Password") };
        var second = new TextBox { PasswordChar = '•', PlaceholderText = Loc.T("PasswordRepeat"), IsVisible = confirm };
        var error = new TextBlock { Classes = { "danger", "small" }, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 8 };
        if (!string.IsNullOrEmpty(message)) panel.Children.Add(DialogWindow.Text(message, muted: true));
        panel.Children.Add(first);
        panel.Children.Add(second);
        panel.Children.Add(error);

        var dlg = new DialogWindow(title, icon) { Body = panel };
        dlg.AddButton(Loc.T("Cancel"), "x", false, false, null, null);
        dlg.AddButton(Loc.T("Ok"), "check", true, false, () =>
        {
            var pw = first.Text ?? "";
            string? problem = null;
            if (pw.Length == 0 || pw.Length < minLength) problem = Loc.F("PasswordTooShort", Math.Max(1, minLength));
            else if (confirm && pw != second.Text) problem = Loc.T("PasswordMismatch");
            else if (validate is not null && !validate(pw)) problem = Loc.T("PasswordWrong");
            error.Text = problem;
            error.IsVisible = problem is not null;
            if (problem is not null)
            {
                first.SelectAll();
                first.Focus();
            }

            return problem is null;
        }, "ok");
        dlg.Opened += (_, _) => first.Focus();
        return await dlg.ShowDialog<object?>(owner) is "ok" ? first.Text : null;
    }
}

/// <summary>Cryptographically random passwords.</summary>
public static class PasswordGenerator
{
    public static string Create(int length, bool upper, bool lower, bool digits, bool symbols, bool noAmbiguous)
    {
        var sets = new List<string>();
        if (upper) sets.Add(noAmbiguous ? "ABCDEFGHJKLMNPQRSTUVWXYZ" : "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
        if (lower) sets.Add(noAmbiguous ? "abcdefghijkmnopqrstuvwxyz" : "abcdefghijklmnopqrstuvwxyz");
        if (digits) sets.Add(noAmbiguous ? "23456789" : "0123456789");
        if (symbols) sets.Add("!#$%&*+-=?@^_~.:;");
        if (sets.Count == 0) sets.Add("abcdefghijkmnopqrstuvwxyz");
        var all = string.Concat(sets);
        var chars = new char[Math.Max(length, sets.Count)];
        // One character of every chosen class, the rest from all classes, then shuffled.
        for (var i = 0; i < chars.Length; i++)
        {
            var pool = i < sets.Count ? sets[i] : all;
            chars[i] = pool[RandomNumberGenerator.GetInt32(pool.Length)];
        }

        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }
}
