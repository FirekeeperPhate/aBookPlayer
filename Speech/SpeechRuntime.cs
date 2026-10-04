using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;

namespace aBookPlayer;

/// <summary>
/// What turning text into speech needs besides the voices, downloaded once on demand (about 45 MB) like the
/// Whisper models: ONNX Runtime with DirectML (the speech models run on it, on the processor or on the graphics
/// card of any maker), and espeak-ng (it turns English text into the phonemes the models are given). Each file is
/// checked against a SHA-256 written here before it is ever loaded.
/// </summary>
static class SpeechRuntime
{
    const string OrtVersion = "1.24.4"; // must match the Microsoft.ML.OnnxRuntime.Managed package version
    const string OrtUrl = $"https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime.directml/{OrtVersion}/microsoft.ml.onnxruntime.directml.{OrtVersion}.nupkg";
    const string OrtSha256 = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40";
    const long OrtBytes = 12_458_649;
    // DirectML 1.15.4 (the one Windows has in System32 is older than ONNX Runtime needs): one DLL of a 200 MB package
    const string DirectMlUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.ai.directml/1.15.4/microsoft.ai.directml.1.15.4.nupkg";
    const string DirectMlSha256 = "9c9e6d822561c6c41b90e6994b3e8857cf1d66dbfb1e0c4c799c7c89b4e92da1";
    const long DirectMlBytes = 8_400_000;
    // espeak-ng and its data, from Piper's Windows build
    const string EspeakUrl = "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";
    const string EspeakZipSha256 = "f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea";
    const long EspeakBytes = 22_477_236;

    public const long DownloadBytes = OrtBytes + DirectMlBytes + EspeakBytes;

    public static readonly string Root = Path.Combine(AppPaths.Local, "speech");
    static string RuntimeFolder => Path.Combine(Root, "onnxruntime-" + OrtVersion);
    static string OrtDll => Path.Combine(RuntimeFolder, "onnxruntime.dll");
    static string DirectMlDll => Path.Combine(RuntimeFolder, "DirectML.dll");
    static string EspeakFolder => Path.Combine(Root, "espeak");
    static string EspeakDll => Path.Combine(EspeakFolder, "espeak-ng.dll");

    public static bool IsInstalled =>
        File.Exists(OrtDll) && File.Exists(DirectMlDll) && File.Exists(EspeakDll) && File.Exists(Path.Combine(EspeakFolder, "espeak-ng-data", "phontab"));

