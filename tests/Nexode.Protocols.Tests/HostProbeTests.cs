using System.Diagnostics;
using Nexode.Core.Model;
using Nexode.Protocols;
using Xunit;

namespace Nexode.Protocols.Tests;

public class HostProbeTests
{
    [Fact]
    public void ParsesLinuxOutputAndIgnoresNoise()
    {
        const string output = """
            Welcome to Ubuntu (motd)
            NX:host=web01
            NX:kernel=Linux 6.8.0-45-generic
            NX:arch=x86_64
            NX:os=Ubuntu 24.04.1 LTS
            NX:uptime=93784.21
            NX:cpu=Intel(R)   Xeon(R) Gold 6338
            NX:cores=8
            NX:mem=16777216000 4194304000
            NX:load=0.12 0.30 0.25
            NX:ip=192.0.2.10 fe80::1 2001:db8::10
            NX:disk=/|107374182400|53687091200
            NX:disk=/boot|1073741824|2000000000
            NX:end=1
            """;
        var info = HostInfo.Parse(output, "ssh")!;
        Assert.Equal("web01", info.HostName);
        Assert.Equal("Ubuntu 24.04.1 LTS", info.Os);
        Assert.Equal("Intel(R) Xeon(R) Gold 6338", info.Cpu);
        Assert.Equal(8, info.Cores);
        Assert.Equal(93784, info.UptimeSeconds);
        Assert.Equal(16777216000, info.MemoryTotal);
        Assert.Equal(4194304000, info.MemoryUsed);
        Assert.Equal(["192.0.2.10", "2001:db8::10"], info.Addresses);
        Assert.Equal(2, info.Disks.Count);
        Assert.Equal(1073741824, info.Disks[1].Used); // clamped to the size
    }

    [Fact]
    public void NoMarkersMeansNoResult()
    {
        Assert.Null(HostInfo.Parse("'/bin/sh' is not recognized as an internal or external command", "ssh"));
        Assert.Null(HostInfo.Parse("NX:end=1", "ssh"));
    }

    [Fact]
    public void WindowsOutputParses()
    {
        var info = HostInfo.Parse("NX:host=DC01\r\nNX:os=Microsoft Windows Server 2022 Standard\r\nNX:domain=corp.example\r\nNX:disk=C:|136365211648|40000000000\r\n", "winrm")!;
        Assert.Equal("DC01", info.HostName);
        Assert.Equal("corp.example", info.Domain);
        Assert.Equal("C:", info.Disks.Single().Name);
    }

    [Fact]
    public void WinRmScriptCarriesNoPassword()
    {
        var p = new ConnectParams
        {
            Host = "srv.example", Port = 0, Username = "admin", Domain = "CORP", Password = "Very$ecret'1",
            Settings = new Dictionary<string, string> { [SettingKeys.PsTransport] = "winrm-ssl", [SettingKeys.PsSkipCertCheck] = "1" }
        };
        var script = HostProbe.WinRmScript(p, true);
        Assert.DoesNotContain("Very$ecret", script);
        Assert.Contains("[Console]::In.ReadLine()", script);
        Assert.Contains("-ComputerName 'srv.example' -Port 5986 -UseSSL -Credential $nxCred", script);
        Assert.Contains("'CORP\\admin'", script);
        Assert.Contains("-SessionOption $nxOptions", script);
        Assert.DoesNotContain("Very$ecret", HostProbe.WindowsCommand);
    }

    [Fact]
    public void PosixScriptRunsLocally()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh")) return;
        var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(HostInfo.PosixScript.Replace("\r", "", StringComparison.Ordinal));
        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        var info = HostInfo.Parse(output, "ssh");
        Assert.NotNull(info);
        Assert.NotNull(info!.Kernel);
        Assert.Contains("NX:end=1", output);
    }

    [Fact]
    public void PosixCommandQuotesForAnyShell()
    {
        // The command must survive a POSIX login shell unchanged: one single-quoted argument.
        var cmd = HostProbe.PosixCommand;
        Assert.StartsWith("/bin/sh -c '", cmd);
        Assert.EndsWith("'", cmd);
        Assert.DoesNotContain("\r", cmd);
    }
}
