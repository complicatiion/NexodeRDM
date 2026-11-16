using Nexode.Core.Files;
using Nexode.Core.Theming;
using Xunit;

namespace Nexode.Core.Tests;

public class SyncTests
{
    private static readonly DateTime T = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private static SyncFile F(string path) => new(path, false, 1, T);

    private static IReadOnlyList<SyncDiff> Sample() => FolderSync.Compare(
        [
            new("same.txt", false, 10, T), new("newer-local.txt", false, 5, T.AddMinutes(5)), new("only-local.txt", false, 1, T),
            new("dir", true, 0, T), new("dir/a.txt", false, 3, T), new("rounded.txt", false, 7, T.AddSeconds(1)), new("uploaded.txt", false, 4, T)
        ],
        [
            new("same.txt", false, 10, T), new("newer-local.txt", false, 6, T), new("only-remote.txt", false, 2, T),
            new("old", true, 0, T), new("old/x.txt", false, 1, T), new("rounded.txt", false, 7, T), new("uploaded.txt", false, 4, T.AddMinutes(3))
        ]);

    [Fact]
    public void ComparesWithTolerance()
    {
        var d = Sample().ToDictionary(x => x.RelativePath, x => x.State);
        Assert.Equal(SyncState.Same, d["same.txt"]);
        Assert.Equal(SyncState.Same, d["rounded.txt"]);
        Assert.Equal(SyncState.LocalNewer, d["newer-local.txt"]);
        Assert.Equal(SyncState.LocalOnly, d["only-local.txt"]);
        Assert.Equal(SyncState.RemoteOnly, d["only-remote.txt"]);
        Assert.Equal(SyncState.LocalOnly, d["dir/a.txt"]);
        Assert.Equal(SyncState.RemoteNewer, d["uploaded.txt"]);
    }

    [Fact]
    public void UploadMirrorKeepsPreviousCopyAndDeletesOnlyTopFolder()
    {
        var plan = FolderSync.Plan(Sample(), SyncDirection.Upload, deleteExtra: true);
        var kinds = plan.ToDictionary(a => a.Diff.RelativePath, a => a.Kind);
        Assert.Equal(SyncActionKind.Upload, kinds["newer-local.txt"]);
        Assert.Equal(SyncActionKind.Upload, kinds["only-local.txt"]);
        Assert.Equal(SyncActionKind.CreateRemoteFolder, kinds["dir"]);
        Assert.Equal(SyncActionKind.DeleteRemote, kinds["only-remote.txt"]);
        Assert.Equal(SyncActionKind.DeleteRemote, kinds["old"]);
        Assert.False(kinds.ContainsKey("old/x.txt"));          // removed with its folder
        Assert.False(kinds.ContainsKey("uploaded.txt"));       // newer copy of the same size on the target
        Assert.False(kinds.ContainsKey("same.txt"));
        Assert.True(plan.ToList().FindIndex(a => a.Diff.RelativePath == "dir") < plan.ToList().FindIndex(a => a.Diff.RelativePath == "dir/a.txt"));
    }

    [Fact]
    public void BothWaysNeverDeletes()
    {
        var plan = FolderSync.Plan(Sample(), SyncDirection.Both, deleteExtra: true);
        Assert.DoesNotContain(plan, a => a.Kind is SyncActionKind.DeleteLocal or SyncActionKind.DeleteRemote);
        var kinds = plan.ToDictionary(a => a.Diff.RelativePath, a => a.Kind);
        Assert.Equal(SyncActionKind.Download, kinds["only-remote.txt"]);
        Assert.Equal(SyncActionKind.Download, kinds["uploaded.txt"]);
        Assert.Equal(SyncActionKind.Upload, kinds["newer-local.txt"]);
        Assert.Equal(SyncActionKind.CreateLocalFolder, kinds["old"]);
    }

    [Fact]
    public void DownloadWithoutDeleteKeepsLocalExtras()
    {
        var plan = FolderSync.Plan(Sample(), SyncDirection.Download, deleteExtra: false);
        Assert.DoesNotContain(plan, a => a.Diff.RelativePath == "only-local.txt");
        Assert.Contains(plan, a => a.Diff.RelativePath == "only-remote.txt" && a.Kind == SyncActionKind.Download);
        // Local is newer here: a one-way download does not overwrite it unless the size differs.
        Assert.Contains(plan, a => a.Diff.RelativePath == "newer-local.txt" && a.Kind == SyncActionKind.Download);
    }

