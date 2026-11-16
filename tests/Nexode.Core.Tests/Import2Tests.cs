using Nexode.Core.Import;
using Nexode.Core.Model;
using Xunit;

namespace Nexode.Core.Tests;

public class Import2Tests
{
    [Fact]
    public void ReadsWinScpIni()
    {
        var pw = ForeignImporters.WinScpObfuscate("Sëcret1", "deploy", "web01.example.com");
        var ini = $"""
            [Configuration\Security]
            UseMasterPassword=0

            [Sessions\Customer%20A/web01]
            HostName=web01.example.com
            PortNumber=2222
            UserName=deploy
            Password={pw}
            RemoteDirectory=/var/www
            FSProtocol=2

            [Sessions\Customer%20A/ftp]
            HostName=ftp.example.com
            FSProtocol=5
            Ftps=3

            [Sessions\Default%20Settings]
            HostName=x
            """;
        var r = ForeignImporters.ReadWinScpIni(ini);
        Assert.Equal(3, r.Entries.Count);
        var folder = r.Entries[0];
        Assert.Equal(("Customer A", EntryKind.Folder), (folder.Name, folder.Kind));
        var web = r.Entries.Single(e => e.Name == "web01");
        Assert.Equal((EntryKind.Sftp, "web01.example.com", 2222, "deploy", folder.Id), (web.Kind, web.Host, web.Port, web.Username, web.ParentId!.Value));
        Assert.Equal("/var/www", web.Setting(SettingKeys.RemotePath));
        Assert.Equal("Sëcret1", r.Secrets.Single().Value);
        Assert.Equal("Explicit", r.Entries.Single(e => e.Name == "ftp").Setting(SettingKeys.FtpEncryption));

        var locked = ForeignImporters.ReadWinScpIni(ini.Replace("UseMasterPassword=0", "UseMasterPassword=1"));
        Assert.Empty(locked.Secrets);
        Assert.Equal(1, locked.UndecryptedSecrets);
    }

    [Fact]
    public void WinScpKnownVector()
    {
        // Short form without user/host prefix: flag = length.
        var hex = string.Concat(new[] { 3, 0 }.Concat("abc".Select(c => (int)c)).Select(b => ((~b & 0xFF) ^ 0xA3).ToString("X2")));
        Assert.Equal("abc", ForeignImporters.WinScpPassword(hex, "u", "h"));
        Assert.Null(ForeignImporters.WinScpPassword("ZZ", "u", "h"));
    }

    [Fact]
    public void ReadsMobaXterm()
    {
        const string ini = """
            [Bookmarks]
            SubRep=
            ImgNum=42
            jump= #109#0%jump.example.org%22%admin%%-1%-1%%%22%%0%0%0%%%-1%0%0%0%%1080%%0%0%1#MobaFont%10%0%0%-1%15%236,236,236%30,30,30%180,180,192%0%-1%0%%xterm%-1%-1%_Std_Colors_0_%80%24%0%1%-1%<none>%%0%1%-1#0# #-1

            [Bookmarks_1]
            SubRep=Customer B\Windows
            ImgNum=41
            dc01= #91#4%dc01.example.local%3390%CORP\adm%0%-1%-1%-1%-1%0%0%-1%%%%%0%0%%-1%%-1%-1%0%-1%0%-1#MobaFont%10%0%0%-1%15%236,236,236%30,30,30%180,180,192%0%-1%0%%xterm%-1%-1%_Std_Colors_0_%80%24%0%1%-1%<none>%%0%1%-1#0# #-1
            console= #131#8%COM3  (USB Serial Port (COM3))%115200%3%0%0%1%2%#MobaFont%10%0%0%-1%15%236,236,236%30,30,30%180,180,192%0%-1%0%%xterm%-1%-1%_Std_Colors_0_%80%24%0%1%-1%<none>%%0%1%-1#0# #-1

            [Passwords]
            admin@jump.example.org=abcdef
            """;
        var r = ForeignImporters.ReadMobaXterm(ini);
        var jump = r.Entries.Single(e => e.Name == "jump");
        Assert.Equal((EntryKind.Ssh, "jump.example.org", (int?)null, "admin"), (jump.Kind, jump.Host, jump.Port, jump.Username));
        var dc = r.Entries.Single(e => e.Name == "dc01");
        Assert.Equal((EntryKind.Rdp, 3390), (dc.Kind, dc.Port!.Value));
        Assert.Equal("Windows", r.Entries.Single(e => e.Id == dc.ParentId).Name);
        var serial = r.Entries.Single(e => e.Name == "console");
        Assert.Equal(("COM3", "115200"), (serial.Setting(SettingKeys.SerialPort), serial.Setting(SettingKeys.SerialBaud)));
        Assert.Contains(r.Warnings, w => w.Contains("MobaXterm passwords", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadsRemmina()
    {
        var secret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var pref = "[remmina_pref]\nsecret=" + Convert.ToBase64String(secret) + "\n";
        var files = new[]
        {
            ("rdp_dc01.remmina", "[remmina]\nname=dc01\ngroup=Office/Servers\nprotocol=RDP\nserver=dc01.example.local:3391\nusername=adm\ndomain=CORP\npassword=" + ForeignImporters.RemminaEncrypt("pa55", secret) + "\n"),
            ("ssh_web.remmina", "[remmina]\nname=web\ngroup=Office\nprotocol=SSH\nserver=web.example.local\nusername=root\npassword=.\n"),
            ("spice.remmina", "[remmina]\nname=vm\nprotocol=SPICE\nserver=vm\n")
        };
        var r = ForeignImporters.ReadRemmina(files, pref);
        var dc = r.Entries.Single(e => e.Name == "dc01");
        Assert.Equal(("dc01.example.local", 3391, "CORP"), (dc.Host, dc.Port!.Value, dc.Domain));
        Assert.Equal("Servers", r.Entries.Single(e => e.Id == dc.ParentId).Name);
        Assert.Equal("pa55", r.Secrets.Single().Value);
        Assert.Contains(r.Warnings, w => w.Contains("keyring", StringComparison.Ordinal));
        Assert.Contains(r.Warnings, w => w.Contains("SPICE", StringComparison.Ordinal));
    }
}
