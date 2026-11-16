using System.Text;
using System.Text.RegularExpressions;
using Nexode.Core.Model;
using Nexode.Protocols;
using Nexode.Protocols.Terminal;
using Renci.SshNet;
using Xunit;

namespace Nexode.Protocols.Tests;

public class KeyGenTests
{
    [Fact]
    public void BcryptPbkdfMatchesOpenBsdVectors()
    {
        // bcrypt_pbkdf("password", "salt", 4 rounds), the vector of the OpenBSD regression test.
        Assert.Equal("5bbf0cc293587f1c3635555c27796598d47e579071bf427e9d8fbe842aba34d9",
            Convert.ToHexStringLower(BcryptPbkdf.Derive("password"u8.ToArray(), "salt"u8.ToArray(), 32, 4)));
        // 48 bytes (AES-256 key + IV) with 16 rounds, as ssh-keygen uses it (cross-checked with python-bcrypt).
        Assert.Equal("3d3816243886458445d637cf3128079552de37499858b64a726b9799a5fc53e4165d2b23de45be6d819491a9201cb206",
            Convert.ToHexStringLower(BcryptPbkdf.Derive("Nexode"u8.ToArray(), Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), 48, 16)));
    }

    [Theory]
    [InlineData(SshKeyType.Ed25519, null)]
    [InlineData(SshKeyType.Ed25519, "correct horse")]
    [InlineData(SshKeyType.Rsa3072, null)]
    [InlineData(SshKeyType.Rsa3072, "pässwörd")]
    public void GeneratedKeysLoadInSshNet(SshKeyType type, string? passphrase)
    {
        var pair = SshKeyGen.Generate(type, "admin@nexode", passphrase);
        Assert.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----", pair.PrivateKey);
        Assert.EndsWith(" admin@nexode", pair.PublicKey);
        Assert.StartsWith(type == SshKeyType.Ed25519 ? "ssh-ed25519 " : "ssh-rsa ", pair.PublicKey);
        Assert.Equal(pair.Fingerprint, SshKeyGen.FingerprintOfLine(pair.PublicKey));

        // SSH.NET has its own reader (and its own bcrypt): the key must open there with the passphrase.
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(pair.PrivateKey));
        using var key = new PrivateKeyFile(stream, passphrase);
        var blob = key.HostKeyAlgorithms.First().Data;
        Assert.Equal(pair.PublicKey.Split(' ')[1], Convert.ToBase64String(blob));

        if (passphrase is not null)
        {
            using var wrong = new MemoryStream(Encoding.ASCII.GetBytes(pair.PrivateKey));
            Assert.ThrowsAny<Exception>(() => new PrivateKeyFile(wrong, "wrong"));
        }
    }

    [Fact]
    public void PublicKeyOfFileMatches()
    {
        var pair = SshKeyGen.Generate(SshKeyType.Ed25519, "", "pw");
        var path = Path.Combine(Path.GetTempPath(), "nx-key-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, pair.PrivateKey);
        try
        {
            Assert.Equal(pair.PublicKey + " me", SshKeyGen.PublicKeyOf(path, "pw", "me"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AuthorizeCommandQuotesTheKey()
    {
        var cmd = SshKeyGen.AuthorizeCommand("ssh-ed25519 AAAA it's\n");
        Assert.Contains("'ssh-ed25519 AAAA it'\\''s'", cmd);
        Assert.DoesNotContain("\n", cmd);
        Assert.Contains("chmod 600 ~/.ssh/authorized_keys", cmd);
        Assert.Contains("grep -qxF", cmd);
    }
}

public class PowerShellLaunchTests
{
    private static ConnectParams P(string? transport = null, string? password = "s3cret", string? user = "admin", string? domain = "CORP", int port = 0,
        params (string, string)[] settings)
    {
        var dict = settings.ToDictionary(s => s.Item1, s => s.Item2);
        if (transport is not null) dict[SettingKeys.PsTransport] = transport;
        return new ConnectParams { Host = "srv01.example.com", Port = port, Username = user, Password = password, Domain = domain, Settings = dict };
    }

    [Fact]
    public void WinRmScriptNeverContainsThePassword()
    {
        var p = P(port: 5985, settings: [(SettingKeys.PsAuth, "Kerberos"), (SettingKeys.PsConfiguration, "JEA'Admins")]);
        var plan = PowerShellLaunch.Build(p, "powershell.exe");
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(plan.Arguments[^1]));
        Assert.DoesNotContain("s3cret", script);
        Assert.DoesNotContain(plan.Arguments, a => a.Contains("s3cret", StringComparison.Ordinal));
        Assert.Contains("Read-Host -AsSecureString -Prompt 'Nexode credential'", script);
        Assert.Contains("[System.Management.Automation.PSCredential]::new('CORP\\admin', $nxSecret)", script);
        Assert.Contains("Enter-PSSession -ComputerName 'srv01.example.com' -Port 5985 -Credential $nxCred -Authentication Kerberos -ConfigurationName 'JEA''Admins'", script);
        Assert.Contains("function global:prompt", script);
        Assert.Equal("s3cret", plan.Secret);
        Assert.Matches(plan.SecretPrompt!, "\r\nNexode credential: ");
        Assert.Equal(["-NoLogo", "-NoProfile", "-NoExit", "-EncodedCommand"], plan.Arguments.Take(4));
    }

    [Fact]
    public void HttpsAndSshVariants()
    {
        var ssl = PowerShellLaunch.Script(P("winrm-ssl", settings: [(SettingKeys.PsSkipCertCheck, "1")]), out _);
        Assert.Contains("-Port 5986 -UseSSL", ssl);
        Assert.Contains("New-PSSessionOption -SkipCACheck -SkipCNCheck", ssl);
        Assert.Contains("-SessionOption $nxOptions", ssl);

        var ssh = PowerShellLaunch.Build(P("ssh", user: "root", domain: null), "pwsh");
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(ssh.Arguments[^1]));
        Assert.Contains("Enter-PSSession -HostName 'srv01.example.com' -Port 22 -UserName 'root'", script);
        Assert.DoesNotContain("Read-Host", script);
        Assert.Matches(ssh.SecretPrompt!, "root@srv01.example.com's password: ");

        // UPN and current-user logon.
        Assert.Equal("admin@corp.example", PowerShellLaunch.UserName(P(user: "admin@corp.example")));
        var current = PowerShellLaunch.Script(P(user: null, password: null), out var asks);
        Assert.False(asks);
        Assert.DoesNotContain("-Credential", current);
    }

    [Fact]
    public void ExecutableChoice()
    {
        string? None(string _) => null;
        string? Pwsh(string exe) => exe.StartsWith("pwsh", StringComparison.Ordinal) ? "/opt/" + exe : null;
        Assert.Null(PowerShellLaunch.FindExecutable(P(), None, windows: false, null));
        Assert.Equal("/opt/pwsh", PowerShellLaunch.FindExecutable(P(), Pwsh, windows: false, null));
        Assert.EndsWith("powershell.exe", PowerShellLaunch.FindExecutable(P(), Pwsh, windows: true, @"C:\Windows\System32"));
        Assert.Equal("/opt/pwsh.exe", PowerShellLaunch.FindExecutable(P(settings: [(SettingKeys.PsPwsh, "1")]), Pwsh, windows: true, @"C:\Windows\System32"));
        // SSH needs PowerShell 7 everywhere.
        Assert.Null(PowerShellLaunch.FindExecutable(P("ssh"), None, windows: true, @"C:\Windows\System32"));
    }

    [Fact]
    public void WindowsCommandLineQuoting()
    {
        Assert.Equal("a.exe plain \"with space\" \"q\\\"uote\" trail\\ \"end space\\\\\" \"\"",
            LocalProcessConnection.CommandLine("a.exe", ["plain", "with space", "q\"uote", "trail\\", "end space\\", ""]));
        Assert.Equal("'it'\\''s'", LocalProcessConnection.ShellQuote("it's"));
        Assert.Equal("-NoExit", LocalProcessConnection.ShellQuote("-NoExit"));
    }

    [Fact]
    public async Task LocalProcessAnswersPromptAndEnds()
    {
        if (OperatingSystem.IsWindows()) return;
        // A shell that asks like Read-Host and echoes what it got (the secret goes in through the terminal, not argv).
        // Echo goes off before the prompt, as with Read-Host -AsSecureString (otherwise the answer can race stty).
        using var c = new LocalProcessConnection("/bin/sh", ["-c", "stty -echo; printf 'Nexode credential: '; read s; stty echo; echo; echo \"got:${#s}\"; read x; echo bye"],
            "hunter22", new Regex(@"Nexode credential: ?\s*$"));
        var output = new StringBuilder();
        var closed = new TaskCompletionSource<string?>();
        c.DataReceived += d => { lock (output) output.Append(Encoding.UTF8.GetString(d)); };
        c.Closed += r => closed.TrySetResult(r);
        await c.ConnectAsync(80, 24, CancellationToken.None);
        for (var i = 0; i < 100 && !Snapshot().Contains("got:8", StringComparison.Ordinal); i++) await Task.Delay(50);
        Assert.Contains("got:8", Snapshot());
        Assert.DoesNotContain("hunter22", Snapshot());
        c.Send("x\r"u8.ToArray());
        Assert.Null(await closed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("bye", Snapshot());

        string Snapshot()
        {
            lock (output) return output.ToString();
        }
    }
}
