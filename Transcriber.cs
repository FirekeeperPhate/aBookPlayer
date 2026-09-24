using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.Ggml;

namespace aBookPlayer;

sealed record WhisperModelInfo(GgmlType Type, string Name, int SizeMb, string Note)
{
    public string FilePath => Path.Combine(WhisperModels.Folder, $"ggml-{Type.ToString().ToLowerInvariant()}.bin");
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
        new(GgmlType.LargeV3Turbo, "Large v3 Turbo", 1550, "best accuracy, needs a powerful PC"),
    ];

    /// <summary>Downloads the model (once) from Hugging Face; reports downloaded bytes through <paramref name="progress"/>.</summary>
    public static async Task DownloadAsync(WhisperModelInfo model, IProgress<long> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var partial = model.FilePath + ".part";
        try
        {
            await using (var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(model.Type, QuantizationType.NoQuantization, ct))
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
            }
            File.Move(partial, model.FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }
}

sealed record TranscriptionProgress(double Fraction, string Status, string? LogLine = null);

/// <summary>Local transcription with whisper.cpp (via Whisper.net). No audio ever leaves the PC.</summary>
static class Transcriber
{
    const int SampleRate = 16000;
    const int ChunkSamples = SampleRate * 600;   // ~10-minute chunks: keeps memory low even for long audiobooks
    const int SearchSamples = SampleRate * 30;   // cut at the quietest point in the chunk's last 30 s
    const int WindowSamples = SampleRate / 5;

    public static Task<List<SubtitleCue>> TranscribeAsync(
        string audioPath, string modelPath, string language, IProgress<TranscriptionProgress> progress, CancellationToken ct) =>
        Task.Run(async () =>
        {
            using var reader = AudioFormats.Open(audioPath);
            double totalSec = Math.Max(1, reader.TotalTime.TotalSeconds);
            ISampleProvider source = reader.ToSampleProvider();
            if (source.WaveFormat.Channels > 1) source = new DownmixToMono(source);
            if (source.WaveFormat.SampleRate != SampleRate) source = new WdlResamplingSampleProvider(source, SampleRate);

            progress.Report(new(0, "Loading model…"));
            using var factory = WhisperFactory.FromPath(modelPath);
            progress.Report(new(0, "Starting transcription…", "Model loaded: transcription runs locally on this PC."));

            var clock = Stopwatch.StartNew();
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

            await using var processor = factory.CreateBuilder()
                .WithLanguage(language)
                .WithThreads(Math.Max(1, Environment.ProcessorCount / 2))                .WithProgressHandler(percent => Report(chunkStart + chunkLength * percent / 100.0))
                .Build();

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

    /// <summary>Averages all channels into one (works for stereo as well as multichannel audio).</summary>
    sealed class DownmixToMono(ISampleProvider source) : ISampleProvider
    {
        readonly int _channels = source.WaveFormat.Channels;
        float[] _buffer = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int needed = count * _channels;
            if (_buffer.Length < needed) _buffer = new float[needed];
            int read = source.Read(_buffer, 0, needed);
            int frames = read / _channels;
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < _channels; c++) sum += _buffer[f * _channels + c];
                buffer[offset + f] = sum / _channels;
            }
            return frames;
        }
    }

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
