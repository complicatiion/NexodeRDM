using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.Core.Model;

namespace Nexode.App.ViewModels;

public sealed record DiskRow(string Name, string Text, double Fraction, bool IsHigh);

/// <summary>System information card of the details view (feature "sysinfo").</summary>
public sealed partial class EntryTabViewModel
{
    private CancellationTokenSource? _sysCts;
    private bool _sysHooked;

    [ObservableProperty] private bool _isFetchingSysInfo;
    [ObservableProperty] private string _sysInfoError = "";

    public bool CanSysInfo { get; private set; }
    public bool HasSysInfo => SysInfoRows.Count > 0;
    public bool HasSysInfoError => SysInfoError.Length > 0;
    public bool HasSysInfoHint => CanSysInfo && !HasSysInfo && !IsFetchingSysInfo && !HasSysInfoError;
    public string SysInfoStatus { get; private set; } = "";
    public IReadOnlyList<InfoRow> SysInfoRows { get; private set; } = [];
    public IReadOnlyList<DiskRow> SysInfoDisks { get; private set; } = [];
    public bool HasSysInfoDisks => SysInfoDisks.Count > 0;

    partial void OnSysInfoErrorChanged(string value) => NotifySysInfo();
    partial void OnIsFetchingSysInfoChanged(bool value) => NotifySysInfo();

    private void NotifySysInfo()
    {
        OnPropertyChanged(nameof(HasSysInfoError));
        OnPropertyChanged(nameof(HasSysInfoHint));
    }

    private void RefreshSysInfo(Entry entry, Core.Tree.Effective eff)
    {
        if (!_sysHooked)
        {
            _sysHooked = true;
            HostInfoService.Changed += OnHostInfoChanged;
        }

        CanSysInfo = Features.On(Features.SysInfo) && HostInfoService.MethodFor(entry, eff) is not null;
        var info = CanSysInfo ? HostInfoService.Get(entry.Id) : null;
        var rows = new List<InfoRow>();
        var disks = new List<DiskRow>();
        if (info is not null)
        {
            void Add(string key, string? value, string icon)
            {
                if (!string.IsNullOrWhiteSpace(value)) rows.Add(new(Loc.T("SysInfo." + key), value, icon));
            }

            Add("HostName", info.HostName, "server");
            Add("Os", info.Os, "app-window");
            Add("Kernel", info.Kernel != info.Os ? info.Kernel : null, "cpu");
            Add("Model", info.Model, "hard-drive");
            Add("Cpu", info.Cpu is null ? null : info.Cores is { } c ? $"{info.Cpu} · {Loc.F("SysInfo.CoresN", c)}" : info.Cpu, "cpu");
            Add("Architecture", info.Architecture, "binary");
            Add("Memory", info.MemoryTotal is { } t
                ? info.MemoryUsed is { } u ? Loc.F("SysInfo.UsedOf", Bytes(u), Bytes(t), Percent(u, t)) : Bytes(t)
                : null, "memory-stick");
            Add("Uptime", info.UptimeSeconds is { } up ? Duration(up) : null, "timer");
            Add("Load", info.Load, "activity");
            Add("Domain", info.Domain, "building-2");
            Add("Addresses", info.Addresses.Count > 0 ? string.Join(", ", info.Addresses) : null, "network");
            foreach (var d in info.Disks.Take(12))
            {
                var f = d.Size > 0 ? (double)d.Used / d.Size : 0;
                disks.Add(new DiskRow(d.Name, Loc.F("SysInfo.UsedOf", Bytes(d.Used), Bytes(d.Size), Percent(d.Used, d.Size)), f, f >= 0.9));
            }

            var via = info.Source switch { "winrm" => "PowerShell (WinRM)", "ssh-powershell" => "SSH · PowerShell", _ => "SSH" };
            SysInfoStatus = Loc.F("SysInfo.Fetched", info.FetchedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), via);
        }
        else
        {
            SysInfoStatus = "";
        }

        SysInfoRows = rows;
        SysInfoDisks = disks;
    }

    private void OnHostInfoChanged(Guid id)
    {
        if (id == EntryId) Dispatcher.UIThread.Post(Refresh);
    }

    [RelayCommand]
    private async Task FetchSysInfo()
    {
        if (Entry is not { } entry || Owner?.Window is not { } window || IsFetchingSysInfo) return;
        _sysCts?.Dispose();
        _sysCts = new CancellationTokenSource();
        SysInfoError = "";
        IsFetchingSysInfo = true;
        try
        {
            await HostInfoService.FetchAsync(entry, window, Owner.Actions, _sysCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SysInfoError = Loc.F("SysInfo.Failed", ex.Message);
        }
        finally
        {
            IsFetchingSysInfo = false;
        }
    }

    [RelayCommand]
    private void CancelSysInfo() => _sysCts?.Cancel();

    [RelayCommand]
    private void ClearSysInfo()
    {
        SysInfoError = "";
        HostInfoService.Remove(EntryId);
    }

    private void CloseSysInfo()
    {
        _sysCts?.Cancel();
        if (_sysHooked) HostInfoService.Changed -= OnHostInfoChanged;
        _sysHooked = false;
    }

    private static string Percent(long part, long total) => total > 0 ? ((double)part / total).ToString("P0", CultureInfo.CurrentCulture) : "";

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        return v.ToString(i == 0 || v >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + units[i];
    }

    private static string Duration(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalDays >= 1) return Loc.F("SysInfo.DaysHours", (int)t.TotalDays, t.Hours);
        if (t.TotalHours >= 1) return Loc.F("SysInfo.HoursMinutes", t.Hours, t.Minutes);
        return Loc.F("SysInfo.Minutes", Math.Max(1, t.Minutes));
    }
}
