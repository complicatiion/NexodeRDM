using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.Core.Model;
using Nexode.Core.Tree;

namespace Nexode.App.ViewModels;

public enum DropZone
{
    None,
    Before,
    Into,
    After
}

/// <summary>One visible line of the flattened, virtualized connection tree.</summary>
public sealed partial class TreeRow : ObservableObject
{
    public TreeRow(Entry entry, Effective effective, int depth, bool hasChildren, bool expanded, int childCount, bool isMatch, string? pathHint)
    {
        Entry = entry;
        Effective = effective;
        Depth = depth;
        HasChildren = hasChildren;
        IsExpanded = expanded;
        IsMatch = isMatch;
        PathHint = pathHint;
        _status = LiveStatus.Get(entry.Id)?.State ?? Nexode.Protocols.ReachState.Unknown;

        var theme = AppHost.Theme;
        Icon = effective.Icon;
        IconBrush = entry.Kind == EntryKind.Folder && effective.ColorHue is { } hue
            ? theme.FolderBrush(hue)
            : entry.ColorHue is { } own ? theme.FolderBrush(own) : null;
        StripeBrush = entry.Kind != EntryKind.Folder && entry.ColorHue is null && effective.ColorHue is { } inherited ? theme.FolderBrush(inherited) : null;

        IsFeatureOff = !Features.KindEnabled(entry.Kind);
        CountText = entry.IsContainer && childCount > 0 ? childCount.ToString(System.Globalization.CultureInfo.CurrentCulture) : null;
        Detail = AppHost.Settings.ShowHostInTree && !entry.IsContainer
            ? entry.Kind == EntryKind.Web ? effective.Url : effective.Host
            : null;
        if (!string.IsNullOrEmpty(pathHint)) Detail = pathHint;

        // Badge only where the environment is set; children show it in their tab stripe.
        if (Envs.Get(entry.EnvironmentId) is { } env)
        {
            EnvText = Envs.Short(env);
            EnvBrush = Envs.Brush(env.Id);
            EnvBackground = Envs.Background(env.Id);
        }
    }

    public Entry Entry { get; }
    public Effective Effective { get; }
    public Guid Id => Entry.Id;
    public int Depth { get; }
    public Thickness Indent => new(Depth * 16, 0, 0, 0);
    public bool HasChildren { get; }
    public bool IsExpanded { get; }
    public string ExpanderIcon => IsExpanded ? "chevron-down" : "chevron-right";
    public string Name => string.IsNullOrEmpty(Entry.Name) ? Loc.T("Unnamed") : Entry.Name;
    public string Icon { get; }
    public IBrush? IconBrush { get; }
    public IBrush? StripeBrush { get; }
    public bool HasStripe => StripeBrush is not null;
    public string? Detail { get; }
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public string? CountText { get; }
    public bool HasCount => CountText is not null;
    public bool IsFavorite => Entry.IsFavorite;
    public bool IsMatch { get; }
    public string? PathHint { get; }
    public string? EnvText { get; }
    public IBrush? EnvBrush { get; }
    public IBrush? EnvBackground { get; }
    public bool HasEnv => EnvText is not null;
    public bool NeedsReview => Entry.NeedsReview;
    public bool IsCompact => AppHost.Settings.Compact;

    /// <summary>Live status (dot next to the name); Unknown = not checked.</summary>
    [ObservableProperty]
    private Nexode.Protocols.ReachState _status;

    public bool HasStatus => Status is Nexode.Protocols.ReachState.Online or Nexode.Protocols.ReachState.Offline && Features.On(Features.TreeStatus);

    /// <summary>Kind belongs to a switched-off feature: shown dimmed, opening explains why.</summary>
    public bool IsFeatureOff { get; }

    public bool IsOnline => Status == Nexode.Protocols.ReachState.Online;
    public bool IsOffline => Status == Nexode.Protocols.ReachState.Offline;

    partial void OnStatusChanged(Nexode.Protocols.ReachState value)
    {
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(StatusTip));
    }

    /// <summary>Set for rows that appeared by expanding a folder (fade-in animation).</summary>
    public bool Appear { get; set; }

    /// <summary>Row is about to disappear (folder collapses).</summary>
    [ObservableProperty]
    private bool _leaving;

    public string StatusTip => LiveStatus.Get(Entry.Id) is { } s
        ? Loc.F(s.State == Nexode.Protocols.ReachState.Online ? "Status.OnlineTip" : "Status.OfflineTip", s.Millis?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "–", s.CheckedUtc.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture))
        : "";

    public string Tooltip
    {
        get
        {
            var parts = new List<string> { Name, Loc.Kind(Entry.Kind) };
            if (Effective.Host is { } h) parts.Add(h + (Effective.Port is { } p && p != EntryKinds.DefaultPort(Entry.Kind) ? ":" + p : ""));
            if (Effective.Url is { } u) parts.Add(u);
            if (Effective.UserWithDomain is { } user) parts.Add(user);
            if (IsFeatureOff) parts.Add(Loc.T("Feature.OffTip"));
            return string.Join("\n", parts);
        }
    }

    [ObservableProperty]
    private DropZone _dropZone;

    public bool DropBefore => DropZone == DropZone.Before;
    public bool DropAfter => DropZone == DropZone.After;
    public bool DropInto => DropZone == DropZone.Into;

    partial void OnDropZoneChanged(DropZone value)
    {
        OnPropertyChanged(nameof(DropBefore));
        OnPropertyChanged(nameof(DropAfter));
        OnPropertyChanged(nameof(DropInto));
    }
}
