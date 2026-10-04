using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Onnx;

namespace aBookPlayer;

/// <summary>
/// A Kokoro voice (Kokoro-82M v1.0): one model for all the voices, given the phonemes in Kokoro's own alphabet and a
/// "style" taken from the voice's file. 24 kHz.
/// </summary>
sealed partial class KokoroVoice : ISpeechVoice
{
    public const string Repository = "https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/main";
    const string ModelSha256 = "8fbea51ea711f2af382e88c833d9e288c6dc82ce5e98421ea61c058ce21a34cb";
    public const long ModelBytes = 325_532_232;
    public const long VoiceBytes = 522_240; // 510 styles of 256 numbers
    const int MaxTokens = 510, StyleSize = 256;

    public static string Folder => Path.Combine(SpeechRuntime.Root, "kokoro");
    static string ModelFile => Path.Combine(Folder, "kokoro-v1.0.onnx");
    static string VocabularyFile => Path.Combine(Folder, "tokenizer.json");
    public static bool IsModelInstalled => File.Exists(ModelFile) && File.Exists(VocabularyFile);

    /// <summary>
    /// Downloads the model (checked against its SHA-256) and rewrites the three nodes DirectML cannot run, so the
    /// same file works on the processor and on the graphics card.
    /// </summary>
    public static async Task DownloadModelAsync(HttpClient http, IProgress<long> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var original = ModelFile + ".download";
        try
        {
            await SpeechRuntime.FetchAsync(http, Repository + "/tokenizer.json", VocabularyFile + ".part", null, ct);
            await SpeechRuntime.FetchAsync(http, Repository + "/onnx/model.onnx", original, progress, ct);
            await using (var check = File.OpenRead(original))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(check, ct)).Equals(ModelSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The downloaded Kokoro model is not the expected one (checksum mismatch). Please try again.");
            await Task.Run(() => Patch(original, ModelFile + ".part"), ct);
            File.Move(ModelFile + ".part", ModelFile, overwrite: true);
            File.Move(VocabularyFile + ".part", VocabularyFile, overwrite: true);
        }
        finally
        {
            foreach (var leftover in new[] { original, ModelFile + ".part", VocabularyFile + ".part" })
                if (File.Exists(leftover)) File.Delete(leftover);
        }
    }

