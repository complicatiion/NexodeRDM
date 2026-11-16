using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.App.Views;
using Nexode.Core.Model;

namespace Nexode.App.ViewModels;

/// <summary>One template on the Profiles page.</summary>
public sealed record TemplateItem(EntryTemplate Template, bool IsBuiltIn)
{
    public string Name => Template.Name;
    public string Icon => Template.Entry.Icon ?? EntryKinds.Icon(Template.Entry.Kind);
    public string Summary => Template.Description is { Length: > 0 } d ? $"{Loc.Kind(Template.Entry.Kind)} · {d}" : Loc.Kind(Template.Entry.Kind) + OptionsText(Template.Entry);

    private static string OptionsText(Entry e)
    {
        var parts = new List<string>();
        if (e.Port is { } p) parts.Add(Loc.F("Template.Port", p));
        if (e.Setting(SettingKeys.RdpGateway) is { Length: > 0 } gw) parts.Add(Loc.F("Template.Gateway", gw));
        if (e.Setting(SettingKeys.RdpResolution) is { Length: > 0 } res) parts.Add(res);
        if (e.Setting(SettingKeys.RdpDrives) == "1") parts.Add(Loc.T("Template.Drives"));
        if (e.CredentialMode != CredentialMode.Inherit) parts.Add(Loc.T("Template.OwnCredentials"));
        return parts.Count == 0 ? "" : " · " + string.Join(", ", parts);
    }
}

/// <summary>v0.0.7 settings: entry templates.</summary>
public sealed partial class SettingsTabViewModel
{
    public ObservableCollection<TemplateItem> TemplateList { get; } = [];

    private void LoadTemplates()
    {
        TemplateList.Clear();
        foreach (var t in Templates.BuiltIn()) TemplateList.Add(new TemplateItem(t, true));
        foreach (var t in AppHost.Workspace.Templates) TemplateList.Add(new TemplateItem(t, false));
    }

    [RelayCommand]
    private async Task RenameTemplate(TemplateItem item)
    {
        if (item.IsBuiltIn || Owner?.Window is not { } w) return;
        var name = await Dialog.InputAsync(w, Loc.T("Template.Rename"), Loc.T("Template.Name"), item.Name, "layout-template");
        if (string.IsNullOrWhiteSpace(name)) return;
        var list = AppHost.Workspace.Templates.ToList();
        if (list.FirstOrDefault(t => t.Id == item.Template.Id) is { } t) t.Name = name.Trim();
        AppHost.Workspace.SaveTemplates(list);
        LoadTemplates();
    }

    [RelayCommand]
    private async Task DeleteTemplate(TemplateItem item)
    {
        if (item.IsBuiltIn || Owner?.Window is not { } w) return;
        if (!await Dialog.ConfirmAsync(w, Loc.T("Template.Delete"), Loc.F("Template.DeleteText", item.Name), Loc.T("Template.Delete"), danger: true, icon: "trash-2")) return;
        AppHost.Workspace.SaveTemplates(AppHost.Workspace.Templates.Where(t => t.Id != item.Template.Id));
        LoadTemplates();
    }
}