    [Fact]
    public void ThemeVariantsBuildAndStayReadable()
    {
        foreach (var (dark, variant) in new[] { (true, PaletteVariant.Dark), (true, PaletteVariant.Shadow), (true, PaletteVariant.Mica), (true, PaletteVariant.Amoled), (false, PaletteVariant.Lightweight), (false, PaletteVariant.Default) })
        {
            var p = Palette.Build(dark, Palette.DefaultAccent, variant);
            Assert.Equal(variant, p.Variant);
            Assert.True(Rgb.Contrast(p["TextPrimary"], p["BgContent"]) >= 7, $"{variant} text");
            Assert.True(Rgb.Contrast(p["TextSecondary"], p["BgContent"]) >= 4.5, $"{variant} secondary");
            Assert.True(Rgb.Contrast(p["AccentText"], p["BgPanel"]) >= 4.5, $"{variant} accent");
            Assert.True(p.Colors.ContainsKey("AccentTab"));
        }

        // Dark-only variants fall back on light and vice versa.
        Assert.Equal(PaletteVariant.Default, Palette.Build(false, Palette.DefaultAccent, PaletteVariant.Mica).Variant);
        Assert.Equal(PaletteVariant.Default, Palette.Build(true, Palette.DefaultAccent, PaletteVariant.Lightweight).Variant);
        Assert.Equal("#202020", Palette.Build(true, Palette.DefaultAccent, PaletteVariant.Mica)["BgWindow"].ToHex().ToUpperInvariant());
        // 0.0.6: "Dark" is the neutral #202020 family (the former Shadow), "Shadow" a lighter medium gray.
        Assert.Equal("#202020", Palette.Build(true, Palette.DefaultAccent, PaletteVariant.Dark)["BgContent"].ToHex().ToUpperInvariant());
        var shadow = Palette.Build(true, Palette.DefaultAccent, PaletteVariant.Shadow);
        Assert.True(shadow["BgContent"].R > Palette.Build(true, Palette.DefaultAccent, PaletteVariant.Dark)["BgContent"].R);
        Assert.DoesNotContain(Palette.AccentPresets, p => p.Hex == "#4F46E5");
    }

    [Fact]
    public void ExcludesMatchNamesPathsAndChildren()
    {
        var x = new SyncExcludes(".git; node_modules; *.tmp; build/output; docs/**/*.pdf; ~$*", ignoreCase: false);
        Assert.True(x.IsExcluded(".git"));
        Assert.True(x.IsExcluded(".git/objects/ab/cdef"));
        Assert.True(x.IsExcluded("web/node_modules/react/index.js"));
        Assert.True(x.IsExcluded("a/b/c.tmp"));
        Assert.True(x.IsExcluded("build/output/app.dll"));
        Assert.False(x.IsExcluded("src/build/output/app.dll"));
        Assert.True(x.IsExcluded("docs/a/b/manual.pdf"));
        Assert.True(x.IsExcluded("~$report.docx"));
        Assert.False(x.IsExcluded("src/git.cs"));
        Assert.False(x.IsExcluded("Readme.TMP"));
        Assert.True(new SyncExcludes("*.tmp", ignoreCase: true).IsExcluded("Readme.TMP"));
        Assert.True(new SyncExcludes("", false).IsEmpty);
        Assert.False(new SyncExcludes(SyncExcludes.Default, false).IsExcluded("index.html"));

        var diffs = FolderSync.Compare([F(".git/config"), F("a.txt"), F("x.tmp")], [F("a.txt")]);
        Assert.Equal(["a.txt"], x.Apply(diffs).Select(d => d.RelativePath));
    }
}

public class RdpSessionTests
{
    [Fact]
    public void ParsesEnglishQwinsta()
    {
        const string text = """
             SESSIONNAME       USERNAME                 ID  STATE   TYPE        DEVICE
             services                                    0  Disc
            >console           Administrator             1  Active
             rdp-tcp#5         jdoe                      2  Active
                               msmith                    3  Disc
             rdp-tcp                                 65536  Listen
            """;
        var s = Nexode.Core.Rdp.RdpSessions.ParseQwinsta(text);
        Assert.Equal(3, s.Count);
        Assert.Equal((1, "Administrator", "console", "Active", true), (s[0].Id, s[0].User, s[0].SessionName, s[0].State, s[0].IsCurrent));
        Assert.Equal((2, "jdoe", "rdp-tcp#5"), (s[1].Id, s[1].User, s[1].SessionName));
        Assert.Equal((3, "msmith", (string?)null, "Disc"), (s[2].Id, s[2].User, s[2].SessionName, s[2].State));
    }

    [Fact]
    public void ParsesGermanQwinsta()
    {
        const string text = """
             SITZUNGSNAME      BENUTZERNAME             ID  STATUS  TYP         GERÄT
             services                                    0  Getr.
             console                                     1  Verb.
             rdp-tcp#12        Müller                    4  Aktiv
                               schmidt.adm               7  Getr.
            """;
        var s = Nexode.Core.Rdp.RdpSessions.ParseQwinsta(text);
        Assert.Equal([4, 7], s.Select(x => x.Id));
        Assert.Equal("Müller", s[0].User);
        Assert.Equal("Getr.", s[1].State);
        Assert.Empty(Nexode.Core.Rdp.RdpSessions.ParseQwinsta("Error 5 getting sessionnames\r\nError [5]:Access is denied."));
    }
}
