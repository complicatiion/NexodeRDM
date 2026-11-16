using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Nexode.Protocols.Terminal;

/// <summary>
/// A local program in an embedded terminal. Windows: pseudo console (ConPTY, Windows 10 1809+). Linux/macOS: the
/// program runs under <c>script</c>, which gives it a real pseudo terminal (resize is not passed on there).
/// An optional secret is typed into the program once its prompt appears (never on the command line).
/// </summary>
public sealed partial class LocalProcessConnection : ITerminalConnection
{
    private readonly string _file;
    private readonly IReadOnlyList<string> _args;
    private readonly string? _secret;
    private readonly Regex? _secretPrompt;
    private readonly StringBuilder _seen = new();
    private int _closed;
    private bool _secretSent;
    private Stream? _input;
    private Process? _process;
    private PseudoConsole? _console;

    /// <param name="secretPrompt">Prompt (on the visible text, escape sequences removed) that triggers sending <paramref name="secret"/> + Enter.</param>
    public LocalProcessConnection(string file, IReadOnlyList<string> args, string? secret = null, Regex? secretPrompt = null)
    {
        _file = file;
        _args = args;
        _secret = string.IsNullOrEmpty(secret) ? null : secret;
        _secretPrompt = secretPrompt;
    }

    public event Action<byte[]>? DataReceived;
    public event Action<string?>? Closed;

    public bool IsConnected => _closed == 0 && _input is not null;

