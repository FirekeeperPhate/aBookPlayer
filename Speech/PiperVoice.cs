using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace aBookPlayer;

/// <summary>
/// A Piper voice: a VITS model given phoneme ids, with the voice's own table of them (in the ".onnx.json" beside
/// the model, which also says the sample rate and how the voice likes to be run).
/// </summary>
sealed class PiperVoice : ISpeechVoice
{
    readonly InferenceSession _session;
    readonly Dictionary<string, long[]> _ids;
    readonly string _espeakVoice;
    readonly float _noise, _lengthScale, _noiseWidth;
    readonly bool _manySpeakers;

    public int SampleRate { get; }
    public bool OnGpu { get; }

    public PiperVoice(string model, bool gpu)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(model + ".json"));
        var root = config.RootElement;
        SampleRate = root.GetProperty("audio").GetProperty("sample_rate").GetInt32();
        _espeakVoice = root.GetProperty("espeak").GetProperty("voice").GetString() ?? "en-us";
        var inference = root.GetProperty("inference");
        _noise = inference.GetProperty("noise_scale").GetSingle();
        _lengthScale = inference.GetProperty("length_scale").GetSingle();
        _noiseWidth = inference.GetProperty("noise_w").GetSingle();
        _manySpeakers = root.TryGetProperty("num_speakers", out var speakers) && speakers.GetInt32() > 1;
        _ids = root.GetProperty("phoneme_id_map").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(v => v.GetInt64()).ToArray());
        _session = SpeechRuntime.Open(model, gpu, out bool onGpu);
        OnGpu = onGpu;
    }

    public float[] Speak(string sentence, double speed)
    {
        // The clauses' phonemes, each followed by the mark that ended it ("ðə kˈæt, ɪt sˈɛd.")
        var phonemes = new StringBuilder();
        foreach (var clause in Espeak.Phonemes(sentence, _espeakVoice, tie: false))
            phonemes.Append(clause.Phonemes).Append(clause.Terminator).Append(' ');
        if (phonemes.Length == 0) return [];

        // "^", then every phoneme followed by the pad "_", then "$"
        var ids = new List<long>(phonemes.Length * 2 + 3);
        ids.AddRange(_ids["^"]);
        ids.AddRange(_ids["_"]);
        foreach (var rune in phonemes.ToString().TrimEnd().EnumerateRunes())
        {
            if (!_ids.TryGetValue(rune.ToString(), out var id)) continue; // a sound this voice does not have
            ids.AddRange(id);
            ids.AddRange(_ids["_"]);
        }
        ids.AddRange(_ids["$"]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<long>(ids.ToArray(), [1, ids.Count])),
            NamedOnnxValue.CreateFromTensor("input_lengths", new DenseTensor<long>(new long[] { ids.Count }, [1])),
            // A longer "length" is slower speech
            NamedOnnxValue.CreateFromTensor("scales", new DenseTensor<float>(new[] { _noise, (float)(_lengthScale / speed), _noiseWidth }, [3])),
        };
        if (_manySpeakers) inputs.Add(NamedOnnxValue.CreateFromTensor("sid", new DenseTensor<long>(new long[] { 0 }, [1])));
        using var result = _session.Run(inputs);
        return result[0].AsEnumerable<float>().ToArray();
    }

    public void Dispose() => _session.Dispose();
}
