using NAudio.Wave;

namespace aBookPlayer;

/// <summary>
/// "Voice boost": a gentle compressor that lifts quiet passages and tames loud ones, so a whispering or
/// uneven narrator stays intelligible at a comfortable volume (or in a noisy place). Channels are linked, so
/// the stereo image does not move; a soft limiter keeps the made-up gain from clipping. When disabled, samples
/// pass through untouched.
/// </summary>
sealed class VoiceBoost(ISampleProvider source) : ISampleProvider
{
    const float ThresholdDb = -30f;   // compression starts here
    const float Ratio = 3f;
    const float MakeupDb = 10f;       // brings the compressed level back up
    const float AttackMs = 8f, ReleaseMs = 250f;

    readonly int _channels = source.WaveFormat.Channels;
    readonly float _attack = Coefficient(AttackMs, source.WaveFormat.SampleRate);
    readonly float _release = Coefficient(ReleaseMs, source.WaveFormat.SampleRate);
    readonly float _makeup = DbToGain(MakeupDb);
    float _envelopeDb = -90f;

    public WaveFormat WaveFormat => source.WaveFormat;

    public bool Enabled { get; set; }

    static float Coefficient(float ms, int rate) => (float)Math.Exp(-1.0 / (ms / 1000.0 * rate));
    static float DbToGain(float db) => (float)Math.Pow(10, db / 20);

    public int Read(float[] buffer, int offset, int count)
    {
        int n = source.Read(buffer, offset, count);
        if (!Enabled)
        {
            _envelopeDb = -90f;
            return n;
        }

        for (int i = offset; i + _channels <= offset + n; i += _channels)
        {
            // Peak of the frame (all channels), followed by an attack/release envelope in dB
            float peak = 0;
            for (int c = 0; c < _channels; c++) peak = Math.Max(peak, Math.Abs(buffer[i + c]));
            float levelDb = peak > 1e-6f ? 20f * MathF.Log10(peak) : -120f;
            float k = levelDb > _envelopeDb ? _attack : _release;
            _envelopeDb = k * _envelopeDb + (1 - k) * levelDb;

            float overDb = _envelopeDb - ThresholdDb;
            float gainDb = overDb > 0 ? -overDb * (1 - 1 / Ratio) : 0;
            float gain = DbToGain(gainDb) * _makeup;

            for (int c = 0; c < _channels; c++) buffer[i + c] = SoftLimit(buffer[i + c] * gain);
        }
        return n;
    }

    /// <summary>Linear up to ±0.8, then a smooth curve that never exceeds ±1.</summary>
    static float SoftLimit(float x)
    {
        const float knee = 0.8f;
        float a = Math.Abs(x);
        if (a <= knee) return x;
        float over = (a - knee) / (1 - knee);
        float limited = knee + (1 - knee) * over / (1 + over);
        return x < 0 ? -limited : limited;
    }
}
