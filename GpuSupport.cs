using System.IO.Compression;
using System.Security.Cryptography;
using Whisper.net.LibraryLoader;

namespace aBookPlayer;

/// <summary>
/// Optional GPU acceleration for Whisper through Vulkan, which works with the normal graphics driver of NVIDIA,
/// AMD and Intel cards (CUDA would also need the CUDA Toolkit). The Vulkan build of the native library is large
/// (~58 MB), so like the models it is downloaded once, on demand, from nuget.org, and checked against the
/// package's published SHA-512 before it is ever loaded.
/// </summary>
static class GpuSupport
{
    const string Version = "1.9.1"; // must match the Whisper.net package version
    const string PackageUrl = $"https://api.nuget.org/v3-flatcontainer/whisper.net.runtime.vulkan/{Version}/whisper.net.runtime.vulkan.{Version}.nupkg";
    const string PackageSha512 = "hRYbrj76y09g38wZccGb3DvNiempYMq6Zf6FX/gKzxjS2Ff1JeTZ/XSSuJ0UgU13q/TAsD/Gl/Ctu0PmNcFjdA==";
    public const long DownloadBytes = 36_867_136;
    static readonly string[] Files = ["ggml-base-whisper.dll", "ggml-cpu-whisper.dll", "ggml-vulkan-whisper.dll", "ggml-whisper.dll", "whisper.dll"];

    /// <summary>Whisper.net looks for runtimes\vulkan\win-x64 in the folder of <see cref="RuntimeOptions.LibraryPath"/>.</summary>
    static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppSettings.AppName, "gpu", Version);
    static string LibraryFolder => Path.Combine(Root, "runtimes", "vulkan", "win-x64");

    /// <summary>A Vulkan driver is installed (it comes with the graphics driver).</summary>
    public static bool IsDriverAvailable => File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll"));

    public static bool IsInstalled => Files.All(f => File.Exists(Path.Combine(LibraryFolder, f)));

    /// <summary>
    /// The native library is chosen when the first model is loaded and stays for the whole session.
    /// True when the CPU-only one is already in use, so GPU support installed since needs a restart.
    /// </summary>
    public static bool NeedsRestart => RuntimeOptions.LoadedLibrary is { } loaded && loaded != RuntimeLibrary.Vulkan && IsInstalled;

    /// <summary>
    /// Picks the native library before the first model load. The Vulkan build also runs on the CPU
    /// (<c>UseGpu = false</c>), so once installed it is always preferred and the GPU is switched per transcription.
    /// Without a working Vulkan driver it fails to load and Whisper.net falls back to the CPU build.
    /// </summary>
    public static void Configure()
    {
        if (RuntimeOptions.LoadedLibrary != null) return; // already loaded: fixed for this session
        if (IsInstalled && IsDriverAvailable)
        {
            RuntimeOptions.LibraryPath = Path.Combine(Root, "whisper.dll");
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];
        }
        else
        {
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
        }
    }

    /// <summary>The Vulkan library is loaded, so the GPU can be used.</summary>
    public static bool IsActive => RuntimeOptions.LoadedLibrary == RuntimeLibrary.Vulkan;

    /// <summary>Downloads and verifies the package, then extracts the native libraries; reports downloaded bytes.</summary>
    public static async Task DownloadAsync(IProgress<long> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        var package = Path.Combine(Root, "package.part");
        var staging = LibraryFolder + ".part";
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            using (var response = await http.GetAsync(PackageUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(package);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
                var buffer = new byte[1 << 20];
                long total = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), ct);
                    hash.AppendData(buffer, 0, n);
                    total += n;
                    progress.Report(total);
                }
                // Native code is about to be loaded into the app: accept only the exact published package
                if (Convert.ToBase64String(hash.GetHashAndReset()) != PackageSha512)
                    throw new IOException("The GPU support download is incomplete or damaged. Please try again.");
            }

            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            using (var zip = ZipFile.OpenRead(package))
            {
                foreach (var file in Files)
                {
                    var entry = zip.GetEntry($"build/win-x64/{file}") ?? throw new IOException($"{file} is missing from the GPU support package.");
                    entry.ExtractToFile(Path.Combine(staging, file));
                }
            }
            if (Directory.Exists(LibraryFolder)) Directory.Delete(LibraryFolder, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(LibraryFolder)!);
            Directory.Move(staging, LibraryFolder);
        }
        finally
        {
            try { File.Delete(package); } catch (IOException) { }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }
    }
}
