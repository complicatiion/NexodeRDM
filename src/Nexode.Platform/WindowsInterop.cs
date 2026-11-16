using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Nexode.Platform;

/// <summary>DPAPI without extra entropy (passwords of RDCMan and .rdp files of the current Windows user).</summary>
public static partial class Dpapi
{
    public static unsafe byte[]? UnprotectUser(byte[] blob)
    {
        if (!OperatingSystem.IsWindows() || blob.Length == 0) return null;
        fixed (byte* p = blob)
        {
            var input = new Blob { cbData = blob.Length, pbData = (nint)p };
            if (!CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out var output)) return null;
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int cbData;
        public nint pbData;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref Blob dataIn, nint description, nint entropy, nint reserved, nint prompt, int flags, out Blob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint mem);
}

/// <summary>Short-lived entries in the Windows Credential Manager (password for mstsc without command line arguments).</summary>
public static partial class WindowsCredentials
{
    private const int CredTypeDomainPassword = 2;
    private const int PersistSession = 1;

    public static void WriteTemporary(string target, string user, string password, TimeSpan lifetime)
    {
        if (!OperatingSystem.IsWindows()) return;
        Write(target, user, password);
        _ = Task.Delay(lifetime).ContinueWith(_ => Delete(target), TaskScheduler.Default);
    }

    public static unsafe void Write(string target, string user, string password)
    {
        var secret = Encoding.Unicode.GetBytes(password);
        fixed (byte* pSecret = secret)
        fixed (char* pTarget = target)
        fixed (char* pUser = user)
        {
            var cred = new Credential
            {
                Type = CredTypeDomainPassword,
                TargetName = (nint)pTarget,
                CredentialBlobSize = secret.Length,
                CredentialBlob = (nint)pSecret,
                Persist = PersistSession,
                UserName = (nint)pUser
            };
            CredWriteW(ref cred, 0);
        }

        Array.Clear(secret);
    }

    public static void Delete(string target)
    {
        if (OperatingSystem.IsWindows()) CredDeleteW(target, CredTypeDomainPassword, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public nint TargetName;
        public nint Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(ref Credential credential, int flags);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string target, int type, int flags);
}

/// <summary>Detects a locked Windows workstation (input desktop not switchable).</summary>
public static partial class SessionState
{
    public static bool IsWorkstationLocked()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var desktop = OpenInputDesktop(0, false, 0x0100);
        if (desktop == 0) return true;
        try
        {
            return !SwitchDesktop(desktop);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint OpenInputDesktop(int flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, int access);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SwitchDesktop(nint desktop);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(nint desktop);
}

/// <summary>PuTTY sessions: registry on Windows, ~/.putty/sessions elsewhere.</summary>
public static class PuttySessions
{
    public static IReadOnlyList<(string Name, IReadOnlyDictionary<string, string> Values)> Read()
    {
        var list = new List<(string, IReadOnlyDictionary<string, string>)>();
        if (OperatingSystem.IsWindows())
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Software\SimonTatham\PuTTY\Sessions");
            if (root is null) return list;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key is null) continue;
                var values = key.GetValueNames().ToDictionary(n => n, n => Convert.ToString(key.GetValue(n), System.Globalization.CultureInfo.InvariantCulture) ?? "");
                list.Add((name, values));
            }

            return list;
        }

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".putty", "sessions");
        if (!Directory.Exists(dir)) return list;
        foreach (var file in Directory.GetFiles(dir))
        {
            try
            {
                list.Add((Path.GetFileName(file), Core.Import.ForeignImporters.ParsePuttySessionFile(File.ReadAllText(file))));
            }
            catch (IOException)
            {
            }
        }

        return list;
    }

    public static string SshConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
}

/// <summary>Sessions of other tools found on this computer (WinSCP registry, MobaXterm.ini, Remmina profiles).</summary>
public static class ToolSessions
{
    /// <summary>WinSCP sessions from the registry (Windows); null when WinSCP keeps nothing there.</summary>
    public static (IReadOnlyList<(string Name, IReadOnlyDictionary<string, string> Values)> Sessions, bool MasterPassword)? WinScpRegistry()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var root = Registry.CurrentUser.OpenSubKey(@"Software\Martin Prikryl\WinSCP 2");
        using var sessions = root?.OpenSubKey("Sessions");
        if (root is null || sessions is null) return null;
        var list = new List<(string, IReadOnlyDictionary<string, string>)>();
        foreach (var name in sessions.GetSubKeyNames())
        {
            using var key = sessions.OpenSubKey(name);
            if (key is null) continue;
            list.Add((name, key.GetValueNames().ToDictionary(n => n, n => Convert.ToString(key.GetValue(n), System.Globalization.CultureInfo.InvariantCulture) ?? "")));
        }

        using var security = root.OpenSubKey(@"Configuration\Security");
        var master = Convert.ToString(security?.GetValue("UseMasterPassword"), System.Globalization.CultureInfo.InvariantCulture) == "1";
        return (list, master);
    }

    /// <summary>WinSCP.ini in the usual places (portable WinSCP keeps it next to the program).</summary>
    public static string? WinScpIni() => FirstExisting(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinSCP.ini"));

    /// <summary>MobaXterm.ini (installed: %APPDATA%\MobaXterm; portable: next to the program, ask the user).</summary>
    public static string? MobaXtermIni() => FirstExisting(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MobaXterm", "MobaXterm.ini"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MobaXterm", "MobaXterm.ini"));

    /// <summary>Remmina profile folder and remmina.pref (Linux).</summary>
    public static (string Folder, string? Pref)? Remmina()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folder = FirstExisting(Path.Combine(home, ".local", "share", "remmina"), Path.Combine(home, ".remmina"));
        if (folder is null || !Directory.Exists(folder)) return null;
        var pref = FirstExisting(Path.Combine(home, ".config", "remmina", "remmina.pref"), Path.Combine(home, ".remmina", "remmina.pref"));
        return (folder, pref);
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(p => File.Exists(p) || Directory.Exists(p));
}
