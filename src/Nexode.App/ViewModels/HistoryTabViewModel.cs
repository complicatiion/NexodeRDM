using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.App.Views;
using Nexode.Core.Model;

namespace Nexode.App.ViewModels;

public sealed record HistoryRow(HistoryRecord Record, string Icon, string Name, string Target, string When, string Day, string ResultText, string ResultClass, string DurationText)
{
    public bool HasError => !string.IsNullOrEmpty(Record.Error);
    public string? Error => Record.Error;
}

/// <summary>Connection history of this device for the active data source (optionally for one entry).</summary>
public sealed partial class HistoryTabViewModel : TabViewModel
{
    public HistoryTabViewModel(Guid? entryId = null)
    {
        EntryId = entryId;
        Icon = "history";
        ConnectionHistory.Changed += OnChanged;
        Refresh();
    }

    public Guid? EntryId { get; }
    public override string Key => EntryId is { } id ? "history:" + id : "history";
    public override TabViewModel Recreate() => new HistoryTabViewModel(EntryId);
    public ObservableCollection<HistoryRow> Rows { get; } = [];
    public bool IsEmpty => Rows.Count == 0;
    public bool IsOff => !AppHost.Settings.ConnectionHistory;
    public string Subtitle { get; private set; } = "";

    [ObservableProperty]
    private string _filter = "";

    partial void OnFilterChanged(string value) => Refresh();

    private void OnChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);

    public override void Refresh()
    {
        var entry = EntryId is { } id ? AppHost.Workspace.Tree.Get(id) : null;
        Title = entry is null ? Loc.T("Tab.History") : Loc.F("History.TitleFor", entry.Name);
        var records = ConnectionHistory.All().AsEnumerable().Reverse();
        if (EntryId is { } eid) records = records.Where(r => r.EntryId == eid);
        if (Filter.Trim() is { Length: > 0 } f)
        {
            records = records.Where(r => r.Name.Contains(f, StringComparison.CurrentCultureIgnoreCase) || r.Target.Contains(f, StringComparison.CurrentCultureIgnoreCase));
        }

        Rows.Clear();
        foreach (var r in records.Take(500))
        {
            var local = r.StartUtc.ToLocalTime();
            var (text, cls) = r.Result switch
            {
                HistoryResult.Failed => (Loc.T("History.Failed"), "danger"),
                HistoryResult.External => (Loc.T("History.External"), "info"),
                _ when r.EndUtc is null => (Loc.T("History.Open"), "success"),
                _ => (Loc.T("History.Ended"), "")
            };
            var duration = r.Duration is { } d ? d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{d.Minutes}:{d.Seconds:00}" : "";
            Rows.Add(new HistoryRow(r, EntryKinds.Icon(r.Kind), r.Name, r.Target, local.ToString("t", CultureInfo.CurrentCulture),
                local.Date == DateTime.Today ? Loc.T("History.Today") : local.ToString("D", CultureInfo.CurrentCulture), text, cls, duration));
        }

        Subtitle = Loc.F("History.Count", Rows.Count);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsOff));
        OnPropertyChanged(nameof(Subtitle));
    }

    [RelayCommand]
    private void Open(HistoryRow row)
    {
        if (AppHost.Workspace.Tree.Get(row.Record.EntryId) is { } e) Owner?.Actions.Open(e);
        else Owner?.ShowToast(Loc.T("History.Gone"), true);
    }

    [RelayCommand]
    private async Task Clear()
    {
        if (Owner?.Window is not { } w) return;
        if (!await Dialog.ConfirmAsync(w, Loc.T("History.Clear"), Loc.T("History.ClearText"), Loc.T("History.Clear"), danger: true)) return;
        ConnectionHistory.Clear();
    }

    public override void OnClosed() => ConnectionHistory.Changed -= OnChanged;
}
