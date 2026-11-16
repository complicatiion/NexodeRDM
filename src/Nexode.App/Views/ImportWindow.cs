using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Nexode.App.Controls;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.App.ViewModels;
using Nexode.Core.Files;
using Nexode.Core.Model;

namespace Nexode.App.Views;

/// <summary>Import of a "connections" .nexode file: password, preview with status per entry, target and conflict policy.</summary>
public sealed class ImportWindow : Window
{
    private readonly NexodeFile? _file;
    private readonly IReadOnlyList<string> _notes = [];
    private readonly string _path;
    private readonly Guid? _selectedFolder;
    private readonly ContentControl _body = new();
    private readonly Button _importButton;
    private ConnectionsPayload? _payload;
    private IReadOnlyList<ImportItem> _plan = [];
    private readonly HashSet<Guid> _selected = [];
    private ComboBox? _target;
    private ComboBox? _policy;

    public ImportWindow() : this(new NexodeFile(), "", null)
    {
    }

    /// <summary>Import from another tool: the payload is ready (passwords already decrypted).</summary>
    public ImportWindow(ConnectionsPayload payload, string label, string format, IReadOnlyList<string> notes, Guid? selectedFolder)
        : this(null, label, selectedFolder, format)
    {
        _payload = payload;
        _notes = notes;
    }

    public ImportWindow(NexodeFile file, string path, Guid? selectedFolder) : this(file, path, selectedFolder, null)
    {
    }

    private ImportWindow(NexodeFile? file, string path, Guid? selectedFolder, string? format)
    {
        _file = file;
        _path = path;
        _selectedFolder = AppHost.Workspace.Tree.Get(selectedFolder)?.IsContainer == true ? selectedFolder : null;
        Title = Loc.T("Import.Title");
        Width = 720;
        Height = 620;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Nexode/Assets/nexode.ico")));
        this[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("B.BgContent");

        var header = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new IconLabel { Icon = "file-down", Text = Loc.T("Import.Title"), IconSize = 20, Classes = { } });
        header.Children.Add(new TextBlock { Text = Path.GetFileName(path), Classes = { "mono", "small", "muted" } });
        var meta = file is null ? format ?? "" : $"{file.Description ?? ""} · {Loc.F("Import.CreatedBy", file.AppVersion, file.Created.ToLocalTime().ToString("g"))}";
        header.Children.Add(new TextBlock { Text = meta.Trim(' ', '·'), Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap });

        var cancel = new Button { Content = new IconLabel { Icon = "x", Text = Loc.T("Cancel") }, IsCancel = true, MinWidth = 100 };
        cancel.Click += (_, _) => Close(false);
        _importButton = new Button { Content = new IconLabel { Icon = "file-down", Text = Loc.T("Import.Run") }, Classes = { "primary" }, IsDefault = true, MinWidth = 110, IsEnabled = false };
        _importButton.Click += async (_, _) => await RunAsync();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_importButton);

        var root = new DockPanel { Margin = new Thickness(22, 18, 22, 16) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(buttons);
        root.Children.Add(_body);
        Content = root;

        Opened += (_, _) =>
        {
            WindowChrome.Apply(this);
            if (file is not null && ExportBuilder.NeedsPassword(file)) ShowPassword(null);
            else Load(null);
        };
    }

