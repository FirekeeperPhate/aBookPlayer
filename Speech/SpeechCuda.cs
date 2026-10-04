using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace aBookPlayer;

/// <summary>
/// CUDA, for NVIDIA graphics cards: NVIDIA's own way to them instead of DirectML, at the price of a large download
/// (1.5 GB, 2.2 GB on the disk), made on demand: ONNX Runtime built for CUDA from nuget.org, and the libraries it
/// needs (CUDA 12 and cuDNN 9) straight from NVIDIA's own server, under NVIDIA's license; nothing of it comes with
/// the app. They live in a folder of their own: a program runs one build of ONNX Runtime, this one or the one with
/// DirectML, chosen when it is first loaded.
/// </summary>
static partial class SpeechRuntime
{
    const string CudaOrtUrl = $"https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime.gpu.windows/{OrtVersion}/microsoft.ml.onnxruntime.gpu.windows.{OrtVersion}.nupkg";
    /// <summary>The three files wanted of that package: their SHA-256 and their size in the package.</summary>
    static readonly (string Name, string Sha256, long Bytes)[] CudaOrt =
    [
        ("onnxruntime.dll", "3b46571d12a9567791a42a2b2967a79c4e2e957aacdba09a2ddb4fb391707baa", 5_278_770),
        ("onnxruntime_providers_shared.dll", "1bcbad19d14bc8395c1422c752e9e4cdd79e316e2582cdcd767bb8f500cdae99", 12_506),
        ("onnxruntime_providers_cuda.dll", "ce1c698cae708fd4ed9ab36eddf256213df40779d0806d20f32132c5d422da72", 166_314_436),
    ];

    const string NvidiaUrl = "https://developer.download.nvidia.com/compute/";
    /// <summary>
    /// NVIDIA's archives (CUDA 12.9.1, cuDNN 9.10.2), with the SHA-256 NVIDIA publishes for each, and the libraries
    /// wanted of them, in the order they are loaded.
    /// </summary>
    static readonly (string Path, string Sha256, long Bytes, string[] Libraries)[] Nvidia =
    [
        ("cuda/redist/cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-12.9.79-archive.zip",
            "179e9c43b0735ffe67207b3da556eb5a0c50f3047961882b7657d3b822d34ef8", 3_521_238, ["cudart64_12.dll"]),
        ("cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-12.9.1.4-archive.zip",
            "d534d98b0b453a98914dbf3adf47d7e84b55037abf02f87466439e1dcef581ed", 549_755_186, ["cublasLt64_12.dll", "cublas64_12.dll"]),
        ("cuda/redist/libcufft/windows-x86_64/libcufft-windows-x86_64-11.4.1.4-archive.zip",
            "f26f80bb9abff3269c548e1559e8c2b4ba58ccb8acc6095bbc6404fc962d4b80", 198_361_265, ["cufft64_11.dll"]),
        ("cudnn/redist/cudnn/windows-x86_64/cudnn-windows-x86_64-9.10.2.21_cuda12-archive.zip",
            "c1a4567d822ebda7373fa1f19255dff4942302de741f830160b6c7d1fb31af23", 683_336_095,
            [
                "cudnn_ops64_9.dll", "cudnn_cnn64_9.dll", "cudnn_adv64_9.dll", "cudnn_graph64_9.dll", "cudnn_engines_precompiled64_9.dll",
                "cudnn_engines_runtime_compiled64_9.dll", "cudnn_heuristic64_9.dll", "cudnn64_9.dll",
            ]),
    ];

    public static readonly long CudaDownloadBytes = CudaOrt.Sum(f => f.Bytes) + Nvidia.Sum(a => a.Bytes);

    static string CudaFolder => Path.Combine(Root, "onnxruntime-cuda-" + OrtVersion);
    /// <summary>Written last: everything is there and checked.</summary>
    static string CudaReady => Path.Combine(CudaFolder, "ready.txt");

    public static bool IsCudaInstalled => File.Exists(CudaReady);

    /// <summary>An NVIDIA card with its driver is in this PC (the driver brings this library, which CUDA talks to).</summary>
    public static bool HasNvidiaCard { get; internal set; } = File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll"));

    /// <summary>
    /// Downloads and checks what is missing (a download stopped halfway goes on from the archives already done);
    /// reports the bytes downloaded so far, of <see cref="CudaDownloadBytes"/>.
    /// </summary>
    public static async Task DownloadCudaAsync(IProgress<long> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(CudaFolder);
        using var http = NewClient();
        long done = 0;
        foreach (var (name, sha256, bytes) in CudaOrt)
        {
            var file = Path.Combine(CudaFolder, name);
            if (!File.Exists(file))
            {
                long before = done;
                await RemoteZip.ExtractAsync(http, CudaOrtUrl, "runtimes/win-x64/native/" + name, file + ".part",
                    new Progress<long>(b => progress.Report(before + Math.Min(b, bytes))), ct);
                await Task.Run(() => Accept(file, sha256), ct);
            }
            done += bytes;
            progress.Report(done);
        }
        foreach (var (path, sha256, bytes, libraries) in Nvidia)
        {
            if (!libraries.All(library => File.Exists(Path.Combine(CudaFolder, library))))
            {
                long before = done;
                var package = Path.Combine(CudaFolder, "nvidia.zip.part");
                try
                {
                    await FetchAsync(http, NvidiaUrl + path, package, new Progress<long>(b => progress.Report(before + b)), ct);
                    await using (var check = File.OpenRead(package))
                        if (!Convert.ToHexString(await SHA256.HashDataAsync(check, ct)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("The downloaded NVIDIA package is not the expected one (checksum mismatch). Please try again.");
                    await Task.Run(() =>
                    {
                        using var zip = ZipFile.OpenRead(package);
                        foreach (var library in libraries)
                        {
                            ct.ThrowIfCancellationRequested();
                            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/" + library, StringComparison.OrdinalIgnoreCase))
                                        ?? throw new InvalidDataException($"NVIDIA's package lacks {library}.");
                            var file = Path.Combine(CudaFolder, library);
                            entry.ExtractToFile(file + ".part", overwrite: true);
                            File.Move(file + ".part", file, overwrite: true);
                        }
                    }, ct);
                }
                finally
                {
                    foreach (var leftover in Directory.GetFiles(CudaFolder, "*.part")) File.Delete(leftover);
                }
            }
            done += bytes;
            progress.Report(done);
        }
        File.WriteAllText(CudaReady, OrtVersion);
    }

    /// <summary>
    /// Loads NVIDIA's libraries by their full names, so ONNX Runtime and cuDNN (which looks for its parts by their
    /// bare names) find these ones, then the ONNX Runtime built for CUDA.
    /// </summary>
    static IntPtr LoadCuda()
    {
        foreach (var library in Nvidia.SelectMany(archive => archive.Libraries))
            NativeLibrary.TryLoad(Path.Combine(CudaFolder, library), out _); // (what cannot be loaded is told when a voice is opened)
        return NativeLibrary.Load(Path.Combine(CudaFolder, "onnxruntime.dll"));
    }
}
