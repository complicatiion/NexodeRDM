using System.Globalization;

namespace Nexode.Core.Model;

/// <summary>One drive or mount point of a host.</summary>
public sealed record HostDisk(string Name, long Size, long Used);

/// <summary>
/// System information of a host, read on request over SSH or PowerShell remoting. Kept only on this computer
/// (never in a data source, never in exports).
/// </summary>
public sealed class HostInfo
{
    public DateTime FetchedUtc { get; set; }

    /// <summary>"ssh", "ssh-powershell" or "winrm".</summary>
    public string Source { get; set; } = "";

    public string? HostName { get; set; }
    public string? Os { get; set; }
    public string? Kernel { get; set; }
    public string? Architecture { get; set; }
    public string? Model { get; set; }
    public string? Domain { get; set; }
    public string? Cpu { get; set; }
    public int? Cores { get; set; }
    public long? UptimeSeconds { get; set; }
    public long? MemoryTotal { get; set; }
    public long? MemoryUsed { get; set; }
    public string? Load { get; set; }
    public List<string> Addresses { get; set; } = [];
    public List<HostDisk> Disks { get; set; } = [];

    /// <summary>Marker of every result line; anything else in the output (banners, motd, errors) is ignored.</summary>
    public const string Marker = "NX:";

    /// <summary>
    /// POSIX sh script (Linux, BSD, macOS). Prints "NX:key=value" lines only; every probe is optional, so a missing
    /// tool just leaves a field empty. Read-only: nothing on the host is changed.
    /// </summary>
    public const string PosixScript =
        """
        p() { [ -n "$2" ] && printf 'NX:%s=%s\n' "$1" "$2"; }
        p host "$(hostname 2>/dev/null || uname -n)"
        p kernel "$(uname -sr 2>/dev/null)"
        p arch "$(uname -m 2>/dev/null)"
        if [ -r /etc/os-release ]; then p os "$(. /etc/os-release 2>/dev/null; echo "${PRETTY_NAME:-$NAME $VERSION}")";
        elif command -v sw_vers >/dev/null 2>&1; then p os "$(sw_vers -productName) $(sw_vers -productVersion)"; fi
        if [ -r /proc/uptime ]; then p uptime "$(cut -d' ' -f1 /proc/uptime)";
        else b=$(sysctl -n kern.boottime 2>/dev/null | sed -n 's/.*sec = \([0-9]*\).*/\1/p'); [ -n "$b" ] && p uptime "$(( $(date +%s) - b ))"; fi
        if [ -r /proc/cpuinfo ]; then p cpu "$(grep -m1 -E '^(model name|Model|Hardware)' /proc/cpuinfo | cut -d: -f2- | sed 's/^ *//')";
        else p cpu "$(sysctl -n machdep.cpu.brand_string hw.model 2>/dev/null | head -n1)"; fi
        p cores "$(nproc 2>/dev/null || getconf _NPROCESSORS_ONLN 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null)"
        if [ -r /proc/meminfo ]; then awk '/^MemTotal:/{t=$2}/^MemAvailable:/{a=$2}END{if(t)printf "NX:mem=%.0f %.0f\n",t*1024,(t-a)*1024}' /proc/meminfo;
        else p mem "$(sysctl -n hw.memsize hw.physmem 2>/dev/null | head -n1)"; fi
        [ -r /proc/loadavg ] && p load "$(cut -d' ' -f1-3 /proc/loadavg)"
        [ -r /sys/class/dmi/id/product_name ] && p model "$(cat /sys/class/dmi/id/sys_vendor 2>/dev/null) $(cat /sys/class/dmi/id/product_name 2>/dev/null)"
        p ip "$(hostname -I 2>/dev/null)"
        df -Pk 2>/dev/null | awk 'NR>1 && $1 ~ /^\/dev\// && $2>0 {printf "NX:disk=%s|%.0f|%.0f\n",$6,$2*1024,$3*1024}'
        echo NX:end=1
        """;

    /// <summary>PowerShell script for Windows hosts (Windows PowerShell 5.1 and PowerShell 7, CIM only, read-only).</summary>
    public const string WindowsScript =
        """
        $ErrorActionPreference = 'SilentlyContinue'
        $o = Get-CimInstance Win32_OperatingSystem
        $c = Get-CimInstance Win32_ComputerSystem
        $p = @(Get-CimInstance Win32_Processor)
        "NX:host=$env:COMPUTERNAME"
        "NX:os=$($o.Caption)"
        "NX:kernel=Windows $($o.Version)"
        "NX:arch=$($o.OSArchitecture)"
        if ($o.LastBootUpTime) { "NX:uptime=$([int64]((Get-Date) - $o.LastBootUpTime).TotalSeconds)" }
        if ($p.Count) { "NX:cpu=$($p[0].Name.Trim())"; "NX:cores=$(($p | Measure-Object NumberOfLogicalProcessors -Sum).Sum)" }
        if ($o.TotalVisibleMemorySize) { "NX:mem=$([int64]$o.TotalVisibleMemorySize * 1024) $(([int64]$o.TotalVisibleMemorySize - [int64]$o.FreePhysicalMemory) * 1024)" }
        if ($c) { "NX:model=$($c.Manufacturer) $($c.Model)"; if ($c.PartOfDomain) { "NX:domain=$($c.Domain)" } }
        $ips = @(Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=True' | ForEach-Object { $_.IPAddress } | Where-Object { $_ -and $_ -notlike 'fe80*' })
        if ($ips.Count) { "NX:ip=$($ips -join ' ')" }
        Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object { "NX:disk=$($_.DeviceID)|$([int64]$_.Size)|$([int64]$_.Size - [int64]$_.FreeSpace)" }
        'NX:end=1'
        """;

    /// <summary>Reads the "NX:key=value" lines; null when the output has none (wrong shell, script blocked).</summary>
    public static HostInfo? Parse(string output, string source)
    {
        var info = new HostInfo { Source = source, FetchedUtc = DateTime.UtcNow };
        var any = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(Marker, StringComparison.Ordinal)) continue;
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0) continue;
            var key = line[Marker.Length..eq];
            var value = line[(eq + 1)..].Trim();
            if (value.Length == 0) continue;
            any = true;
            switch (key)
            {
                case "host": info.HostName = value; break;
                case "os": info.Os = value; break;
                case "kernel": info.Kernel = value; break;
                case "arch": info.Architecture = value; break;
                case "model": info.Model = Clean(value); break;
                case "domain": info.Domain = value; break;
                case "cpu": info.Cpu = Clean(value); break;
                case "cores": info.Cores = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null; break;
                case "uptime": info.UptimeSeconds = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var up) && up >= 0 ? (long)up : null; break;
                case "load": info.Load = value; break;
                case "mem":
                    var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var total) && total > 0) info.MemoryTotal = total;
                    if (parts.Length > 1 && long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var used) && used >= 0) info.MemoryUsed = used;
                    break;
                case "ip":
                    foreach (var ip in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        if (!ip.StartsWith("fe80", StringComparison.OrdinalIgnoreCase) && !info.Addresses.Contains(ip)) info.Addresses.Add(ip);
                    break;
                case "disk":
                    var d = value.Split('|');
                    if (d.Length == 3 && long.TryParse(d[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size > 0
                        && long.TryParse(d[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var dused) && info.Disks.All(x => x.Name != d[0]))
                        info.Disks.Add(new HostDisk(d[0], size, Math.Clamp(dused, 0, size)));
                    break;
            }
        }

        return any && (info.HostName ?? info.Os ?? info.Kernel) is not null ? info : null;
    }

    private static string Clean(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
