using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Nexode.App.Services;
using Nexode.App.ViewModels;
using Nexode.Core.Model;

namespace Nexode.App.Views;

public partial class EntryEditorWindow : Window
{
    private readonly Entry _entry;

    public EntryEditorWindow() : this(new Entry(), true)
    {
    }

    /// <summary>SSH key tools from the editor: a generated or chosen key fills the key file field.</summary>
    private async void KeyToolsClick(object? sender, RoutedEventArgs e)
    {
        if (Owner?.DataContext is not MainWindowViewModel vm || DataContext is not EntryEditorViewModel editor) return;
        var existing = AppHost.Workspace.Tree.Get(_entry.Id) is { } saved && saved.Kind is EntryKind.Ssh or EntryKind.Sftp or EntryKind.Scp ? saved : null;
        if (await SshKeyDialog.ShowAsync(vm, existing, this) is { } path) editor.KeyFile = path;
    }

    public EntryEditorWindow(Entry entry, bool isNew)
    {
        _entry = entry;
        InitializeComponent();
        DataContext = new EntryEditorViewModel(entry, isNew);
        // Serial port picker: filled when the flyout opens (ports come and go with USB adapters).
        {
            var list = SerialPortList;
            list.AttachedToVisualTree += (_, _) =>
            {
                list.Children.Clear();
                var ports = Nexode.Protocols.Terminal.SerialConnection.ListPorts();
                foreach (var port in ports)
                {
                    var b = new Button { Classes = { "ghost" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Content = new Controls.IconLabel { Icon = "cable", Text = port } };
                    b.Click += (_, _) => Vm.SerialPort = port;
                    list.Children.Add(b);
                }

                if (ports.Count == 0) list.Children.Add(new TextBlock { Text = Localization.Loc.T("Serial.NoPorts"), Classes = { "muted" }, Margin = new Avalonia.Thickness(8, 6) });
            };
        }

        Opened += (_, _) =>
        {
            WindowChrome.Apply(this);
            Dispatcher.UIThread.Post(() =>
            {
                NameBox.Focus();
                NameBox.SelectAll();
            }, DispatcherPriority.Background);
        };
    }

    private EntryEditorViewModel Vm => (EntryEditorViewModel)DataContext!;

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private async void SaveClick(object? sender, RoutedEventArgs e)
    {
        var error = Vm.Apply();
        Vm.Error = error;
        if (error is not null) return;

        var password = Vm.PasswordToSave;
        var ws = AppHost.Workspace;
        if (!string.IsNullOrEmpty(password) && ws.IsLocked)
        {
            var owner = Owner as MainWindow;
            if (owner?.DataContext is not MainWindowViewModel vm || !await vm.Actions.EnsureUnlockedAsync())
            {
                Vm.Error = Localization.Loc.T("Editor.ErrorLocked");
                return;
            }
        }

        ws.Save(_entry, password);
        Close(true);
    }

    private void ClearPasswordClick(object? sender, RoutedEventArgs e) => Vm.ClearPassword();

    private void NoColorClick(object? sender, RoutedEventArgs e)
    {
        Vm.RandomColor = false;
        Vm.ColorHue = null;
    }

    private void RandomColorClick(object? sender, RoutedEventArgs e) => Vm.RandomColor = true;

    private async void GenerateClick(object? sender, RoutedEventArgs e)
    {
        if (await Dialog.GeneratePasswordAsync(this) is { } pw)
        {
            Vm.Password = pw;
            PasswordBox.RevealPassword = true;
            RevealToggle.IsChecked = true;
        }
    }

    /// <summary>Showing a stored password may require the master password again (setting).</summary>
    private async void RevealClick(object? sender, RoutedEventArgs e)
    {
        if (RevealToggle.IsChecked != true)
        {
            PasswordBox.RevealPassword = false;
            return;
        }

        RevealToggle.IsChecked = false;
        if (Vm.HadPassword && !Vm.PasswordChanged && Owner?.DataContext is MainWindowViewModel vm)
        {
            if (!await vm.Actions.ReauthenticateAsync()) return;
            if (AppHost.Workspace.GetSecret(Vm.EntryId, Workspace.PasswordField) is { } stored)
            {
                Vm.Password = stored;
                Vm.PasswordChanged = false;
            }
        }

        PasswordBox.RevealPassword = true;
        RevealToggle.IsChecked = true;
    }

    private async void PickIconClick(object? sender, RoutedEventArgs e)
    {
        var result = await new IconPickerWindow(Vm.Icon).ShowDialog<string?>(this);
        if (result is not null) Vm.Icon = result.Length == 0 ? null : result;
    }

    private void ResetIconClick(object? sender, RoutedEventArgs e) => Vm.Icon = null;

    private void SwatchClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is HueSwatch s) Vm.ColorHue = s.Hue;
    }
}
