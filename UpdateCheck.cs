using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace aBookPlayer;

/// <summary>A file attached to a GitHub release.</summary>
sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>The latest GitHub release: its version, notes, files and when it was published.</summary>
sealed record ReleaseInfo(Version Version, string Notes, IReadOnlyList<ReleaseAsset> Assets, DateTime? Published = null);

/// <summary>Looks for a newer release on GitHub (the repository's latest release, drafts and pre-releases excluded).</summary>
static class UpdateCheck
{
    const string Repository = "MarcoTrombetta/aBookPlayer";
    public const string ReleasesPage = $"https://github.com/{Repository}/releases/latest";

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("aBookPlayer", CurrentVersion.ToString()));
        return http;
    }

    /// <summary>The latest release, or null if GitHub cannot be reached (offline, rate limit…).</summary>
    public static Task<ReleaseInfo?> LatestAsync(CancellationToken ct) => ReleaseAsync("latest", ct);

    /// <summary>The release of a version (its files), or null if there is none or GitHub cannot be reached.</summary>
    public static Task<ReleaseInfo?> ReleaseAsync(Version version, CancellationToken ct) => ReleaseAsync($"tags/v{version}", ct);

    static async Task<ReleaseInfo?> ReleaseAsync(string which, CancellationToken ct)
    {
        using var http = NewClient(TimeSpan.FromSeconds(15));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/{which}", ct);
        if (!response.IsSuccessStatusCode) return null;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return Parse(json.RootElement);
    }

    internal static ReleaseInfo? Parse(JsonElement release)
    {
        var tag = release.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var version)) return null;
        var notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        var assets = new List<ReleaseAsset>();
        if (release.TryGetProperty("assets", out var list))
            foreach (var a in list.EnumerateArray())
            {
                // GitHub gives every asset a digest ("sha256:…")
                var digest = a.TryGetProperty("digest", out var d) ? d.GetString() : null;
                assets.Add(new ReleaseAsset(a.GetProperty("name").GetString() ?? "", a.GetProperty("browser_download_url").GetString() ?? "",
                    a.GetProperty("size").GetInt64(), digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null));
            }
        DateTime? published = release.TryGetProperty("published_at", out var at) && at.ValueKind == JsonValueKind.String
            ? at.GetDateTime().ToUniversalTime() : null;
        return new ReleaseInfo(version, notes, assets, published);
    }

    /// <summary>
    /// The installers are attached by GitHub Actions a few minutes after a release is published: until then (a few
    /// hours at most, in case the build failed) the daily check waits instead of offering only the download page.
    /// </summary>
    internal static bool WaitForInstaller(ReleaseInfo release, DateTime now) =>
        release.Published is not { } published || now - published < TimeSpan.FromHours(6);

    /// <summary>
    /// Removes the installers of versions already installed (this one or older). A newer one is kept: its update
    /// did not happen (administrator prompt declined), and the user was told it can be run by hand.
    /// </summary>
    public static void CleanUpDownloads()
    {
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "aBookPlayer-update");
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.GetFiles(folder))
            {
                var version = VersionInName(Path.GetFileName(file));
                bool done = version != null ? version <= CurrentVersion : File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7);
                if (done)
                    try { File.Delete(file); } catch { /* still running: next time */ }
            }
        }
        catch { /* only a temp folder */ }
    }

    /// <summary>"aBookPlayer-1.11.0-x64-setup.exe" → 1.11.0.</summary>
    internal static Version? VersionInName(string fileName)
    {
        var parts = fileName.Split('-');
        return parts.Length > 2 && parts[0] == "aBookPlayer" && Version.TryParse(parts[1], out var v) ? v : null;
    }

    /// <summary>
    /// The installer this copy can update itself with: the full one for the self-contained build, the light one
    /// otherwise; none for the portable zip or a copy not installed by the installer (the page is opened instead).
    /// </summary>
    internal static ReleaseAsset? InstallerFor(ReleaseInfo release, InstallKind install, bool selfContained)
    {
        if (install is not (InstallKind.AllUsers or InstallKind.CurrentUser)) return null;
        var name = $"aBookPlayer-{release.Version}-x64{(selfContained ? "" : "-light")}-setup.exe";
        return release.Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The release notes without the downloads table (the app picks the file itself).</summary>
    internal static string NotesForDisplay(string notes)
    {
        int downloads = notes.IndexOf("## Downloads", StringComparison.OrdinalIgnoreCase);
        var text = (downloads >= 0 ? notes[..downloads] : notes).Replace("\r\n", "\n").Trim();
        // Plain text: headings and bold markers read better without their Markdown signs
        return string.Join(Environment.NewLine, text.Split('\n').Select(l => l.TrimStart('#', ' ').Replace("**", "")));
    }

    /// <summary>
    /// Downloads a release file (the installer: to the temp folder) and checks its size and SHA-256; returns its path.
    /// </summary>
    public static async Task<string> DownloadAsync(ReleaseAsset asset, IProgress<long>? progress, CancellationToken ct, string? folder = null)
    {
        folder ??= Path.Combine(Path.GetTempPath(), "aBookPlayer-update");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, asset.Name);
        var partial = path + ".part";
        try
        {
            using var http = NewClient(TimeSpan.FromMinutes(30));
            using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            using var sha = SHA256.Create();
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = File.Create(partial))
            {
                var buffer = new byte[1 << 16];
                long total = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), ct);
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    total += n;
                    progress?.Report(total);
                }
                sha.TransformFinalBlock([], 0, 0);
                if (total != asset.Size) throw new IOException($"The download was incomplete ({total} of {asset.Size} bytes). Please try again.");
            }
            if (asset.Sha256 != null && !string.Equals(Convert.ToHexString(sha.Hash!), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The downloaded file is not the one published on GitHub (checksum mismatch). Please try again.");
            File.Move(partial, path, overwrite: true);
            return path;
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    /// <summary>
    /// Runs the installer quietly, for the same users as the installed copy, and asks it to open the app again when
    /// done. The app stays open meanwhile: the installer closes it (Restart Manager, forced if needed) only once it
    /// really installs, so if the Windows administrator prompt is declined the app is still there to say so.
    /// </summary>
    public static Process StartInstaller(string installer, InstallKind install) =>
        Process.Start(new ProcessStartInfo(installer)
        {
            UseShellExecute = true,
            Arguments = "/SILENT /SP- /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS /UPDATE=1 " +
                        (install == InstallKind.AllUsers ? "/ALLUSERS" : "/CURRENTUSER"),
        }) ?? throw new InvalidOperationException("The installer did not start.");

    public static void OpenReleasesPage()
    {
        try { Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true }); }
        catch { /* no browser */ }
    }
}
