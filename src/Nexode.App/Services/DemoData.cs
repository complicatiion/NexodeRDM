using Nexode.Core.Model;

namespace Nexode.App.Services;

/// <summary>
/// Sample data for trying Nexode out: folders with inheritance, every connection type, environments, categories,
/// favorites and connection profiles. Only reserved names and addresses (example.com/.org/.net, *.example.local,
/// localhost and the documentation ranges 192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24) – nothing points to a real system.
/// </summary>
public static class DemoData
{
    public static void Create(Workspace ws)
    {
        var made = new Dictionary<string, Entry>();

        Entry Add(Entry e, string? key = null)
        {
            ws.Save(e);
            if (key is not null) made[key] = e;
            return e;
        }

        Entry Folder(string name, Guid? parent, string? env = null, string? cat = null, string? icon = null, double? hue = null, bool expanded = true)
        {
            var f = ws.NewEntry(EntryKind.Folder, parent);
            f.Name = name;
            f.ParentId = parent;
            f.EnvironmentId = env;
            f.CategoryId = cat;
            f.Icon = icon;
            if (hue is not null) f.ColorHue = hue;
            f.IsExpanded = expanded;
            return Add(f);
        }

        // ---------------------------------------------------------------- Example Corp (inherits admin credentials)
        var company = Folder("Example Corp", null);
        company.CredentialMode = CredentialMode.Own;
        company.Username = "admin";
        company.Domain = "EXAMPLE";
        company.Description = "Sample company: everything below inherits the user EXAMPLE\\admin.";
        ws.Save(company);

        var windows = Folder("Windows Server", company.Id, "production", "server");
        Add(new Entry { Kind = EntryKind.Rdp, Name = "srv-dc01", Host = "srv-dc01.example.local", ParentId = windows.Id, Os = OsFamily.Windows, CategoryId = "identity", Tags = ["ad", "dns"], IsFavorite = true }, "dc01");
        Add(new Entry { Kind = EntryKind.Rdp, Name = "srv-dc02", Host = "srv-dc02.example.local", ParentId = windows.Id, Os = OsFamily.Windows, CategoryId = "identity", Tags = ["ad"] }, "dc02");
        Add(new Entry { Kind = EntryKind.Rdp, Name = "srv-file01", Host = "srv-file01.example.local", ParentId = windows.Id, Os = OsFamily.Windows, CategoryId = "storage" });
        var hv = Add(new Entry
        {
            Kind = EntryKind.Host, Name = "srv-hv01", Host = "srv-hv01.example.local", ParentId = windows.Id, Os = OsFamily.Windows, CategoryId = "virtualization",
            Settings = { [SettingKeys.MacAddress] = "00:00:5E:00:53:01" }
        });
        Add(new Entry { Kind = EntryKind.Rdp, Name = "Remote Desktop", ParentId = hv.Id, Settings = { [SettingKeys.RdpAdmin] = "1" } }, "hv-rdp");
        Add(new Entry { Kind = EntryKind.Web, Name = "Windows Admin Center", Url = "https://srv-hv01.example.local:6516", ParentId = hv.Id });

        // Citrix farm for the profile example.
        var citrix = Folder("Citrix", company.Id, "production", "virtualization", "layers");
        Add(new Entry { Kind = EntryKind.Rdp, Name = "ctx-ddc01", Host = "ctx-ddc01.example.local", ParentId = citrix.Id, Os = OsFamily.Windows, CategoryId = "server", Tags = ["citrix", "ddc"] }, "ddc01");
        Add(new Entry { Kind = EntryKind.Rdp, Name = "ctx-ddc02", Host = "ctx-ddc02.example.local", ParentId = citrix.Id, Os = OsFamily.Windows, CategoryId = "server", Tags = ["citrix", "ddc"] }, "ddc02");
        Add(new Entry { Kind = EntryKind.Rdp, Name = "ctx-sf01", Host = "ctx-sf01.example.local", ParentId = citrix.Id, Os = OsFamily.Windows, CategoryId = "webservice", Tags = ["citrix", "storefront"] }, "sf01");
        Add(new Entry { Kind = EntryKind.Web, Name = "NetScaler GUI", Url = "https://adc.example.local", ParentId = citrix.Id, CategoryId = "network", Icon = "network" }, "adc");
        Add(new Entry { Kind = EntryKind.Ssh, Name = "NetScaler CLI", Host = "198.51.100.10", ParentId = citrix.Id, Os = OsFamily.Network, CategoryId = "network", CredentialMode = CredentialMode.Own, Username = "nsroot" }, "adc-ssh");

        var linux = Folder("Linux", company.Id, "production", "server");
        var web = Add(new Entry { Kind = EntryKind.Host, Name = "web01", Host = "web01.example.local", ParentId = linux.Id, Os = OsFamily.Linux, CategoryId = "server", CredentialMode = CredentialMode.Own, Username = "root" });
        Add(new Entry { Kind = EntryKind.Ssh, Name = "SSH", ParentId = web.Id }, "web-ssh");
        Add(new Entry { Kind = EntryKind.Sftp, Name = "SFTP /var/www", ParentId = web.Id, Settings = { [SettingKeys.RemotePath] = "/var/www" } }, "web-sftp");
        Add(new Entry { Kind = EntryKind.Web, Name = "Webmin", Url = "https://web01.example.local:10000", ParentId = web.Id });
        Add(new Entry { Kind = EntryKind.Ssh, Name = "db01", Host = "db01.example.local", ParentId = linux.Id, Os = OsFamily.Linux, CategoryId = "database", IsFavorite = true }, "db01");
        Add(new Entry { Kind = EntryKind.Database, Name = "PostgreSQL db01", Host = "db01.example.local", Port = 5432, ParentId = linux.Id, CategoryId = "database", Settings = { [SettingKeys.DbEngine] = "PostgreSQL", [SettingKeys.DbName] = "app" } });
        Add(new Entry { Kind = EntryKind.Ssh, Name = "docker01", Host = "docker01.example.local", ParentId = linux.Id, Os = OsFamily.Linux, CategoryId = "container", Tags = ["docker"] }, "docker");
        Add(new Entry { Kind = EntryKind.Web, Name = "Portainer", Url = "https://docker01.example.local:9443", ParentId = linux.Id, CategoryId = "container" });
        Add(new Entry { Kind = EntryKind.Ssh, Name = "backup01", Host = "backup01.example.local", ParentId = linux.Id, Os = OsFamily.Linux, CategoryId = "backup" });

        var net = Folder("Network", company.Id, null, "network");
        Add(new Entry { Kind = EntryKind.Web, Name = "Firewall", Url = "https://fw01.example.local", ParentId = net.Id, CategoryId = "firewall", Icon = "brick-wall", IsFavorite = true }, "fw");
        Add(new Entry { Kind = EntryKind.Ssh, Name = "core-switch", Host = "192.0.2.2", ParentId = net.Id, Os = OsFamily.Network, CategoryId = "network", Icon = "router" }, "switch");
        Add(new Entry
        {
            Kind = EntryKind.Serial, Name = "Switch console", ParentId = net.Id, CategoryId = "network", Icon = "cable",
            Settings = { [SettingKeys.SerialPort] = OperatingSystem.IsWindows() ? "COM3" : "/dev/ttyUSB0", [SettingKeys.SerialBaud] = "9600" }
        });
        Add(new Entry { Kind = EntryKind.Telnet, Name = "old-printer", Host = "192.0.2.40", ParentId = net.Id, CategoryId = "printer" });
        Add(new Entry { Kind = EntryKind.Web, Name = "iDRAC srv-hv01", Url = "https://192.0.2.21", ParentId = net.Id, CategoryId = "hardware", Icon = "cpu" });

        var lab = Folder("Lab", company.Id, "test");
        Add(new Entry { Kind = EntryKind.Vnc, Name = "kiosk-01", Host = "192.0.2.111", ParentId = lab.Id, Icon = "monitor-smartphone" });
        Add(new Entry { Kind = EntryKind.Ftp, Name = "printer-scan", Host = "192.0.2.130", ParentId = lab.Id, CategoryId = "printer", Settings = { [SettingKeys.FtpEncryption] = nameof(FtpEncryption.Explicit) } });
        Add(new Entry { Kind = EntryKind.Rdp, Name = "test-ws01", Host = "test-ws01.example.local", ParentId = lab.Id, Os = OsFamily.Windows, CategoryId = "workstation", EnvironmentId = "development" });

        var old = Folder("To be decommissioned", company.Id, "trash");
        Add(new Entry { Kind = EntryKind.Rdp, Name = "srv-legacy01", Host = "srv-legacy01.example.local", ParentId = old.Id, Os = OsFamily.Windows, Description = "Switched off end of quarter." });

        // ---------------------------------------------------------------- local services (try the sessions without a network)
        var local = Folder("This computer", null, "development", null, "laptop");
        Add(new Entry { Kind = EntryKind.Ssh, Name = "localhost SSH", Host = "localhost", ParentId = local.Id, CredentialMode = CredentialMode.Prompt }, "local-ssh");
        Add(new Entry { Kind = EntryKind.Sftp, Name = "localhost SFTP", Host = "127.0.0.1", ParentId = local.Id, CredentialMode = CredentialMode.Prompt });
        Add(new Entry { Kind = EntryKind.Vnc, Name = "localhost VNC", Host = "127.0.0.1", ParentId = local.Id, CredentialMode = CredentialMode.Prompt });
        Add(new Entry { Kind = EntryKind.Web, Name = "Local web server", Url = "http://localhost:8080", ParentId = local.Id, CategoryId = "website" });

        // ---------------------------------------------------------------- hosting
        var hosting = Folder("Web hosting", null, null, "website");
        Add(new Entry { Kind = EntryKind.Sftp, Name = "example.org", Host = "ssh.example.org", Port = 2222, ParentId = hosting.Id, CredentialMode = CredentialMode.Own, Username = "deploy" });
        Add(new Entry { Kind = EntryKind.Web, Name = "Hosting panel", Url = "https://panel.example.org", ParentId = hosting.Id });
        Add(new Entry { Kind = EntryKind.Web, Name = "Status page", Url = "https://status.example.net", ParentId = hosting.Id, CategoryId = "webservice" });

        var creds = Folder("Credentials", null, null, null, "key-round");
        Add(new Entry { Kind = EntryKind.Credential, Name = "Domain admin", Username = "adm-example", Domain = "EXAMPLE", ParentId = creds.Id, CredentialMode = CredentialMode.Own });
        Add(new Entry { Kind = EntryKind.Note, Name = "Read me", ParentId = creds.Id, Notes = "Sample data: all names and addresses are reserved examples. Remove it under Settings → Integration → Sample data." });

        // ---------------------------------------------------------------- branch offices (bigger tree, colored folders)
        var branches = Folder("Branch offices", null, "production", null, "building-2", 210);
        string[] cities = ["Hamburg", "Munich", "Vienna", "Zurich", "Lyon"];
        double[] hues = [255, 335, 55, 120, 185];
        for (var c = 0; c < cities.Length; c++)
        {
            var city = cities[c];
            var code = city[..3].ToLowerInvariant();
            var site = Folder(city, branches.Id, null, null, "map-pin", hues[c], expanded: c == 0);
            var servers = Folder("Servers", site.Id, null, "server");
            Add(new Entry { Kind = EntryKind.Rdp, Name = $"{code}-dc01", Host = $"{code}-dc01.example.local", ParentId = servers.Id, Os = OsFamily.Windows, CategoryId = "identity", Tags = ["ad", code] }, $"{code}-dc");
            Add(new Entry { Kind = EntryKind.Rdp, Name = $"{code}-file01", Host = $"{code}-file01.example.local", ParentId = servers.Id, Os = OsFamily.Windows, CategoryId = "storage" });
            Add(new Entry { Kind = EntryKind.Ssh, Name = $"{code}-app01", Host = $"{code}-app01.example.local", ParentId = servers.Id, Os = OsFamily.Linux, CategoryId = "server" }, $"{code}-app");
            var netw = Folder("Network", site.Id, null, "network");
            Add(new Entry { Kind = EntryKind.Ssh, Name = $"{code}-sw01", Host = $"198.51.100.{20 + c * 10}", ParentId = netw.Id, Os = OsFamily.Network, CategoryId = "network", Icon = "router" });
            Add(new Entry { Kind = EntryKind.Web, Name = $"{city} firewall", Url = $"https://fw.{code}.example.local", ParentId = netw.Id, CategoryId = "firewall", Icon = "brick-wall" }, $"{code}-fw");
            Add(new Entry { Kind = EntryKind.Web, Name = $"{city} access points", Url = $"https://wlc.{code}.example.local", ParentId = netw.Id, CategoryId = "network", Icon = "wifi" });
            var clients = Folder("Workstations", site.Id, "development", "workstation");
            for (var i = 1; i <= 4; i++)
            {
                Add(new Entry { Kind = EntryKind.Rdp, Name = $"{code}-ws{i:00}", Host = $"{code}-ws{i:00}.example.local", ParentId = clients.Id, Os = OsFamily.Windows, CategoryId = "workstation" });
            }

            Add(new Entry { Kind = EntryKind.Vnc, Name = $"{code}-kiosk", Host = $"203.0.113.{10 + c}", ParentId = clients.Id, Icon = "monitor-smartphone" });
        }

        // ---------------------------------------------------------------- cloud & storage (SCP, WebDAV, S3, jump host)
        var cloud = Folder("Cloud & storage", null, null, "storage", "cloud", 230);
        var bastion = Add(new Entry { Kind = EntryKind.Ssh, Name = "bastion", Host = "bastion.example.net", ParentId = cloud.Id, Os = OsFamily.Linux, CategoryId = "server", CredentialMode = CredentialMode.Own, Username = "jump", Icon = "shield" }, "bastion");
        Add(new Entry
        {
            Kind = EntryKind.Ssh, Name = "app-internal (via bastion)", Host = "192.0.2.15", ParentId = cloud.Id, Os = OsFamily.Linux, CategoryId = "server",
            Description = "Reached through the jump host 'bastion'.", Settings = { [SettingKeys.TunnelVia] = bastion.Id.ToString() }
        }, "internal");
        Add(new Entry { Kind = EntryKind.Scp, Name = "SCP backups", Host = "backup.example.net", ParentId = cloud.Id, CategoryId = "backup", Settings = { [SettingKeys.RemotePath] = "/srv/backup" } });
        Add(new Entry { Kind = EntryKind.WebDav, Name = "WebDAV share", Url = "https://dav.example.com/remote.php/dav/files/demo/", ParentId = cloud.Id, CategoryId = "storage", Icon = "folder-sync" }, "dav");
        Add(new Entry
        {
            Kind = EntryKind.S3, Name = "S3 bucket assets", Host = "s3.example.com", ParentId = cloud.Id, CategoryId = "storage",
            Settings = { [SettingKeys.S3Bucket] = "assets", [SettingKeys.S3Region] = "eu-central-1", [SettingKeys.S3PathStyle] = "1" }
        }, "s3");
        Add(new Entry { Kind = EntryKind.Web, Name = "Cloud console", Url = "https://console.example.com", ParentId = cloud.Id, CategoryId = "webservice", Icon = "cloud" }, "console");

        // ---------------------------------------------------------------- customers (deeper nesting)
        var customers = Folder("Customers", null, null, null, "briefcase");
        string[] names = ["Alpha Ltd", "Beta GmbH", "Gamma SA"];
        for (var n = 0; n < names.Length; n++)
        {
            var cust = Folder(names[n], customers.Id, n == 2 ? "test" : "production", null, "building", expanded: false);
            var prefix = names[n].Split(' ')[0].ToLowerInvariant();
            Add(new Entry { Kind = EntryKind.Rdp, Name = $"{prefix}-rds01", Host = $"rds01.{prefix}.example.org", ParentId = cust.Id, Os = OsFamily.Windows, CategoryId = "server" }, $"{prefix}-rds");
            Add(new Entry { Kind = EntryKind.Ssh, Name = $"{prefix}-web01", Host = $"web01.{prefix}.example.org", ParentId = cust.Id, Os = OsFamily.Linux, CategoryId = "server" });
            Add(new Entry { Kind = EntryKind.Sftp, Name = $"{prefix} uploads", Host = $"sftp.{prefix}.example.org", ParentId = cust.Id, Settings = { [SettingKeys.RemotePath] = "/uploads" } });
            Add(new Entry { Kind = EntryKind.Web, Name = $"{names[n]} portal", Url = $"https://portal.{prefix}.example.org", ParentId = cust.Id, CategoryId = "website" });
        }

        // ---------------------------------------------------------------- favorites and rail pins
        foreach (var key in new[] { "ham-dc", "mun-fw", "dav", "bastion", "alpha-rds" })
        {
            if (made.TryGetValue(key, out var fav))
            {
                fav.IsFavorite = true;
                ws.Save(fav);
            }
        }

        string? pinKey = null;
        foreach (var key in new[] { "dc01", "fw", "web-ssh", "console", "s3" })
        {
            if (!made.TryGetValue(key, out var pin)) continue;
            pinKey = Nexode.Core.Tree.SortKeys.Between(pinKey, null);
            pin.IsPinned = true;
            pin.PinKey = pinKey;
            ws.Save(pin);
        }

        // ---------------------------------------------------------------- connection profiles
        var profiles = ws.Profiles.ToList();
        void Profile(string name, string icon, ProfileLayout layout, params string[] keys)
        {
            profiles.Add(new ConnectionProfile
            {
                Id = "p-sample-" + profiles.Count, Name = name, Icon = icon, Layout = layout, Order = profiles.Count,
                Entries = [.. keys.Where(made.ContainsKey).Select(k => made[k].Id)]
            });
        }

        Profile("Citrix management", "layers", ProfileLayout.Split, "ddc01", "ddc02", "sf01", "adc-ssh");
        Profile("Domain controllers", "key-round", ProfileLayout.Tabs, "dc01", "dc02");
        Profile("Web stack", "globe", ProfileLayout.NewWindow, "web-ssh", "web-sftp", "db01", "docker");
        Profile("Branch DCs", "building-2", ProfileLayout.Tabs, "ham-dc", "mun-dc", "vie-dc", "zur-dc", "lyo-dc");
        Profile("Branch firewalls", "brick-wall", ProfileLayout.Tabs, "ham-fw", "mun-fw", "vie-fw", "zur-fw", "lyo-fw");
        Profile("Cloud", "cloud", ProfileLayout.Split, "bastion", "internal", "dav", "s3");
        Profile("Customer RDS", "briefcase", ProfileLayout.Tabs, "alpha-rds", "beta-rds", "gamma-rds");
        ws.SaveProfiles(profiles);
    }
}
