namespace aBookPlayer;

/// <summary>
/// The Android app, kept by the PC for phones that scan its code without having it: the APK attached to this
/// version's GitHub release (or to the latest one, when this version's has none), downloaded in the background
/// while the library is shared, and served by <see cref="LibraryServer"/>.
/// </summary>
static class PhoneApp
{
    const string Prefix = "aBookPlayer-";
    static readonly string Folder = Path.Combine(AppPaths.Local, "android");
    static Task? _download;
    static DateTime _tried;

    /// <summary>The APK downloaded ("…\android\aBookPlayer-1.11.1.apk"), or null: not yet, or GitHub could not be reached.</summary>
    public static string? Package
    {
        get
        {
            try
            {
                return Directory.Exists(Folder)
                    ? Directory.GetFiles(Folder, Prefix + "*.apk").Where(f => f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(VersionOf).FirstOrDefault()
                    : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>"aBookPlayer-1.11.1.apk" → 1.11.1.</summary>
    internal static Version? VersionOf(string? file) => AppPackages.VersionOf(file);

    /// <summary>
    /// Gets this version's APK in the background, unless it is here already or on its way. After a failure (no
    /// internet), tried again an hour later at the soonest.
    /// </summary>
    public static void Prepare()
    {
        if (_download is { IsCompleted: false } || DateTime.UtcNow - _tried < TimeSpan.FromHours(1)) return;
        if (VersionOf(Package) is { } have && have >= UpdateCheck.CurrentVersion) return;
        _tried = DateTime.UtcNow;
        _download = Task.Run(DownloadAsync);
    }

    static async Task DownloadAsync()
    {
        try
        {
            var apk = Apk(await UpdateCheck.ReleaseAsync(UpdateCheck.CurrentVersion, CancellationToken.None))
                      ?? Apk(await UpdateCheck.LatestAsync(CancellationToken.None));
            if (apk == null) return;
            var path = Path.Combine(Folder, apk.Name);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Folder);
                await UpdateCheck.DownloadAsync(apk, null, CancellationToken.None, Folder);
            }
            // One APK is enough: an older one goes (a phone still downloading it keeps reading it)
            foreach (var old in Directory.GetFiles(Folder, Prefix + "*.apk"))
                if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(old); } catch (IOException) { /* next time */ }
        }
        catch (Exception)
        {
            // Offline, GitHub unreachable or limiting, disk full: the page on the phone offers GitHub's download instead
        }
    }

    static ReleaseAsset? Apk(ReleaseInfo? release) =>
        release?.Assets.FirstOrDefault(a => a.Name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
}
