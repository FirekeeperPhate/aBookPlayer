using System.IO.Compression;
using System.Security.Cryptography;
using Whisper.net.LibraryLoader;

namespace aBookPlayer;

/// <summary>
/// Optional GPU acceleration for Whisper. Through Vulkan, which works with the normal graphics driver of NVIDIA,
/// AMD and Intel cards; or, on an NVIDIA card, through CUDA, which is faster there but also needs NVIDIA's CUDA
/// libraries (see <see cref="NvidiaLibraries"/>). The builds of the native library for them are large (~58 MB
/// and ~560 MB), so like the models they are downloaded once, on demand, from nuget.org, and checked against the
/// package's published SHA-512 before they are ever loaded.
/// </summary>
static class GpuSupport
{
    const string Version = "1.9.1"; // must match the Whisper.net package version
    const string PackageUrl = $"https://api.nuget.org/v3-flatcontainer/whisper.net.runtime.vulkan/{Version}/whisper.net.runtime.vulkan.{Version}.nupkg";
    const string PackageSha512 = "hRYbrj76y09g38wZccGb3DvNiempYMq6Zf6FX/gKzxjS2Ff1JeTZ/XSSuJ0UgU13q/TAsD/Gl/Ctu0PmNcFjdA==";
    public const long DownloadBytes = 36_867_136;
    static readonly string[] Files = ["ggml-base-whisper.dll", "ggml-cpu-whisper.dll", "ggml-vulkan-whisper.dll", "ggml-whisper.dll", "whisper.dll"];

    // The build for CUDA 12 (the one for CUDA 13 would want a newer driver, and libraries the voices do not share)
    const string CudaPackageUrl = $"https://api.nuget.org/v3-flatcontainer/whisper.net.runtime.cuda12.windows/{Version}/whisper.net.runtime.cuda12.windows.{Version}.nupkg";
    const string CudaPackageSha512 = "pzvo217y8nHPcZav82zJ/uHstnsCFpNn6dodrOi2WjwY3j7xyPcBBhGXeRvvpRuZiRY0cuT8BETcY9nKGqVOZw==";
    const long CudaPackageBytes = 250_058_371;
    static readonly string[] CudaFiles = ["ggml-base-whisper.dll", "ggml-cpu-whisper.dll", "ggml-cuda-whisper.dll", "ggml-whisper.dll", "whisper.dll"];

    /// <summary>Whisper.net looks for runtimes\vulkan\win-x64 and runtimes\cuda12\win-x64 in the folder of <see cref="RuntimeOptions.LibraryPath"/>.</summary>
    static readonly string Root = Path.Combine(AppPaths.Local, "gpu", Version);
    static string LibraryFolder => Path.Combine(Root, "runtimes", "vulkan", "win-x64");
    static string CudaFolder => Path.Combine(Root, "runtimes", "cuda12", "win-x64");

    /// <summary>A Vulkan driver is installed (it comes with the graphics driver).</summary>
    public static bool IsDriverAvailable => File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll"));

    public static bool IsInstalled => Files.All(f => File.Exists(Path.Combine(LibraryFolder, f)));

    static bool HasCudaLibrary => CudaFiles.All(f => File.Exists(Path.Combine(CudaFolder, f)));

    /// <summary>What is still to download for CUDA, in bytes (NVIDIA's libraries may be here already, for the voices).</summary>
    public static long CudaMissingBytes => (HasCudaLibrary ? 0 : CudaPackageBytes) + NvidiaLibraries.MissingBytes(speech: false);

    public static bool IsCudaInstalled => CudaMissingBytes == 0;

    /// <summary>CUDA is to be used when it can be (an NVIDIA card, and its support downloaded): the user's choice.</summary>
    public static bool PreferCuda { get; set; }

    /// <summary>The library that would be loaded now: what is chosen, installed, and has a driver for it.</summary>
    static RuntimeLibrary Wanted =>
        PreferCuda && NvidiaLibraries.HasCard && IsCudaInstalled ? RuntimeLibrary.Cuda12
        : IsInstalled && IsDriverAvailable ? RuntimeLibrary.Vulkan
        : RuntimeLibrary.Cpu;

