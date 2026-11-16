using Nexode.Core.Model;
using Nexode.Core.Security;
using Nexode.Core.Theming;
using Nexode.Storage;
using Xunit;

namespace Nexode.Core.Tests;

public class MiscTests
{
    [Theory]
    [InlineData(true, "#573CFA")]
    [InlineData(false, "#573CFA")]
    [InlineData(true, "#FFE600")]
    [InlineData(false, "#FFE600")]
    [InlineData(true, "#000080")]
    public void Palette_AccentTokensAreReadable(bool dark, string accent)
    {
        var p = Palette.Build(dark, accent);
        Assert.True(Rgb.Contrast(p["AccentText"], p["BgPanel"]) >= 4.4);
        Assert.True(Rgb.Contrast(p["OnAccent"], p["Accent"]) >= 4.4);
        Assert.True(Rgb.Contrast(p["TextPrimary"], p["BgContent"]) >= 7);
        Assert.True(Rgb.Contrast(p["TextSecondary"], p["BgPanel"]) >= 4.5);
        Assert.True(Rgb.Contrast(p["DangerText"], p["BgPanel"]) >= 4.4);
        Assert.True(Rgb.Contrast(p["WarningText"], p["BgPanel"]) >= 4.4);
    }

    [Fact]
    public void Oklch_RoundTrips()
    {
        foreach (var hex in new[] { "#573CFA", "#E8083E", "#02864A", "#FB8D1A", "#1C1A27", "#FFFFFF" })
        {
            Assert.Equal(hex, Oklch.FromRgb(Rgb.FromHex(hex)).ToRgb().ToHex());
        }
    }

    [Fact]
    public void FolderColors_SpreadAndAvoidSemanticHues()
    {
        var accent = Oklch.FromRgb(Rgb.FromHex("#573CFA")).H;
        var hues = new List<double>();
        for (var i = 0; i < 8; i++) hues.Add(FolderColors.Next(hues, accent));
        Assert.All(hues, h => Assert.False(FolderColors.IsExcluded(h)));
        var minGap = hues.SelectMany((a, i) => hues.Skip(i + 1).Select(b => FolderColors.Distance(a, b))).Min();
        Assert.True(minGap > 20, $"min gap {minGap}");
    }

    [Fact]
    public void SunTimes_Bremen()
    {
        var s = SunTimes.Calculate(new DateOnly(2026, 6, 21), 53.08, 8.80)!.Value;
        Assert.InRange(s.Sunrise.Hour + s.Sunrise.Minute / 60.0, 2.8, 3.3); // ~03:00 UTC
        Assert.InRange(s.Sunset.Hour + s.Sunset.Minute / 60.0, 19.9, 20.3); // ~20:05 UTC
    }

    [Fact]
    public void Store_RoundTripSecretsAndBackup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexode-test-" + Guid.NewGuid());
        var path = Path.Combine(dir, "test.db");
        var e = new Entry { Kind = EntryKind.Rdp, Name = "dc01", Host = "dc01", Tags = ["ad"], EnvironmentId = "production" };
        e.SetSetting(SettingKeys.RdpAdmin, "true");
        var protector = new Platform.FileKeyProtector(Path.Combine(dir, "keys"));
        using (var store = DataSourceStore.Open(path, "Test"))
        {
            var vault = Vault.CreateSystem(store.Id, protector);
            store.WriteVaultHeader(vault.Header);
            store.Save([e]);
            store.SetSecret(e.Id, "password", vault.EncryptSecret(e.Id, "password", "pw"));
            Assert.Equal("Test", store.Name);
            var backup = store.Backup(Path.Combine(dir, "backups"), 2);
            Assert.True(File.Exists(backup));
        }

        using (var store = DataSourceStore.Open(path))
        {
            var loaded = Assert.Single(store.LoadEntries());
            Assert.True(loaded.ContentEquals(e));
            var vault = Vault.Open(store.ReadVaultHeader()!, store.Id, protector);
            Assert.Equal("pw", vault.DecryptSecret(e.Id, "password", store.GetSecret(e.Id, "password")!));

            loaded.DeletedUtc = DateTime.UtcNow.AddDays(-40);
            store.Save([loaded]);
            Assert.Equal(1, store.PurgeDeleted(DateTime.UtcNow.AddDays(-30)));
            Assert.Empty(store.LoadEntries());
            Assert.Null(store.GetSecret(e.Id, "password"));
        }

        Directory.Delete(dir, true);
    }
}
