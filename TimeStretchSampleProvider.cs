using NAudio.Wave;

namespace aBookPlayer;

/// <summary>
/// Changes playback speed without changing pitch, using WSOLA (waveform-similarity overlap-add):
/// 40 ms Hann-windowed frames are taken from the source every <c>hop × rate</c> samples and
/// overlap-added every <c>hop</c> samples; each frame is shifted by up to ±10 ms to the position
/// that best continues the previous one, which avoids the "phasey" artifacts of plain overlap-add.
/// It also reports the source time of the next output sample, so the player can keep subtitles in sync.
/// At rate 1 the audio passes through untouched.
/// </summary>
sealed class TimeStretchSampleProvider : ISampleProvider
{
    readonly ISampleProvider _source;
    readonly int _channels, _sampleRate, _frameLength, _hop, _tolerance;
    readonly float[] _window;
    readonly float[] _accum;
    readonly float[] _hopOut;

    double _rate = 1.0;
    double _sourceTime;         // source time (s) of the next output sample

    float[] _input = new float[16384];
    int _inputFrames;           // frames buffered in _input (including end-of-stream padding)
    int _realFrames;            // of which real audio (the rest is silence padded at the end)
    double _inputStartTime;     // source time (s) of _input[0]
    double _analysisPos;        // ideal position (frames into _input) of the next frame
    bool _started;              // at least one frame produced since the last reset
    int _natural;               // natural continuation of the previous frame (frames into _input)
    int _hopCount, _hopRead;    // emitted hop: samples available / already read
    double _hopSourceTime;      // source time of the first sample of the emitted hop
    bool _sourceEnded;

    public TimeStretchSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        _sampleRate = source.WaveFormat.SampleRate;
        _frameLength = (int)(_sampleRate * 0.04) & ~1;
        _hop = _frameLength / 2;
        _tolerance = _sampleRate / 100;
        // Periodic Hann window: at 50% overlap the windows sum to exactly 1
        _window = new float[_frameLength];
        for (int i = 0; i < _frameLength; i++)
            _window[i] = (float)Math.Pow(Math.Sin(Math.PI * i / _frameLength), 2);
        _accum = new float[_frameLength * _channels];
        _hopOut = new float[_hop * _channels];
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public double Rate => _rate;

    /// <summary>Source time (seconds) of the next sample this provider will output.</summary>
    public double NextSourceTime => _sourceTime;

    /// <summary>Restarts from <paramref name="sourceTime"/> (the source must already be positioned there).</summary>
    public void Reset(double sourceTime, double rate)
    {
        _rate = rate;
        _sourceTime = _inputStartTime = sourceTime;
        _inputFrames = _realFrames = 0;
        _analysisPos = 0;
        _started = false;
        _natural = 0;
        _hopCount = _hopRead = 0;
        _sourceEnded = false;
        Array.Clear(_accum);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_rate == 1.0 && !_started)
        {
            int n = _source.Read(buffer, offset, count);
            _sourceTime += n / (double)(_channels * _sampleRate);
            return n;
        }

