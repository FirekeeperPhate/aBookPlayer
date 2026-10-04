using System.Security.Cryptography;

namespace aBookPlayer;

enum SpeechEngine { Piper, Kokoro }

/// <summary>A voice that can be chosen: which engine speaks it, and what it needs to download.</summary>
/// <param name="Id">Piper: the voice's own name ("en_US-lessac-high"). Kokoro: the voice file ("af_heart").</param>
/// <param name="Bytes">The voice's own download (Kokoro's model, shared by its voices, comes apart).</param>
/// <param name="Md5">Piper publishes an MD5 of each voice; Kokoro's voice files are checked by size.</param>
sealed record SpeechVoiceInfo(SpeechEngine Engine, string Id, string Name, string Note, long Bytes, string? Md5 = null)
{
    public bool British => Engine == SpeechEngine.Piper ? Id.StartsWith("en_GB", StringComparison.Ordinal) : Id.StartsWith('b');
    public override string ToString() => $"{Name}  —  {Note}";
}

/// <summary>A voice ready to speak: text in, samples out.</summary>
interface ISpeechVoice : IDisposable
{
    int SampleRate { get; }
    /// <summary>Running on the graphics card (asked for, and the card could do it).</summary>
    bool OnGpu { get; }
    /// <summary>One sentence (already written out as it is read: see <see cref="SpeechText.Spell"/>), as mono samples.</summary>
    float[] Speak(string sentence, double speed);
}

/// <summary>
/// The English voices of the two engines. Piper: light and very fast, a voice is one small model. Kokoro: more
/// natural, slower; one model (325 MB) for all its voices, each a small file. Everything is downloaded on demand
/// from Hugging Face into the app's data folder.
/// </summary>
static class SpeechVoices
{
    const long Medium = 63_201_294;
    public static readonly SpeechVoiceInfo[] All =
    [
        new(SpeechEngine.Piper, "en_US-lessac-high", "Lessac", "US, female, best quality", 113_895_201, "99d1f6181a7f5ccbe3f117ba8ce63c93"),
        new(SpeechEngine.Piper, "en_US-lessac-medium", "Lessac (lighter)", "US, female, faster", Medium, "2fc642b535197b6305c7c8f92dc8b24f"),
        new(SpeechEngine.Piper, "en_US-ryan-high", "Ryan", "US, male, best quality", 120_786_792, "5d879a17bddf5007f76655b445ba78b4"),
        new(SpeechEngine.Piper, "en_US-amy-medium", "Amy", "US, female", Medium, "778d28aeb95fcdf8a882344d9df142fc"),
        new(SpeechEngine.Piper, "en_US-hfc_female-medium", "HFC female", "US, female", Medium, "7abec91f1d6e19e913fbc4a333f62787"),
        new(SpeechEngine.Piper, "en_US-hfc_male-medium", "HFC male", "US, male", Medium, "cd2fda1933f0653d3ddc85e5f30ebdd2"),
        new(SpeechEngine.Piper, "en_US-joe-medium", "Joe", "US, male", Medium, "74fd6a4dc39e0aa9dce145d7f5acd4f6"),
        new(SpeechEngine.Piper, "en_US-kristin-medium", "Kristin", "US, female", 63_531_379, "5fed42d2296baca042e2bf74785db725"),
        new(SpeechEngine.Piper, "en_US-norman-medium", "Norman", "US, male", 63_531_379, "829cea515dc724d694b83b71e8083f9f"),
        new(SpeechEngine.Piper, "en_GB-cori-high", "Cori", "British, female, best quality", 114_219_352, "3474a80133d9a03e6870d2ac42c18806"),
        new(SpeechEngine.Piper, "en_GB-alan-medium", "Alan", "British, male", Medium, "8f6b35eeb8ef6269021c6cb6d2414c9b"),
        new(SpeechEngine.Piper, "en_GB-jenny_dioco-medium", "Jenny", "British, female", Medium, "d08f2f7edf0c858275a7eca74ff2a9e4"),
        new(SpeechEngine.Piper, "en_GB-northern_english_male-medium", "Northern English", "British, male", Medium, "4c9a9735bfb76ad67c8b31b23d6840a0"),
        new(SpeechEngine.Piper, "en_GB-alba-medium", "Alba", "Scottish, female", Medium, "c07f313752bb3aba8061041666251654"),

        Kokoro("af_heart", "Heart", "US, female, the best one"), Kokoro("af_bella", "Bella", "US, female, warm"),
        Kokoro("af_nicole", "Nicole", "US, female, soft"), Kokoro("af_sarah", "Sarah", "US, female"), Kokoro("af_aoede", "Aoede", "US, female"),
        Kokoro("af_kore", "Kore", "US, female"), Kokoro("af_nova", "Nova", "US, female"), Kokoro("af_sky", "Sky", "US, female"),
        Kokoro("am_michael", "Michael", "US, male"), Kokoro("am_fenrir", "Fenrir", "US, male, deep"), Kokoro("am_puck", "Puck", "US, male"),
        Kokoro("am_adam", "Adam", "US, male"), Kokoro("am_eric", "Eric", "US, male"), Kokoro("am_onyx", "Onyx", "US, male"),
        Kokoro("bf_emma", "Emma", "British, female"), Kokoro("bf_isabella", "Isabella", "British, female"), Kokoro("bf_alice", "Alice", "British, female"),
        Kokoro("bm_george", "George", "British, male"), Kokoro("bm_fable", "Fable", "British, male"), Kokoro("bm_lewis", "Lewis", "British, male"),
        Kokoro("bm_daniel", "Daniel", "British, male"),
    ];