    public Task ConnectAsync(int columns, int rows, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows()) StartWindows(columns, rows);
        else StartUnix(columns, rows);
        return Task.CompletedTask;
    }

    public void Send(byte[] data)
    {
        if (_input is not { } input || _closed != 0) return;
        try
        {
            lock (input)
            {
                input.Write(data);
                input.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Close(ex.Message);
        }
    }

    public void Resize(int columns, int rows)
    {
        if (OperatingSystem.IsWindows()) _console?.Resize(columns, rows);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Cleanup();
    }

    // ------------------------------------------------------------------ Windows (ConPTY)

    [SupportedOSPlatform("windows")]
    private void StartWindows(int columns, int rows)
    {
        _console = PseudoConsole.Start(_file, _args, columns, rows);
        _input = _console.Input;
        _process = _console.Process;
        StartReader(_console.Output);
        WatchExit(_process);
    }

    // ------------------------------------------------------------------ Linux / macOS (script)

    private void StartUnix(int columns, int rows)
    {
        var command = $"stty cols {columns} rows {rows} 2>/dev/null; exec " + string.Join(' ', new[] { _file }.Concat(_args).Select(ShellQuote));
        var psi = new ProcessStartInfo("script")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (OperatingSystem.IsMacOS())
        {
            foreach (var a in new[] { "-q", "/dev/null", "/bin/sh", "-c", command }) psi.ArgumentList.Add(a);
        }
        else
        {
            foreach (var a in new[] { "-q", "-f", "-e", "-c", command, "/dev/null" }) psi.ArgumentList.Add(a);
        }

        psi.Environment["TERM"] = "xterm-256color";
        psi.Environment["COLUMNS"] = columns.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.Environment["LINES"] = rows.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _process = Process.Start(psi) ?? throw new IOException("script could not be started.");
        _input = _process.StandardInput.BaseStream;
        StartReader(_process.StandardOutput.BaseStream);
        StartReader(_process.StandardError.BaseStream);
        WatchExit(_process);
    }

    /// <summary>POSIX shell quoting (single quotes).</summary>
    public static string ShellQuote(string s) => s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or '=' or ':' or ',')
        ? s
        : "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    // ------------------------------------------------------------------ common

    private void StartReader(Stream output)
    {
        var thread = new Thread(() =>
        {
            var buffer = new byte[16384];
            try
            {
                while (true)
                {
                    var n = output.Read(buffer, 0, buffer.Length);
                    if (n <= 0) break;
                    var chunk = buffer.AsSpan(0, n).ToArray();
                    DataReceived?.Invoke(chunk);
                    WatchForPrompt(chunk);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        })
        { IsBackground = true, Name = "nexode-pty-read" };
        thread.Start();
    }

    private void WatchExit(Process process) =>
        _ = process.WaitForExitAsync().ContinueWith(_ =>
        {
            // Give the reader a moment to deliver the last output before the session ends.
            Thread.Sleep(150);
            Close(null);
        }, TaskScheduler.Default);

    /// <summary>Types the secret once the prompt shows up (visible text only, at most once).</summary>
    private void WatchForPrompt(byte[] chunk)
    {
        if (_secret is null || _secretSent || _secretPrompt is null) return;
        lock (_seen)
        {
            if (_secretSent) return;
            _seen.Append(StripEscapes(Encoding.UTF8.GetString(chunk)));
            if (_seen.Length > 4096) _seen.Remove(0, _seen.Length - 4096);
            if (!_secretPrompt.IsMatch(_seen.ToString())) return;
            _secretSent = true;
            _seen.Clear();
        }

        Send(Encoding.UTF8.GetBytes(_secret + "\r"));
    }

    [GeneratedRegex(@"\x1b(\[[0-9;?<>=!]*[ -/]*[@-~]|\][^\x07\x1b]*(\x07|\x1b\\)|[@-Z\\-_])")]
    private static partial Regex EscapeSequence();

    public static string StripEscapes(string text) => EscapeSequence().Replace(text, "");

    private void Close(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Cleanup();
        Closed?.Invoke(reason);
    }

    private void Cleanup()
    {
        try
        {
            if (_process is { HasExited: false } p) p.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }

        _input = null;
        if (OperatingSystem.IsWindows() && _console is { } console)
        {
            // ClosePseudoConsole can block until the output pipe is drained: never on the caller's thread.
            _ = Task.Run(console.Dispose);
        }

        _process?.Dispose();
    }

    // ------------------------------------------------------------------ ConPTY interop

    [SupportedOSPlatform("windows")]
    private sealed partial class PseudoConsole : IDisposable
    {
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const nint ProcThreadAttributePseudoConsole = 0x00020016;
        private nint _handle;

        private PseudoConsole(nint handle, Stream input, Stream output, Process process)
        {
            _handle = handle;
            Input = input;
            Output = output;
            Process = process;
        }

        public Stream Input { get; }
        public Stream Output { get; }
        public Process Process { get; }

        public static PseudoConsole Start(string file, IReadOnlyList<string> args, int columns, int rows)
        {
            if (!CreatePipe(out var inRead, out var inWrite, 0, 0) || !CreatePipe(out var outRead, out var outWrite, 0, 0))
                throw new IOException("Pipes for the pseudo console could not be created.");
            var size = (uint)(ushort)Math.Clamp(columns, 10, 500) | (uint)(ushort)Math.Clamp(rows, 5, 300) << 16;
            var hr = CreatePseudoConsole(size, inRead, outWrite, 0, out var console);
            if (hr != 0) throw new IOException($"The pseudo console could not be created (0x{hr:X8}). Windows 10 1809 or newer is required.");

            nint listSize = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref listSize);
            var list = Marshal.AllocHGlobal(listSize);
            var commandLine = Marshal.StringToHGlobalUni(CommandLine(file, args));
            try
            {
                if (!InitializeProcThreadAttributeList(list, 1, 0, ref listSize) ||
                    !UpdateProcThreadAttribute(list, 0, ProcThreadAttributePseudoConsole, console, IntPtr.Size, 0, 0))
                    throw new IOException("The pseudo console could not be attached.");

                var si = new StartupInfoEx { cb = Marshal.SizeOf<StartupInfoEx>(), AttributeList = list };
                if (!CreateProcessW(0, commandLine, 0, 0, false, ExtendedStartupInfoPresent, 0, 0, ref si, out var pi))
                    throw new IOException($"{file} could not be started (error {Marshal.GetLastPInvokeError()}).");
                CloseHandle(pi.Thread);
                var process = System.Diagnostics.Process.GetProcessById(pi.ProcessId);
                CloseHandle(pi.Process);

                // The pseudo console owns these ends now.
                inRead.Dispose();
                outWrite.Dispose();
                return new PseudoConsole(console, new FileStream(inWrite, FileAccess.Write, 1), new FileStream(outRead, FileAccess.Read, 1), process);
            }
            catch
            {
                ClosePseudoConsole(console);
                inRead.Dispose();
                inWrite.Dispose();
                outRead.Dispose();
                outWrite.Dispose();
                throw;
            }
            finally
            {
                DeleteProcThreadAttributeList(list);
                Marshal.FreeHGlobal(list);
                Marshal.FreeHGlobal(commandLine);
            }
        }

        public void Resize(int columns, int rows)
        {
            if (_handle != 0) ResizePseudoConsole(_handle, (uint)(ushort)Math.Clamp(columns, 10, 500) | (uint)(ushort)Math.Clamp(rows, 5, 300) << 16);
        }

        public void Dispose()
        {
            var h = Interlocked.Exchange(ref _handle, 0);
            if (h != 0) ClosePseudoConsole(h);
            Input.Dispose();
            Output.Dispose();
            Process.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public int cb;
            public nint Reserved;
            public nint Desktop;
            public nint Title;
            public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public short ShowWindow, Reserved2;
            public nint Reserved2Ptr, StdInput, StdOutput, StdError;
            public nint AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public nint Process;
            public nint Thread;
            public int ProcessId;
            public int ThreadId;
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, uint size);

        [LibraryImport("kernel32.dll")]
        private static partial int CreatePseudoConsole(uint size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);

        [LibraryImport("kernel32.dll")]
        private static partial int ResizePseudoConsole(nint console, uint size);

        [LibraryImport("kernel32.dll")]
        private static partial void ClosePseudoConsole(nint console);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

        [LibraryImport("kernel32.dll")]
        private static partial void DeleteProcThreadAttributeList(nint list);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CreateProcessW(nint application, nint commandLine, nint processAttributes, nint threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, nint directory, ref StartupInfoEx startupInfo, out ProcessInformation info);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(nint handle);
    }

    /// <summary>Windows command line from an argument list (the quoting rules of CommandLineToArgvW).</summary>
    public static string CommandLine(string file, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        Append(file);
        foreach (var a in args)
        {
            sb.Append(' ');
            Append(a);
        }

        return sb.ToString();

        void Append(string arg)
        {
            if (arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '"'))
            {
                sb.Append(arg);
                return;
            }

            sb.Append('"');
            var backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                }
                else
                {
                    sb.Append('\\', backslashes);
                }

                backslashes = 0;
                sb.Append(c);
            }

            sb.Append('\\', backslashes * 2);
            sb.Append('"');
        }
    }
}
