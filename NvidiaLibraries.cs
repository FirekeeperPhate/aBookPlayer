using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace aBookPlayer;

/// <summary>
/// NVIDIA's CUDA libraries, for the two things that can use an NVIDIA card through CUDA: transcription (Whisper)
/// and the voices that make an audiobook of a text. They are large, so they are downloaded on demand, once for
/// both, straight from NVIDIA's own server and under NVIDIA's license (nothing of them comes with the app), each
/// archive checked against the SHA-256 NVIDIA publishes. CUDA 12.9: it wants an NVIDIA driver of 2025 or later.
/// </summary>
static class NvidiaLibraries
{
    const string Url = "https://developer.download.nvidia.com/compute/";

    /// <summary>
    /// The archives and the libraries wanted of each, in the order they are loaded. The first two are CUDA itself,
    /// enough for Whisper; the voices also need the other two (<paramref name="Speech"/>), cuDNN 9.10 above all.
    /// </summary>
    static readonly (string Path, string Sha256, long Bytes, bool Speech, string[] Libraries)[] Archives =
    [
        ("cuda/redist/cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-12.9.79-archive.zip",
            "179e9c43b0735ffe67207b3da556eb5a0c50f3047961882b7657d3b822d34ef8", 3_521_238, false, ["cudart64_12.dll"]),
        ("cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-12.9.1.4-archive.zip",
            "d534d98b0b453a98914dbf3adf47d7e84b55037abf02f87466439e1dcef581ed", 549_755_186, false, ["cublasLt64_12.dll", "cublas64_12.dll"]),
        ("cuda/redist/libcufft/windows-x86_64/libcufft-windows-x86_64-11.4.1.4-archive.zip",
            "f26f80bb9abff3269c548e1559e8c2b4ba58ccb8acc6095bbc6404fc962d4b80", 198_361_265, true, ["cufft64_11.dll"]),
        ("cudnn/redist/cudnn/windows-x86_64/cudnn-windows-x86_64-9.10.2.21_cuda12-archive.zip",
            "c1a4567d822ebda7373fa1f19255dff4942302de741f830160b6c7d1fb31af23", 683_336_095, true,
            [
                "cudnn_ops64_9.dll", "cudnn_cnn64_9.dll", "cudnn_adv64_9.dll", "cudnn_graph64_9.dll", "cudnn_engines_precompiled64_9.dll",
                "cudnn_engines_runtime_compiled64_9.dll", "cudnn_heuristic64_9.dll", "cudnn64_9.dll",
            ]),
    ];

    static readonly string Folder = Path.Combine(AppPaths.Local, "nvidia", "cuda12");

    /// <summary>An NVIDIA card with its driver is in this PC (the driver brings this library, which CUDA talks to).</summary>
    public static bool HasCard { get; internal set; } = File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll"));

    static bool Has(string[] libraries) => libraries.All(library => File.Exists(Path.Combine(Folder, library)));

    /// <summary>What is still to download, in bytes: CUDA itself, and with <paramref name="speech"/> what the voices need too.</summary>
    public static long MissingBytes(bool speech) => Archives.Where(a => (speech || !a.Speech) && !Has(a.Libraries)).Sum(a => a.Bytes);

    public static bool IsInstalled(bool speech) => MissingBytes(speech) == 0;

    /// <summary>Downloads and checks the archives still missing; reports the bytes so far, of <see cref="MissingBytes"/>.</summary>
    public static async Task DownloadAsync(bool speech, HttpClient http, IProgress<long> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        long done = 0;
        foreach (var (path, sha256, bytes, forSpeech, libraries) in Archives)
        {
            if ((forSpeech && !speech) || Has(libraries)) continue;
            long before = done;
            var package = Path.Combine(Folder, "archive.zip.part");
            try
            {
                await SpeechRuntime.FetchAsync(http, Url + path, package, new Progress<long>(b => progress.Report(before + b)), ct);
                await using (var check = File.OpenRead(package))
                    if (!Convert.ToHexString(await SHA256.HashDataAsync(check, ct)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The downloaded NVIDIA package is not the expected one (checksum mismatch). Please try again.");
                await Task.Run(() =>
                {
                    using var zip = ZipFile.OpenRead(package);
                    // (Each under another name until all of the archive's are out: a library there is a library whole)
                    foreach (var library in libraries)
                    {
                        ct.ThrowIfCancellationRequested();
                        var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/" + library, StringComparison.OrdinalIgnoreCase))
                                    ?? throw new InvalidDataException($"NVIDIA's package lacks {library}.");
                        entry.ExtractToFile(Path.Combine(Folder, library + ".part"), overwrite: true);
                    }
                    foreach (var library in libraries) File.Move(Path.Combine(Folder, library + ".part"), Path.Combine(Folder, library), overwrite: true);
                }, ct);
            }
            finally
            {
                foreach (var leftover in Directory.GetFiles(Folder, "*.part")) File.Delete(leftover);
            }
            done += bytes;
            progress.Report(done);
        }
    }

    /// <summary>
    /// Loads the libraries by their full names, so what needs them finds these ones wherever it is itself (Windows
    /// gives a library already loaded to whoever asks for it by its bare name, as cuDNN does for its own parts).
    /// What cannot be loaded (no NVIDIA driver) is told by what needs it.
    /// </summary>
    public static void Load(bool speech)
    {
        foreach (var library in Archives.Where(a => speech || !a.Speech).SelectMany(a => a.Libraries))
            NativeLibrary.TryLoad(Path.Combine(Folder, library), out _);
    }
}