        int written = 0;
        while (written < count)
        {
            if (_hopRead >= _hopCount && !ProduceHop()) break;
            int take = Math.Min(count - written, _hopCount - _hopRead);
            // Element-wise on purpose: NAudio may hand over a byte[] aliased as float[] (WaveBuffer),
            // which Array.Copy and spans reject
            for (int i = 0; i < take; i++) buffer[offset + written + i] = _hopOut[_hopRead + i];
            _hopRead += take;
            written += take;
            _sourceTime = _hopSourceTime + (_hopRead / _channels) * _rate / _sampleRate;
        }
        return written;
    }

    bool ProduceHop()
    {
        int ideal = (int)Math.Round(_analysisPos);
        int needed = ideal + _tolerance + _frameLength;
        if (_started) needed = Math.Max(needed, _natural + _hop);
        Fill(needed);
        if (_sourceEnded && ideal >= _realFrames) return false;   // all real audio has been output
        if (_inputFrames < needed)
        {
            if (!_sourceEnded) return false;
            EnsureCapacity(needed);                     // end of stream: pad with silence
            Array.Clear(_input, _inputFrames * _channels, (needed - _inputFrames) * _channels);
            _inputFrames = needed;
        }

        int chosen = _started ? BestMatch(ideal, _natural) : ideal;

        for (int i = 0; i < _frameLength; i++)
        {
            float w = _window[i];
            int src = (chosen + i) * _channels, dst = i * _channels;
            for (int c = 0; c < _channels; c++) _accum[dst + c] += _input[src + c] * w;
        }

        int hopSamples = _hop * _channels;
        Array.Copy(_accum, _hopOut, hopSamples);
        Array.Copy(_accum, hopSamples, _accum, 0, _accum.Length - hopSamples);
        Array.Clear(_accum, _accum.Length - hopSamples, hopSamples);
        _hopCount = hopSamples;
        _hopRead = 0;
        _hopSourceTime = _inputStartTime + ideal / (double)_sampleRate;

        _started = true;
        _natural = chosen + _hop;
        _analysisPos += _hop * _rate;

        // Drop input that no future frame or comparison can reach
        int drop = Math.Max(0, Math.Min(_natural, (int)Math.Floor(_analysisPos) - _tolerance));
        if (drop > 0)
        {
            Array.Copy(_input, drop * _channels, _input, 0, (_inputFrames - drop) * _channels);
            _inputFrames -= drop;
            _realFrames = Math.Max(0, _realFrames - drop);
            _natural -= drop;
            _analysisPos -= drop;
            _inputStartTime += drop / (double)_sampleRate;
        }
        return true;
    }

    /// <summary>Offset within ±tolerance of <paramref name="ideal"/> whose start best matches the natural continuation.</summary>
    int BestMatch(int ideal, int natural)
    {
        int from = Math.Max(0, ideal - _tolerance), to = ideal + _tolerance;
        int length = _hop;

        // Coarse search (every 4th candidate, every 4th sample), then refine around the best one
        int best = ideal;
        double bestScore = double.NegativeInfinity;
        for (int cand = from; cand <= to; cand += 4)
        {
            double score = Similarity(cand, natural, length, 4);
            if (score > bestScore) { bestScore = score; best = cand; }
        }
        int coarse = best;
        for (int cand = Math.Max(from, coarse - 3); cand <= Math.Min(to, coarse + 3); cand++)
        {
            double score = Similarity(cand, natural, length, 1);
            if (score > bestScore) { bestScore = score; best = cand; }
        }
        return best;
    }

    /// <summary>Normalized cross-correlation of two mono-downmixed segments.</summary>
    double Similarity(int a, int b, int length, int step)
    {
        double dot = 0, energy = 1e-9;
        for (int i = 0; i < length; i += step)
        {
            float x = 0, y = 0;
            int ia = (a + i) * _channels, ib = (b + i) * _channels;
            for (int c = 0; c < _channels; c++) { x += _input[ia + c]; y += _input[ib + c]; }
            dot += x * y;
            energy += x * x;
        }
        return dot / Math.Sqrt(energy);
    }

    void Fill(int frames)
    {
        while (!_sourceEnded && _inputFrames < frames)
        {
            int want = Math.Max(frames - _inputFrames, 4096);
            EnsureCapacity(_inputFrames + want);
            int n = _source.Read(_input, _inputFrames * _channels, want * _channels);
            if (n <= 0) _sourceEnded = true;
            else _realFrames = _inputFrames += n / _channels;
        }
    }

    void EnsureCapacity(int frames)
    {
        if (_input.Length >= frames * _channels) return;
        Array.Resize(ref _input, Math.Max(frames * _channels, _input.Length * 2));
    }
}
