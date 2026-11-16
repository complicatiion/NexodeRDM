using System.Net;
using Nexode.Protocols.Server;

namespace Nexode.App.Services;

/// <summary>
/// The one local FTP server of this Nexode instance (optional feature "ftpserver"). It keeps running while tabs and
/// windows change and stops with the feature switch or when Nexode quits.
/// </summary>
public static class FtpServerService
{
    private const int MaxEvents = 300;
    private static readonly Lock Gate = new();
    private static readonly List<FtpServerEvent> EventList = [];
    private static LocalFtpServer? _server;

    static FtpServerService()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
        Features.Changed += () =>
        {
            if (!Features.On(Features.FtpServer)) Stop();
        };
    }

    public static event Action? Changed;

    public static bool IsRunning => _server?.IsRunning == true;
    public static int Clients => _server?.Clients ?? 0;
    public static string? Root { get; private set; }
    public static int Port { get; private set; }
    public static bool Tls { get; private set; }
    public static string? Fingerprint { get; private set; }

    public static IReadOnlyList<FtpServerEvent> Events
    {
        get
        {
            lock (Gate) return EventList.ToList();
        }
    }

    /// <summary>Path of the server certificate (made once, kept so clients keep seeing the same fingerprint).</summary>
    public static string CertificatePath => Path.Combine(AppPaths.KeyDirectory, "ftp-server.pfx");

    public static void Start(FtpServerSettings s, string? password)
    {
        Stop();
        var cert = s.Tls ? LocalFtpServer.LoadOrCreateCertificate(CertificatePath) : null;
        var server = new LocalFtpServer(new FtpServerOptions
        {
            Root = s.Folder ?? throw new InvalidOperationException("No folder."),
            Port = s.Port,
            Bind = IPAddress.Any,
            User = s.Anonymous ? null : s.User,
            Password = password,
            ReadOnly = s.Anonymous || s.ReadOnly,
            Certificate = cert,
            PassiveFrom = s.PassiveFrom,
            PassiveTo = s.PassiveTo
        });
        server.Activity += OnActivity;
        server.Start();
        _server = server;
        Root = s.Folder;
        Port = server.Port;
        Tls = cert is not null;
        Fingerprint = cert is null ? null : "SHA256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.RawData));
        AppLog.Write($"ftp server started on port {Port} (tls {Tls}, read-only {s.Anonymous || s.ReadOnly})");
        Changed?.Invoke();
    }

    public static void Stop()
    {
        var server = Interlocked.Exchange(ref _server, null);
        if (server is null) return;
        server.Activity -= OnActivity;
        server.Dispose();
        AppLog.Write("ftp server stopped");
        Changed?.Invoke();
    }

    private static void OnActivity(FtpServerEvent e)
    {
        lock (Gate)
        {
            EventList.Insert(0, e);
            if (EventList.Count > MaxEvents) EventList.RemoveRange(MaxEvents, EventList.Count - MaxEvents);
        }

        if (e.Kind is "login-failed" or "upload" or "delete") AppLog.Write($"ftp server {e.Kind} from {e.Client}: {e.Detail}");
        Changed?.Invoke();
    }
}
