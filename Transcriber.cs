using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.Logger;

namespace aBookPlayer;

sealed record WhisperModelInfo(GgmlType Type, string Name, int SizeMb, string Note, QuantizationType Quantization = QuantizationType.NoQuantization)
{
    /// <summary>Stored in the settings; the full-precision models keep their original ids (and file names).</summary>
    public string Id => Quantization == QuantizationType.NoQuantization ? Type.ToString() : $"{Type}-{Quantization}";
    public string FilePath => Path.Combine(WhisperModels.Folder, $"ggml-{Id.ToLowerInvariant()}.bin");
    public bool IsDownloaded => File.Exists(FilePath);
    public bool IsEnglishOnly => Type.ToString().EndsWith("En", StringComparison.Ordinal);
}

static class WhisperModels
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppSettings.AppName, "models");

    public static readonly WhisperModelInfo[] All =
    [
        new(GgmlType.Tiny, "Tiny", 75, "fastest, low accuracy"),
        new(GgmlType.Base, "Base", 142, "fast, fair accuracy"),
        new(GgmlType.BaseEn, "Base (English only)", 142, "fast, more accurate than Base for English"),
        new(GgmlType.Small, "Small", 466, "good balance"),
        new(GgmlType.SmallEn, "Small (English only)", 466, "good balance, tuned for English"),
        new(GgmlType.Medium, "Medium", 1460, "slow, high accuracy"),
        // Quantized: much smaller download and memory use, a bit faster on the CPU (about the same on a GPU)
        new(GgmlType.Medium, "Medium Q8", 785, "nearly the same accuracy, half the size", QuantizationType.Q8_0),
        new(GgmlType.Medium, "Medium Q5", 514, "slightly less accurate, a third of the size", QuantizationType.Q5_0),
        new(GgmlType.LargeV3Turbo, "Large v3 Turbo", 1550, "best accuracy, needs a powerful PC"),
        new(GgmlType.LargeV3Turbo, "Large v3 Turbo Q8", 833, "nearly the same accuracy, half the size", QuantizationType.Q8_0),
        new(GgmlType.LargeV3Turbo, "Large v3 Turbo Q5", 547, "slightly less accurate, a third of the size", QuantizationType.Q5_0),
    ];

    /// <summary>Downloads the model (once) from Hugging Face; reports downloaded bytes through <paramref name="progress"/>.</summary>
    public static async Task DownloadAsync(WhisperModelInfo model, IProgress<long> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var partial = model.FilePath + ".part";
        try
        {
            await using (var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(model.Type, model.Quantization, ct))
            await using (var target = File.Create(partial))
            {
                var buffer = new byte[1 << 20];
                long total = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), ct);
                    total += n;
                    progress.Report(total);
                }
                // A connection cut by a proxy/antivirus can end the stream early without an error
                if (total < model.SizeMb * 1024L * 1024L * 9 / 10)
                    throw new IOException($"The download was incomplete ({total / (1024 * 1024)} of ~{model.SizeMb} MB). Please try again.");
            }
            File.Move(partial, model.FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }
}

/// <summary>The model file exists but Whisper cannot load it (damaged or incomplete).</summary>
sealed class InvalidModelException(string path, Exception inner)
    : Exception($"The speech model \"{Path.GetFileName(path)}\" could not be loaded.", inner)
{
    public string ModelPath { get; } = path;
}

sealed record TranscriptionProgress(double Fraction, string Status, string? LogLine = null);

/// <summary>Local transcription with whisper.cpp (via Whisper.net). No audio ever leaves the PC.</summary>
static class Transcriber
{
    const int SampleRate = 16000;
    const int ChunkSamples = SampleRate * 600;   // ~10-minute chunks: keeps memory low even for long audiobooks
    const int SearchSamples = SampleRate * 30;   // cut at the quietest point in the chunk's last 30 s
    const int WindowSamples = SampleRate / 5;
    static string? _gpuName;                     // graphics card reported by whisper.cpp when the library was loaded

