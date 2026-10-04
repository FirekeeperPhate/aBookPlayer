using System.Runtime.InteropServices;

namespace aBookPlayer;

/// <summary>
/// CUDA, for NVIDIA graphics cards: NVIDIA's own way to them instead of DirectML, at the price of a large download
/// (1.5 GB, 2.2 GB on the disk), made on demand: ONNX Runtime built for CUDA from nuget.org, and the libraries it
/// needs (see <see cref="NvidiaLibraries"/>). It lives in a folder of its own: a program runs one build of ONNX
/// Runtime, this one or the one with DirectML, chosen when it is first loaded.
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

    static string CudaFolder => Path.Combine(Root, "onnxruntime-cuda-" + OrtVersion);

    static IEnumerable<(string Name, string Sha256, long Bytes)> CudaOrtMissing => CudaOrt.Where(f => !File.Exists(Path.Combine(CudaFolder, f.Name)));

    /// <summary>What is still to download for CUDA, in bytes (NVIDIA's libraries may be here already, for Whisper).</summary>
    public static long CudaMissingBytes => CudaOrtMissing.Sum(f => f.Bytes) + NvidiaLibraries.MissingBytes(speech: true);

    public static bool IsCudaInstalled => CudaMissingBytes == 0;

    /// <summary>Downloads and checks what is missing; reports the bytes downloaded so far, of <see cref="CudaMissingBytes"/>.</summary>
    public static async Task DownloadCudaAsync(IProgress<long> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(CudaFolder);
        using var http = NewClient();
        long done = 0;
        foreach (var (name, sha256, bytes) in CudaOrtMissing.ToList())
        {
            var file = Path.Combine(CudaFolder, name);
            long before = done;
            await RemoteZip.ExtractAsync(http, CudaOrtUrl, "runtimes/win-x64/native/" + name, file + ".part",
                new Progress<long>(b => progress.Report(before + Math.Min(b, bytes))), ct);
            await Task.Run(() => Accept(file, sha256), ct);
            done += bytes;
            progress.Report(done);
        }
        long ort = done;
        await NvidiaLibraries.DownloadAsync(speech: true, http, new Progress<long>(b => progress.Report(ort + b)), ct);
    }

    /// <summary>NVIDIA's libraries first, so ONNX Runtime finds them, then the ONNX Runtime built for CUDA.</summary>
    static IntPtr LoadCuda()
    {
        NvidiaLibraries.Load(speech: true);
        return NativeLibrary.Load(Path.Combine(CudaFolder, "onnxruntime.dll"));
    }
}