    /// <summary>Downloads and checks what is missing; reports the bytes downloaded so far, of <see cref="DownloadBytes"/>.</summary>
    public static async Task DownloadAsync(IProgress<long> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(RuntimeFolder);
        using var http = NewClient();
        long done = 0;
        if (!File.Exists(OrtDll))
        {
            var package = Path.Combine(RuntimeFolder, "onnxruntime.nupkg.part");
            try
            {
                await FetchAsync(http, OrtUrl, package, new Progress<long>(b => progress.Report(b)), ct);
                using var zip = ZipFile.OpenRead(package);
                var entry = zip.GetEntry("runtimes/win-x64/native/onnxruntime.dll") ?? throw new InvalidDataException("The ONNX Runtime package lacks its library.");
                entry.ExtractToFile(OrtDll + ".part", overwrite: true);
            }
            finally
            {
                if (File.Exists(package)) File.Delete(package);
            }
            Accept(OrtDll, OrtSha256);
        }
        done += OrtBytes;
        progress.Report(done);
        if (!File.Exists(DirectMlDll))
        {
            long before = done;
            await RemoteZip.ExtractAsync(http, DirectMlUrl, "bin/x64-win/DirectML.dll", DirectMlDll + ".part",
                new Progress<long>(b => progress.Report(before + Math.Min(b, DirectMlBytes))), ct);
            Accept(DirectMlDll, DirectMlSha256);
        }
        done += DirectMlBytes;
        progress.Report(done);
        if (!File.Exists(EspeakDll) || !File.Exists(Path.Combine(EspeakFolder, "espeak-ng-data", "phontab")))
        {
            long before = done;
            var package = Path.Combine(Root, "espeak.zip.part");
            var staging = EspeakFolder + ".part";
            try
            {
                await FetchAsync(http, EspeakUrl, package, new Progress<long>(b => progress.Report(before + b)), ct);
                await using (var check = File.OpenRead(package))
                    if (!Convert.ToHexString(await SHA256.HashDataAsync(check, ct)).Equals(EspeakZipSha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The downloaded espeak-ng package is not the expected one (checksum mismatch). Please try again.");
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                using (var zip = ZipFile.OpenRead(package))
                    foreach (var entry in zip.Entries)
                    {
                        // "piper/espeak-ng.dll" and "piper/espeak-ng-data/…": the rest of Piper is not needed
                        if (!entry.FullName.StartsWith("piper/espeak-ng", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith('/')) continue;
                        var path = Path.GetFullPath(Path.Combine(staging, entry.FullName["piper/".Length..]));
                        if (!path.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        entry.ExtractToFile(path, overwrite: true);
                    }
                if (Directory.Exists(EspeakFolder)) Directory.Delete(EspeakFolder, recursive: true);
                Directory.Move(staging, EspeakFolder);
            }
            finally
            {
                if (File.Exists(package)) File.Delete(package);
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
        }
        progress.Report(DownloadBytes);
    }

    /// <summary>The file downloaded as "….part" takes its name once its SHA-256 is the expected one.</summary>
    static void Accept(string file, string sha256)
    {
        var partial = file + ".part";
        try
        {
            using (var stream = File.OpenRead(partial))
                if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"The downloaded {Path.GetFileName(file)} is not the expected one (checksum mismatch). Please try again.");
            File.Move(partial, file, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    public static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("aBookPlayer/" + UpdateCheck.CurrentVersion);
        return http;
    }

    /// <summary>Downloads a file to <paramref name="target"/>, reporting the bytes so far.</summary>
    public static async Task FetchAsync(HttpClient http, string url, string target, IProgress<long>? progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long? expected = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(target);
        var buffer = new byte[1 << 20];
        long total = 0;
        int n;
        while ((n = await source.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            total += n;
            progress?.Report(total);
        }
        // A connection cut by a proxy/antivirus can end the stream early without an error
        if (expected is { } length && total != length) throw new IOException($"The download was incomplete ({total} of {length} bytes). Please try again.");
    }

    static readonly object Gate = new();
    static bool _loaded;
    static IntPtr _ort;

    /// <summary>
    /// Loads the native libraries from the app's data folder (once): DirectML first, so ONNX Runtime finds this one
    /// and not Windows' older one, then ONNX Runtime, which the managed library is told to use, then espeak-ng.
    /// </summary>
    public static void Load()
    {
        lock (Gate)
        {
            if (_loaded) return;
            // (What worked stays as it is when a later step fails and this is tried again: an assembly takes one resolver)
            if (_ort == IntPtr.Zero)
            {
                NativeLibrary.Load(DirectMlDll);
                var ort = NativeLibrary.Load(OrtDll);
                NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly,
                    (name, _, _) => name.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase) ? ort : IntPtr.Zero);
                _ort = ort;
            }
            Espeak.Start(EspeakFolder);
            _loaded = true;
        }
    }

    /// <summary>
    /// How a model is run: on the graphics card through DirectML (any maker's, with its normal driver), or on the
    /// processor. DirectML needs these two settings.
    /// </summary>
    public static SessionOptions Options(bool gpu)
    {
        // (Warnings about nodes left to the processor are not news)
        var options = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        if (gpu)
        {
            options.EnableMemoryPattern = false;
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            // The fastest card of the PC: the first one (the only choice of the older call) is the one the screen is
            // plugged into, which on a laptop with two is the integrated one
            try { options.AppendExecutionProvider("DML", new Dictionary<string, string> { ["performance_preference"] = "high_performance", ["device_filter"] = "gpu" }); }
            catch (Exception e) when (e is OnnxRuntimeException or NotSupportedException or ArgumentException) { options.AppendExecutionProvider_DML(0); }
        }
        return options;
    }

    /// <summary>
    /// Opens a model on the graphics card if asked, falling back to the processor when the card cannot run it (no
    /// DirectX 12, an old driver); <paramref name="onGpu"/> says where it ended up.
    /// </summary>
    public static InferenceSession Open(string model, bool gpu, out bool onGpu)
    {
        Load();
        if (gpu)
        {
            try
            {
                using var options = Options(gpu: true);
                var session = new InferenceSession(model, options);
                onGpu = true;
                return session;
            }
            catch (Exception e) when (e is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException) { /* the processor then */ }
        }
        onGpu = false;
        using var cpu = Options(gpu: false);
        return new InferenceSession(model, cpu);
    }
}