    static SpeechVoiceInfo Kokoro(string id, string name, string note) => new(SpeechEngine.Kokoro, id, name, note, KokoroVoice.VoiceBytes);

    public static IEnumerable<SpeechVoiceInfo> Of(SpeechEngine engine) => All.Where(v => v.Engine == engine);

    public static SpeechVoiceInfo? Find(SpeechEngine engine, string? id) => All.FirstOrDefault(v => v.Engine == engine && v.Id == id);

    static string PiperFolder => Path.Combine(SpeechRuntime.Root, "piper");

    public static string FileOf(SpeechVoiceInfo voice) => voice.Engine == SpeechEngine.Piper
        ? Path.Combine(PiperFolder, voice.Id + ".onnx")
        : Path.Combine(KokoroVoice.Folder, "voices", voice.Id + ".bin");

    /// <summary>The voice (and, for Kokoro, its model) is on this PC.</summary>
    public static bool IsDownloaded(SpeechVoiceInfo voice) => voice.Engine == SpeechEngine.Piper
        ? File.Exists(FileOf(voice)) && File.Exists(FileOf(voice) + ".json")
        : File.Exists(FileOf(voice)) && KokoroVoice.IsModelInstalled;

    /// <summary>What <see cref="DownloadAsync"/> would download for this voice now, in bytes (0: nothing).</summary>
    public static long MissingBytes(SpeechVoiceInfo voice)
    {
        if (voice.Engine == SpeechEngine.Piper) return IsDownloaded(voice) ? 0 : voice.Bytes;
        return (File.Exists(FileOf(voice)) ? 0 : voice.Bytes) + (KokoroVoice.IsModelInstalled ? 0 : KokoroVoice.ModelBytes);
    }

    /// <summary>Downloads what the voice lacks, checking each file; reports the bytes so far, of <see cref="MissingBytes"/>.</summary>
    public static async Task DownloadAsync(SpeechVoiceInfo voice, IProgress<long> progress, CancellationToken ct)
    {
        using var http = SpeechRuntime.NewClient();
        if (voice.Engine == SpeechEngine.Piper)
        {
            if (IsDownloaded(voice)) return;
            Directory.CreateDirectory(PiperFolder);
            // "en_US-lessac-high" lives in en/en_US/lessac/high/
            var parts = voice.Id.Split('-');
            string url = $"https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/{parts[0]}/{parts[1]}/{parts[2]}/{voice.Id}.onnx";
            var model = FileOf(voice);
            await SpeechRuntime.FetchAsync(http, url + ".json", model + ".json.part", null, ct);
            try
            {
                await SpeechRuntime.FetchAsync(http, url, model + ".part", progress, ct);
                await using (var check = File.OpenRead(model + ".part"))
                    if (check.Length != voice.Bytes || (voice.Md5 != null && !Convert.ToHexString(await MD5.HashDataAsync(check, ct)).Equals(voice.Md5, StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("The downloaded voice is not the expected one (checksum mismatch). Please try again.");
                File.Move(model + ".part", model, overwrite: true);
                File.Move(model + ".json.part", model + ".json", overwrite: true);
            }
            finally
            {
                if (File.Exists(model + ".part")) File.Delete(model + ".part");
                if (File.Exists(model + ".json.part")) File.Delete(model + ".json.part");
            }
            return;
        }
        long done = 0;
        if (!KokoroVoice.IsModelInstalled)
        {
            await KokoroVoice.DownloadModelAsync(http, progress, ct);
            done = KokoroVoice.ModelBytes;
        }
        var file = FileOf(voice);
        if (!File.Exists(file))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            try
            {
                await SpeechRuntime.FetchAsync(http, $"{KokoroVoice.Repository}/voices/{voice.Id}.bin", file + ".part", new Progress<long>(b => progress.Report(done + b)), ct);
                if (new FileInfo(file + ".part").Length != voice.Bytes) throw new IOException("The downloaded voice is not the expected one. Please try again.");
                File.Move(file + ".part", file, overwrite: true);
            }
            finally
            {
                if (File.Exists(file + ".part")) File.Delete(file + ".part");
            }
        }
    }

    /// <summary>Loads the voice (the runtime and the voice must be downloaded). Slow: off the UI thread.</summary>
    public static ISpeechVoice Open(SpeechVoiceInfo voice, bool gpu) =>
        voice.Engine == SpeechEngine.Piper ? new PiperVoice(FileOf(voice), gpu) : new KokoroVoice(FileOf(voice), voice.British, gpu);
}
