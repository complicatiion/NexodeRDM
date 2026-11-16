using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.App.Views;
using Nexode.Core.Model;

namespace Nexode.App.ViewModels;

public sealed record TrashItem(Entry Entry, string Icon, string Name, string KindText, string DeletedText);

/// <summary>Deleted entries: restore or empty the trash.</summary>
public sealed partial class TrashTabViewModel : TabViewModel
{
    public TrashTabViewModel()
    {
        Icon = "trash-2";
        Refresh();
    }

    public override string Key => "trash";
    public override TabViewModel Recreate() => new TrashTabViewModel();

    public ObservableCollection<TrashItem> Items { get; } = [];
    public bool IsEmpty => Items.Count == 0;
    public string RetentionText => Loc.F("Trash.Retention", AppHost.Settings.TrashRetentionDays);

    public override void Refresh()
    {
        Title = Loc.T("Tab.Trash");
        Items.Clear();
        var ws = AppHost.Workspace;
        // Show the top of each deleted batch (children follow their parent on restore).
        foreach (var e in ws.Deleted.Where(e => ws.Get(e.ParentId) is not { IsDeleted: true } p || p.DeletedUtc != e.DeletedUtc))
        {
            Items.Add(new TrashItem(e, EntryKinds.Icon(e.Kind), string.IsNullOrEmpty(e.Name) ? Loc.T("Unnamed") : e.Name, Loc.Kind(e.Kind),
                e.DeletedUtc!.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(RetentionText));
    }

    [RelayCommand]
    private void Restore(TrashItem item)
    {
        AppHost.Workspace.Restore([item.Entry.Id]);
        Owner?.ShowToast(Loc.F("Toast.Restored", item.Name));
    }

    [RelayCommand]
    private async Task Empty()
    {
        if (Owner?.Window is not { } window || IsEmpty) return;
        if (!await Dialog.ConfirmAsync(window, Loc.T("Trash.Empty"), Loc.T("Trash.EmptyConfirm"), Loc.T("Trash.Empty"), danger: true)) return;
        var n = AppHost.Workspace.EmptyTrash();
        Owner.ShowToast(Loc.F("Toast.Purged", n));
    }
}