    /// <summary>
    /// DirectML refuses the model's three depthwise transposed convolutions (stride 2: they double the length of
    /// a sequence). Each becomes what it is underneath: a zero put after every sample, then an ordinary depthwise
    /// convolution with the kernel mirrored. The result is the same to the last bit on the processor.
    /// </summary>
    internal static int Patch(string source, string target)
    {
        ModelProto model;
        using (var input = File.OpenRead(source)) model = ModelProto.Parser.ParseFrom(input);
        var graph = model.Graph;
        int rewritten = 0;
        foreach (var node in graph.Node.Where(n => n.OpType == "ConvTranspose").ToList())
        {
            long[] Ints(string name) => node.Attribute.FirstOrDefault(a => a.Name == name)?.Ints.ToArray() ?? [];
            long group = node.Attribute.FirstOrDefault(a => a.Name == "group")?.I ?? 1;
            var weight = graph.Initializer.FirstOrDefault(i => i.Name == node.Input[1]);
            if (group <= 1 || weight == null || weight.RawData.Length == 0 || weight.Dims.Count != 3 || weight.Dims[1] != 1) continue;
            if (Ints("kernel_shape") is not [var kernel] || Ints("strides") is not [2] || Ints("pads") is not [var pad, _]) continue;
            long outputPadding = Ints("output_padding") is [var extra] ? extra : 0;

            // The kernel of each channel, mirrored
            var w = weight.RawData.ToByteArray();
            int channels = (int)weight.Dims[0], k = (int)kernel, size = w.Length / (channels * k);
            var mirrored = new byte[w.Length];
            for (int c = 0; c < channels; c++)
                for (int i = 0; i < k; i++) Array.Copy(w, (c * k + (k - 1 - i)) * size, mirrored, (c * k + i) * size, size);
            string id = node.Name.Replace('/', '_');
            var mirroredWeight = new TensorProto { Name = id + "_mirrored", DataType = weight.DataType, RawData = ByteString.CopyFrom(mirrored) };
            mirroredWeight.Dims.AddRange(weight.Dims);
            graph.Initializer.Add(
            [
                mirroredWeight, Int64s(id + "_axes", 3), Int64s(id + "_shape", 0, 0, -1),
                new TensorProto { Name = id + "_zero", DataType = weight.DataType, RawData = ByteString.CopyFrom(new byte[size]) },
            ]);

            // [N,C,L] → [N,C,L,1] → a zero after every sample [N,C,L,2] → [N,C,2L] → depthwise convolution
            long left = kernel - 1 - pad, right = kernel - 1 - pad + outputPadding - 1;
            NodeProto[] replacement =
            [
                Make("Unsqueeze", [node.Input[0], id + "_axes"], id + "_u"),
                Make("Mul", [id + "_u", id + "_zero"], id + "_z"),
                Make("Concat", [id + "_u", id + "_z"], id + "_c", Int("axis", 3)),
                Make("Reshape", [id + "_c", id + "_shape"], id + "_s"),
                Make("Conv", node.Input.Count > 2 ? [id + "_s", id + "_mirrored", node.Input[2]] : [id + "_s", id + "_mirrored"], node.Output[0],
                    Int("group", group), IntList("kernel_shape", kernel), IntList("strides", 1), IntList("pads", left, right), IntList("dilations", 1)),
            ];
            int at = graph.Node.IndexOf(node);
            graph.Node.RemoveAt(at);
            for (int i = 0; i < replacement.Length; i++) graph.Node.Insert(at + i, replacement[i]);
            rewritten++;

            NodeProto Make(string type, string[] inputs, string output, params AttributeProto[] attributes)
            {
                var made = new NodeProto { OpType = type, Name = $"{id}_{type}" };
                made.Input.AddRange(inputs);
                made.Output.Add(output);
                made.Attribute.AddRange(attributes);
                return made;
            }
        }
        using (var output = File.Create(target)) model.WriteTo(output);
        return rewritten;

        static TensorProto Int64s(string name, params long[] values)
        {
            var tensor = new TensorProto { Name = name, DataType = (int)TensorProto.Types.DataType.Int64, RawData = ByteString.CopyFrom(MemoryMarshal.AsBytes(values.AsSpan())) };
            tensor.Dims.Add(values.Length);
            return tensor;
        }
        static AttributeProto Int(string name, long value) => new() { Name = name, Type = AttributeProto.Types.AttributeType.Int, I = value };
        static AttributeProto IntList(string name, params long[] values)
        {
            var attribute = new AttributeProto { Name = name, Type = AttributeProto.Types.AttributeType.Ints };
            attribute.Ints.AddRange(values);
            return attribute;
        }
    }

    readonly InferenceSession _session;
    readonly Dictionary<string, long> _vocabulary;
    readonly float[] _styles;
    readonly bool _british;
    readonly string _tokensInput;

    public int SampleRate => 24000;
    public bool OnGpu { get; }