    public static Task<List<SubtitleCue>> TranscribeAsync(string audioPath, string modelPath, string language, bool useGpu,
        IProgress<TranscriptionProgress> progress, CancellationToken ct) =>
        Task.Run(async () =>
        {
            using var reader = AudioFormats.Open(audioPath);
            double totalSec = Math.Max(1, reader.TotalTime.TotalSeconds);
            ISampleProvider source = reader.ToSampleProvider();
            if (source.WaveFormat.Channels > 1) source = new DownmixToMono(source);
            if (source.WaveFormat.SampleRate != SampleRate) source = new WdlResamplingSampleProvider(source, SampleRate);

            var clock = new Stopwatch();
            double chunkStart = 0, chunkLength = 0;

            void Report(double positionSec, string? line = null)
            {
                positionSec = Math.Min(positionSec, totalSec);
                double fraction = Math.Clamp(positionSec / totalSec, 0, 1);
                var status = $"Transcribing {Format(positionSec)} / {Format(totalSec)} ({fraction:P0})";
                if (fraction > 0.02)
                {
                    var remaining = TimeSpan.FromSeconds(clock.Elapsed.TotalSeconds * (1 - fraction) / fraction);
                    status += $" · about {FormatRemaining(remaining)} left";
                }
                progress.Report(new(fraction, status, line));
            }

            progress.Report(new(0, "Loading model…"));
            GpuSupport.Configure();
            WhisperFactory? factory = null;
            WhisperProcessor built;
            bool gpu = false;
            // whisper.cpp names the graphics card in its log ("ggml_vulkan: 0 = <name> (driver) | …"),
            // only when the library is first loaded
            using (LogProvider.AddLogger((_, message) =>
                   {
                       if (message?.StartsWith("ggml_vulkan: 0 = ", StringComparison.Ordinal) == true)
                           _gpuName = message["ggml_vulkan: 0 = ".Length..].Split(" (")[0].Split(" |")[0].Trim();
                   }))
            {
                // The native library is only loaded by the first factory: whether it is the GPU one is known after it
                gpu = useGpu && GpuSupport.IsInstalled;
                while (true)
                {
                    try
                    {
                        // Flash attention: same text, measured ~25% faster on the CPU and ~75% on a GPU
                        factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = gpu, UseFlashAttention = true });
                        built = factory.CreateBuilder()
                            .WithLanguage(language)
                            .WithThreads(Math.Max(1, Environment.ProcessorCount / 2))
                            .WithProgressHandler(percent => Report(chunkStart + chunkLength * percent / 100.0))
                            .Build();
                        // Without a usable Vulkan device whisper.cpp silently runs on the CPU
                        gpu = gpu && GpuSupport.IsActive && _gpuName != null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        factory?.Dispose(); // release the native model memory
                        factory = null;
                        // On the GPU a load can fail for lack of video memory: try again on the CPU before
                        // concluding anything about the model file
                        if (gpu && ex is WhisperModelLoadException)
                        {
                            ct.ThrowIfCancellationRequested();
                            gpu = false;
                            progress.Report(new(0, "Loading model on the CPU…", "The graphics card could not load the model (not enough video memory?): using the CPU."));
                            continue;
                        }
                        // Only a model-load failure means the file is damaged; other errors (e.g. a missing native
                        // library) must not cause a good model to be deleted
                        if (ex is WhisperModelLoadException) throw new InvalidModelException(modelPath, ex);
                        throw;
                    }
                }
            }
            using var _ = factory;
            await using var processor = built;
            var where = gpu ? $"on the graphics card ({_gpuName ?? "Vulkan"})" : $"on the CPU ({Math.Max(1, Environment.ProcessorCount / 2)} threads)";
            progress.Report(new(0, "Starting transcription…", $"Model loaded: transcription runs locally on this PC, {where}."));
            clock.Start();

            var cues = new List<SubtitleCue>();
            var pending = new List<float>(ChunkSamples + SampleRate);
            var buffer = new float[SampleRate];
            bool endOfFile = false;

            while (true)
            {
                // Read past the threshold so there is always room to search for a quiet cut point
                while (!endOfFile && pending.Count <= ChunkSamples)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = source.Read(buffer, 0, buffer.Length);
                    if (n == 0) endOfFile = true;
                    else pending.AddRange(buffer.AsSpan(0, n));
                }
                if (pending.Count == 0) break;

                int cut = endOfFile && pending.Count <= ChunkSamples ? pending.Count : FindQuietCut(pending);
                var chunk = pending.GetRange(0, cut).ToArray();
                pending.RemoveRange(0, cut);
                chunkLength = cut / (double)SampleRate;
                var offset = TimeSpan.FromSeconds(chunkStart);

                await foreach (var segment in processor.ProcessAsync(chunk, ct))
                {
                    var text = segment.Text.Trim();
                    if (text.Length == 0) continue;
                    var start = offset + segment.Start;
                    var end = offset + segment.End;
                    if (end <= start) end = start + TimeSpan.FromSeconds(1);
                    cues.Add(new SubtitleCue(start, end, text));
                    Report(end.TotalSeconds, $"[{Format(start.TotalSeconds)}] {text}");
                }

                chunkStart += chunkLength;
                if (endOfFile && pending.Count == 0) break;
            }

            progress.Report(new(1, $"Completed in {FormatRemaining(clock.Elapsed)}"));
            return cues;
        }, ct);

    /// <summary>Cut index at the lowest-energy point near the end of the chunk, so words are not split.</summary>
    static int FindQuietCut(List<float> samples)
    {
        var span = CollectionsMarshal.AsSpan(samples);
        int best = ChunkSamples;
        double bestEnergy = double.MaxValue;
        for (int start = ChunkSamples - SearchSamples; start + WindowSamples <= ChunkSamples; start += WindowSamples / 2)
        {
            double energy = 0;
            foreach (var s in span.Slice(start, WindowSamples)) energy += s * s;
            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                best = start + WindowSamples / 2;
            }
        }
        return best;
    }

    static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    static string FormatRemaining(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s"
        : $"{t.Seconds} s";
}
