using Avalonia.Media;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.Core.Model;

namespace Nexode.App.ViewModels;

/// <summary>Connection profile as shown in the side panel and on the start page.</summary>
public sealed class ProfileItem(ConnectionProfile profile)
{
    public ConnectionProfile Profile { get; } = profile;
    public string Id => Profile.Id;
    public string Name => string.IsNullOrWhiteSpace(Profile.Name) ? Loc.T("Unnamed") : Profile.Name;
    public string Icon => string.IsNullOrEmpty(Profile.Icon) ? "layers" : Profile.Icon;
    public IBrush? IconBrush => Profile.ColorHue is { } h ? AppHost.Theme.FolderBrush(h) : null;
    public IBrush IconBackground => AppHost.Theme.Brush("AccentSoft");
    public IReadOnlyList<Entry> Entries => AppHost.Workspace.ProfileEntries(Profile);

    public string Summary
    {
        get
        {
            var n = Entries.Count;
            var count = n == 1 ? Loc.T("Profile.OneConnection") : Loc.F("Profile.Connections", n);
            return Profile.Layout switch
            {
                ProfileLayout.Split => count + " · " + Loc.T("Profile.LayoutSplit"),
                ProfileLayout.NewWindow => count + " · " + Loc.T("Profile.LayoutWindow"),
                _ => count
            };
        }
    }

    public string Tooltip => Entries.Count == 0 ? Name : Name + "\n" + string.Join("\n", Entries.Select(e => "• " + e.Name));
}