    public KokoroVoice(string voiceFile, bool british, bool gpu)
    {
        _british = british;
        using (var tokenizer = JsonDocument.Parse(File.ReadAllText(VocabularyFile)))
            _vocabulary = tokenizer.RootElement.GetProperty("model").GetProperty("vocab").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64());
        _styles = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(voiceFile)).ToArray();
        _session = SpeechRuntime.Open(ModelFile, gpu, out bool onGpu);
        _tokensInput = _session.InputMetadata.Keys.FirstOrDefault(k => k.Contains("input") || k.Contains("token")) ?? "input_ids";
        if (onGpu)
        {
            // A card may accept the model and then fail to run it: found out now, not in the middle of a book
            try { Run(Tokens("hɛlO."), 1); }
            catch (OnnxRuntimeException)
            {
                _session.Dispose();
                _session = SpeechRuntime.Open(ModelFile, gpu: false, out onGpu);
            }
        }
        OnGpu = onGpu;
    }

    public float[] Speak(string sentence, double speed)
    {
        // The clauses in Kokoro's alphabet, each followed by its mark; the model takes 510 phonemes at a time, so a
        // very long sentence is spoken in groups of clauses
        var audio = new List<float>();
        var group = new StringBuilder();
        foreach (var clause in Espeak.Phonemes(sentence, _british ? "en-gb" : "en-us", tie: true))
        {
            string piece = ToKokoro(clause.Phonemes, _british) + clause.Terminator + " ";
            if (group.Length + piece.Length > MaxTokens - 10 && group.Length > 0)
            {
                audio.AddRange(Run(Tokens(group.ToString().TrimEnd()), speed));
                group.Clear();
            }
            group.Append(piece);
        }
        if (group.Length > 0) audio.AddRange(Run(Tokens(group.ToString().TrimEnd()), speed));
        return audio.ToArray();
    }

    List<long> Tokens(string phonemes) =>
        phonemes.EnumerateRunes().Select(r => _vocabulary.TryGetValue(r.ToString(), out var id) ? id : -1).Where(id => id >= 0).Take(MaxTokens).ToList();

    float[] Run(List<long> tokens, double speed)
    {
        if (tokens.Count == 0) return [];
        // Padded with 0 at both ends; the style depends on how many phonemes there are
        var ids = new long[tokens.Count + 2];
        tokens.CopyTo(ids, 1);
        var style = new float[StyleSize];
        Array.Copy(_styles, Math.Min(tokens.Count, _styles.Length / StyleSize - 1) * StyleSize, style, 0, StyleSize);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_tokensInput, new DenseTensor<long>(ids, [1, ids.Length])),
            NamedOnnxValue.CreateFromTensor("style", new DenseTensor<float>(style, [1, StyleSize])),
            NamedOnnxValue.CreateFromTensor("speed", new DenseTensor<float>(new[] { (float)speed }, [1])),
        };
        using var result = _session.Run(inputs);
        return result[0].AsEnumerable<float>().ToArray();
    }

    [GeneratedRegex(@"(\S)̩")]
    private static partial Regex SyllabicRegex();

    // espeak-ng's phonemes to the ones Kokoro was trained on (its own G2P, "misaki", falls back to espeak-ng the
    // same way): one letter per diphthong and affricate, a few sounds it does not have brought to the nearest one
    static readonly (string From, string To)[] ToMisaki = new (string, string)[]
    {
        ("ʔˌn̩", "ʔn"), ("ʔn̩", "ʔn"), ("a^ɪ", "I"), ("a^ʊ", "W"), ("d^ʒ", "ʤ"), ("e^ɪ", "A"), ("t^ʃ", "ʧ"), ("ɔ^ɪ", "Y"), ("ə^l", "ᵊl"),
        ("ʲo", "jo"), ("ʲə", "jə"), ("ʲ", ""), ("ɚ", "əɹ"), ("r", "ɹ"), ("x", "k"), ("ç", "k"), ("ɐ", "ə"), ("ɬ", "l"), ("̃", ""), ("e", "A"),
    }.OrderByDescending(m => m.Item1.Length).ToArray();

    internal static string ToKokoro(string phonemes, bool british)
    {
        foreach (var (from, to) in ToMisaki) phonemes = phonemes.Replace(from, to);
        phonemes = SyllabicRegex().Replace(phonemes, "ᵊ$1").Replace("̩", "");
        phonemes = british
            ? phonemes.Replace("e^ə", "ɛː").Replace("iə", "ɪə").Replace("ə^ʊ", "Q")
            : phonemes.Replace("o^ʊ", "O").Replace("ɜːɹ", "ɜɹ").Replace("ɜː", "ɜɹ").Replace("ɪə", "iə").Replace("ː", "");
        return phonemes.Replace("o", "ɔ").Replace("ɾ", "T").Replace("ʔ", "t").Replace("^", "");
    }

    public void Dispose() => _session.Dispose();
}