    /// <summary>
    /// The native library is chosen when the first model is loaded and stays for the whole session. True when
    /// another one is in use than the one wanted now (GPU support installed since, CUDA chosen since), which
    /// therefore needs a restart. (With CUDA in use and Vulkan wanted there is nothing to gain from one.)
    /// </summary>
    public static bool NeedsRestart => RuntimeOptions.LoadedLibrary is { } loaded && Wanted is var wanted && wanted != loaded
                                       && wanted != RuntimeLibrary.Cpu && !(wanted == RuntimeLibrary.Vulkan && loaded == RuntimeLibrary.Cuda12);

    /// <summary>
    /// Picks the native library before the first model load. The GPU builds also run on the CPU
    /// (<c>UseGpu = false</c>), so once installed they are always preferred and the GPU is switched per
    /// transcription. One that cannot be loaded (no working driver) makes Whisper.net fall back to the next.
    /// </summary>
    public static void Configure()
    {
        if (RuntimeOptions.LoadedLibrary != null) return; // already loaded: fixed for this session
        var order = new List<RuntimeLibrary>();
        if (Wanted == RuntimeLibrary.Cuda12)
        {
            NvidiaLibraries.Load(speech: false);
            order.Add(RuntimeLibrary.Cuda12);
        }
        if (IsInstalled && IsDriverAvailable) order.Add(RuntimeLibrary.Vulkan);
        if (order.Count > 0) RuntimeOptions.LibraryPath = Path.Combine(Root, "whisper.dll");
        order.Add(RuntimeLibrary.Cpu);
        RuntimeOptions.RuntimeLibraryOrder = order;
    }

    /// <summary>A GPU library is loaded, so the GPU can be used.</summary>
    public static bool IsActive => RuntimeOptions.LoadedLibrary is RuntimeLibrary.Vulkan or RuntimeLibrary.Cuda12;

    /// <summary>The library loaded is the one for CUDA.</summary>
    public static bool UsesCuda => RuntimeOptions.LoadedLibrary == RuntimeLibrary.Cuda12;

    /// <summary>Downloads and verifies the Vulkan package, then extracts the native libraries; reports downloaded bytes.</summary>
    public static Task DownloadAsync(IProgress<long> progress, CancellationToken ct) =>
        DownloadPackageAsync(PackageUrl, PackageSha512, Files, LibraryFolder, progress, ct);

    /// <summary>
    /// Downloads what CUDA still lacks: Whisper's library for it, and NVIDIA's own; reports the bytes so far, of
    /// <see cref="CudaMissingBytes"/>.
    /// </summary>
    public static async Task DownloadCudaAsync(IProgress<long> progress, CancellationToken ct)
    {
        long done = 0;
        if (!HasCudaLibrary)
        {
            await DownloadPackageAsync(CudaPackageUrl, CudaPackageSha512, CudaFiles, CudaFolder, progress, ct);
            done = CudaPackageBytes;
        }
        using var http = SpeechRuntime.NewClient();
        await NvidiaLibraries.DownloadAsync(speech: false, http, new Progress<long>(b => progress.Report(done + b)), ct);
    }

    static async Task DownloadPackageAsync(string url, string sha512, string[] files, string folder, IProgress<long> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        var package = Path.Combine(Root, "package.part");
        var staging = folder + ".part";
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
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
                if (Convert.ToBase64String(hash.GetHashAndReset()) != sha512)
                    throw new IOException("The GPU support download is incomplete or damaged. Please try again.");
            }

            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            using (var zip = ZipFile.OpenRead(package))
            {
                foreach (var file in files)
                {
                    var entry = zip.GetEntry($"build/win-x64/{file}") ?? throw new IOException($"{file} is missing from the GPU support package.");
                    entry.ExtractToFile(Path.Combine(staging, file));
                }
            }
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
            Directory.Move(staging, folder);
        }
        finally
        {
            try { File.Delete(package); } catch (IOException) { }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }
    }
}
