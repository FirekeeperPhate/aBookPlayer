using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace aBookPlayer;

/// <summary>A file attached to a GitHub release.</summary>
sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>The latest GitHub release: its version, notes and files.</summary>
sealed record ReleaseInfo(Version Version, string Notes, IReadOnlyList<ReleaseAsset> Assets);

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
    public static async Task<ReleaseInfo?> LatestAsync(CancellationToken ct)
    {
        using var http = NewClient(TimeSpan.FromSeconds(15));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
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
        return new ReleaseInfo(version, notes, assets);
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

    /// <summary>Downloads the installer to the temp folder and checks its size and SHA-256; returns its path.</summary>
    public static async Task<string> DownloadAsync(ReleaseAsset asset, IProgress<long> progress, CancellationToken ct)
    {
        var folder = Path.Combine(Path.GetTempPath(), "aBookPlayer-update");
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
                    progress.Report(total);
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
    /// done. The caller closes the app right after, so its files can be replaced.
    /// </summary>
    public static void StartInstaller(string installer, InstallKind install) =>
        Process.Start(new ProcessStartInfo(installer)
        {
            UseShellExecute = true,
            Arguments = $"/SILENT /SP- /SUPPRESSMSGBOXES /NORESTART /UPDATE=1 {(install == InstallKind.AllUsers ? "/ALLUSERS" : "/CURRENTUSER")}",
        });

    public static void OpenReleasesPage()
    {
        try { Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true }); }
        catch { /* no browser */ }
    }
}
