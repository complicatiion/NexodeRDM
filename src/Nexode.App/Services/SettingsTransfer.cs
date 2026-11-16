using Nexode.Core.Theming;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Nexode.App.Localization;
using Nexode.App.ViewModels;
using Nexode.App.Views;
using Nexode.Core.Files;

namespace Nexode.App.Services;

/// <summary>Export/import of settings and theme as .nexode files.</summary>
public static class SettingsTransfer
{
    public static async Task ExportAsync(Window window, bool themeOnly)
    {
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T(themeOnly ? "Settings.ExportTheme" : "Settings.ExportSettings"),
            SuggestedFileName = themeOnly ? "nexode-theme.nexode" : "nexode-settings.nexode",
            DefaultExtension = "nexode",
            FileTypeChoices = [new FilePickerFileType("Nexode") { Patterns = ["*.nexode"] }]
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;

        var s = AppHost.Settings;
        var node = themeOnly
            ? JsonSerializer.SerializeToNode(new ThemeExport { Theme = s.Theme, DarkVariantAmoled = s.DarkVariantAmoled, DarkVariant = s.DarkVariant, LightVariant = s.LightVariant, UseSystemAccent = s.UseSystemAccent, Accent = s.Accent, Compact = s.Compact }, AppJson.Default.ThemeExport)!
            : JsonSerializer.SerializeToNode(s, AppJson.Default.AppSettings)!;
        if (!themeOnly)
        {
            // Data source paths and device id stay on this machine.
            node.AsObject().Remove("dataSources");
            node.AsObject().Remove("activeDataSource");
            node.AsObject().Remove("deviceId");
            node.AsObject().Remove("window");
        }

        NexodeFile.Create(themeOnly ? NexodeKinds.Theme : NexodeKinds.Settings, AppHost.Version, node, null).Save(path);
        AppHost.Windows.Toast(Loc.F("Toast.Exported", Path.GetFileName(path)));
    }

    public static async Task ImportAsync(Window window, NexodeFile file, MainWindowViewModel owner)
    {
        var title = Loc.T(file.Kind == NexodeKinds.Theme ? "Import.ThemeTitle" : "Import.SettingsTitle");
        if (file.Kind is not (NexodeKinds.Settings or NexodeKinds.Theme))
        {
            await Dialog.InfoAsync(window, Loc.T("Import.Title"), Loc.F("Import.UnknownKind", file.Kind), "circle-alert");
            return;
        }

        if (!await Dialog.ConfirmAsync(window, title, Loc.T(file.Kind == NexodeKinds.Theme ? "Import.ThemeConfirm" : "Import.SettingsConfirm"), Loc.T("Import.Apply"), icon: "file-down"))
        {
            return;
        }

        var payload = file.ReadPayload(null);
        ThemeNames.Upgrade(payload);
        var s = AppHost.Settings;
        if (file.Kind == NexodeKinds.Theme)
        {
            var theme = payload.Deserialize(AppJson.Default.ThemeExport);
            if (theme is null) return;
            s.Theme = theme.Theme;
            s.DarkVariantAmoled = theme.DarkVariantAmoled;
            s.DarkVariant = theme.DarkVariant == PaletteVariant.Default && theme.DarkVariantAmoled ? PaletteVariant.Amoled : theme.DarkVariant;
            s.LightVariant = theme.LightVariant;
            s.UseSystemAccent = theme.UseSystemAccent;
            s.Accent = theme.Accent;
            s.Compact = theme.Compact;
        }
        else if (payload.Deserialize(AppJson.Default.AppSettings) is { } imported)
        {
            s.ApplyPreferences(imported);
            Localization.Loc.Apply(s.Language ?? Localization.Loc.DefaultLanguage);
        }

        s.Save();
        AppHost.Theme.Apply();
        owner.Tree.Refresh();
        owner.ShowToast(Loc.T("Toast.SettingsImported"));
    }
}
