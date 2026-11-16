using System.Net.Http;
using System.Net.Http.Headers;
using Nexode.Core.Updates;

namespace Nexode.App.Services;

/// <summary>
/// Optional check for a newer release on GitHub (off by default). Sends one anonymous request to the GitHub API –
/// no identifiers, no data about the user. Nothing is downloaded or installed automatically.
/// </summary>
public static class UpdateCheck
{
    public const string ApiUrl = "https://api.github.com/repos/complicatiion/NexodeRDM/releases/latest";

    public static ReleaseInfo? Available { get; private set; }

    public static event Action? Changed;

    /// <summary>Checks at most once a day (or now when <paramref name="force"/>). Returns the newer release or null.</summary>
    public static async Task<ReleaseInfo?> RunAsync(bool force = false)
    {
        var s = AppHost.Settings;
        if (!force && (!s.UpdateCheck || s.LastUpdateCheckUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(20))) return Available;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NexodeRDM", AppHost.Version));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            var json = await http.GetStringAsync(ApiUrl);
            s.LastUpdateCheckUtc = DateTime.UtcNow;
            s.Save();
            var release = ReleaseInfo.Parse(json);
            Available = release is not null && ReleaseInfo.IsNewer(release.Version, AppHost.Version) ? release : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            AppLog.Write("update check: " + ex.GetType().Name);
            if (force) throw;
        }

        Changed?.Invoke();
        return Available;
    }
}