    private void ShowPassword(string? error)
    {
        var box = new TextBox { PasswordChar = '•', PlaceholderText = Loc.T("Password"), Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        var open = new Button { Content = new IconLabel { Icon = "lock-open", Text = Loc.T("Import.Decrypt") }, Classes = { "primary" } };
        var status = new TextBlock { Text = error, Classes = { "danger" }, IsVisible = error is not null };
        async void Decrypt()
        {
            open.IsEnabled = false;
            status.Text = Loc.T("Import.Decrypting");
            status.Classes.Remove("danger");
            status.IsVisible = true;
            var pw = box.Text ?? "";
            var ok = await Task.Run(() =>
            {
                try
                {
                    _payload = ExportBuilder.ReadConnections(_file!.ReadPayload(pw), pw);
                    return true;
                }
                catch (CryptographicException)
                {
                    return false;
                }
            });
            if (ok) Load(pw);
            else ShowPassword(Loc.T("PasswordWrong"));
        }

        open.Click += (_, _) => Decrypt();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) Decrypt();
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new IconLabel { Icon = "lock", Text = Loc.T("Import.Encrypted") });
        panel.Children.Add(box);
        panel.Children.Add(open);
        panel.Children.Add(status);
        _body.Content = panel;
        box.Focus();
    }

    private void Load(string? password)
    {
        try
        {
            _payload ??= ExportBuilder.ReadConnections(_file!.ReadPayload(password), password);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or CryptographicException)
        {
            _body.Content = new TextBlock { Text = Loc.F("Import.Failed", ex.Message), Classes = { "danger" }, TextWrapping = TextWrapping.Wrap };
            return;
        }

        var ws = AppHost.Workspace;
        var existing = new Dictionary<Guid, Entry>();
        foreach (var e in _payload.Entries)
        {
            if (ws.Get(e.Id) is { } cur) existing[e.Id] = cur;
        }

        _plan = ImportPlanner.Plan(_payload, existing);
        foreach (var p in _plan.Where(p => p.Status != ImportStatus.Identical)) _selected.Add(p.Incoming.Id);

        var panel = new DockPanel();
        var summary = new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Margin = new Thickness(0, 0, 0, 12) };
        summary.Children.Add(Pill(Loc.F("Import.New", _plan.Count(p => p.Status == ImportStatus.New)), "success"));
        summary.Children.Add(Pill(Loc.F("Import.Changed", _plan.Count(p => p.Status == ImportStatus.Changed)), "warning"));
        summary.Children.Add(Pill(Loc.F("Import.Identical", _plan.Count(p => p.Status == ImportStatus.Identical)), ""));
        summary.Children.Add(Pill(Loc.F("Import.Secrets", _payload.Secrets.Count), "accent"));
        DockPanel.SetDock(summary, Dock.Top);
        panel.Children.Add(summary);

        var options = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*"), Margin = new Thickness(0, 0, 0, 12) };
        var targets = new List<string> { Loc.F("Import.TargetRoot", ws.Info?.Name) };
        if (_selectedFolder is { } f) targets.Add(Loc.F("Import.TargetFolder", ws.Tree.Get(f)?.Name));
        targets.Add(Loc.T("Import.TargetNewSource"));
        targets.Add(Loc.T("Import.TargetReplace"));
        _target = new ComboBox { ItemsSource = targets, SelectedIndex = _selectedFolder is null ? 0 : 1, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 14, 0) };
        _policy = new ComboBox
        {
            ItemsSource = new[] { Loc.T("Import.Skip"), Loc.T("Import.Overwrite"), Loc.T("Import.NewerWins"), Loc.T("Import.KeepBoth") },
            SelectedIndex = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var l1 = new TextBlock { Text = Loc.T("Import.Target"), Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var l2 = new TextBlock { Text = Loc.T("Import.Conflicts"), Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(_target, 1);
        Grid.SetColumn(l2, 2);
        Grid.SetColumn(_policy, 3);
        options.Children.Add(l1);
        options.Children.Add(_target);
        options.Children.Add(l2);
        options.Children.Add(_policy);
        DockPanel.SetDock(options, Dock.Top);
        panel.Children.Add(options);

        var hint = new TextBlock { Text = Loc.T("Import.ReviewHint"), Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(hint, Dock.Bottom);
        panel.Children.Add(hint);
        if (_notes.Count > 0)
        {
            var notes = new Border
            {
                Classes = { "notice", "warning" },
                Margin = new Thickness(0, 10, 0, 0),
                Child = new TextBlock { Text = string.Join("\n", _notes.Take(12)), TextWrapping = TextWrapping.Wrap, FontSize = 12 }
            };
            DockPanel.SetDock(notes, Dock.Bottom);
            panel.Children.Add(notes);
        }

        var list = new StackPanel { Spacing = 2 };
        var byId = _payload.Entries.ToDictionary(e => e.Id);
        int Depth(Entry e)
        {
            var d = 0;
            var p = e.ParentId;
            while (p is { } id && byId.TryGetValue(id, out var pe) && d < 30)
            {
                d++;
                p = pe.ParentId;
            }

            return d;
        }

        foreach (var item in _plan.Take(2000))
        {
            var e = item.Incoming;
            var check = new CheckBox { IsChecked = _selected.Contains(e.Id), IsEnabled = item.Status != ImportStatus.Identical, Margin = new Thickness(Depth(e) * 18, 0, 0, 0) };
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true) _selected.Add(e.Id);
                else _selected.Remove(e.Id);
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new LineIcon { Icon = EntryKinds.Icon(e.Kind), Size = 15 });
            row.Children.Add(new TextBlock { Text = e.Name, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(Pill(Loc.T("Import.Status." + item.Status), item.Status switch { ImportStatus.New => "success", ImportStatus.Changed => "warning", _ => "" }));
            if (e.Kind == EntryKind.Command) row.Children.Add(Pill(Loc.T("NeedsReview"), "warning"));
            check.Content = row;
            list.Children.Add(check);
        }

        panel.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(10), Child = new ScrollViewer { Content = list } });
        _body.Content = panel;
        _importButton.IsEnabled = _plan.Count > 0;
    }

    private static Border Pill(string text, string cls)
    {
        var b = new Border { Classes = { "pill" }, Child = new TextBlock { Text = text } };
        if (cls.Length > 0) b.Classes.Add(cls);
        return b;
    }

    private async Task RunAsync()
    {
        if (_payload is null || _target is null || _policy is null) return;
        var ws = AppHost.Workspace;
        var owner = Owner as MainWindow;
        var vm = owner?.DataContext as MainWindowViewModel;
        var targetIndex = _target.SelectedIndex;
        var hasFolderOption = _selectedFolder is not null;
        var newSource = targetIndex == (hasFolderOption ? 2 : 1);
        var replace = targetIndex == (hasFolderOption ? 3 : 2);
        Guid? parent = hasFolderOption && targetIndex == 1 ? _selectedFolder : null;

        if (replace && !await Dialog.ConfirmAsync(this, Loc.T("Import.TargetReplace"), Loc.F("Import.ReplaceConfirm", ws.Info?.Name), Loc.T("Import.Run"), danger: true))
        {
            return;
        }

        if (newSource)
        {
            var name = string.IsNullOrWhiteSpace(_payload.SourceName) ? Path.GetFileNameWithoutExtension(_path) : _payload.SourceName;
            if (!await DataSources.OpenAsync(this, DataSources.CreateInfo(name), isNew: true)) return;
            ws = AppHost.Workspace;
        }
        else
        {
            try { ws.BackupNow(); } catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException) { }
            if (replace) ws.ReplaceAll();
        }

        if (_payload.Secrets.Count > 0 && ws.IsLocked && vm is not null && !await vm.Actions.EnsureUnlockedAsync())
        {
            return;
        }

        var existing = new Dictionary<Guid, Entry>();
        foreach (var e in _payload.Entries)
        {
            if (ws.Get(e.Id) is { IsDeleted: false } cur) existing[e.Id] = cur;
        }

        var plan = ImportPlanner.Plan(_payload, existing);
        var keys = new Dictionary<Guid, string?>();
        string NextKey(Guid? p)
        {
            var last = keys.TryGetValue(p ?? Guid.Empty, out var k) ? k : ws.Tree.Children(p).Select(c => c.SortKey).DefaultIfEmpty(null).Max(StringComparer.Ordinal);
            var next = Core.Tree.SortKeys.Between(last, null);
            keys[p ?? Guid.Empty] = next;
            return next;
        }

        var result = ImportPlanner.Apply(_payload, plan, _selected, parent, (ConflictPolicy)_policy.SelectedIndex, ws.Clock.Next, NextKey);
        var secrets = ws.IsLocked ? [] : result.Secrets;
        ws.ImportEntries(result.Upserts, secrets);
        vm?.ShowToast(Loc.F("Toast.Imported", result.Added, result.Updated, result.Skipped) + (result.NeedsReview > 0 ? " " + Loc.F("Toast.ImportReview", result.NeedsReview) : ""));
        Close(true);
    }
}
