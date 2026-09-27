using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace aBookPlayer;

/// <summary>Looks for a newer release on GitHub (the repository's latest release, drafts and pre-releases excluded).</summary>
static class UpdateCheck
{
    const string Repository = "MarcoTrombetta/aBookPlayer";
    public const string ReleasesPage = $"https://github.com/{Repository}/releases/latest";

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>The latest release's version, or null if GitHub cannot be reached (offline, rate limit…).</summary>
    public static async Task<Version?> LatestAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("aBookPlayer", CurrentVersion.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
        if (!response.IsSuccessStatusCode) return null;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var tag = json.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
        return Version.TryParse(tag, out var version) ? version : null;
    }

    public static void OpenReleasesPage()
    {
        try { Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true }); }
        catch { /* no browser */ }
    }
}
