using NAudio.Wave;

namespace aBookPlayer;

/// <summary>
/// Downmixes multichannel audio (e.g. 5.1 AAC) to stereo: waveOut typically rejects more than two
/// float channels. Uses the standard ITU coefficients for 5.1/7.1 (FL FR FC LFE BL BR [SL SR]);
/// other layouts are split into even (left) and odd (right) channels.
/// Writes element-wise because the destination may be a byte[] aliased as float[] by NAudio.
/// </summary>
sealed class DownmixToStereo(ISampleProvider source) : ISampleProvider
{
    const float Side = 0.7071f;
    readonly int _channels = source.WaveFormat.Channels;
    float[] _buffer = [];

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        int needed = frames * _channels;
        if (_buffer.Length < needed) _buffer = new float[needed];
        int read = source.Read(_buffer, 0, needed) / _channels;

        for (int f = 0; f < read; f++)
        {
            int i = f * _channels;
            float left, right;
            if (_channels >= 6)
            {
                float center = _buffer[i + 2] * Side;
                float rearLeft = _buffer[i + 4], rearRight = _buffer[i + 5];
                if (_channels >= 8) { rearLeft = (rearLeft + _buffer[i + 6]) * Side; rearRight = (rearRight + _buffer[i + 7]) * Side; }
                else { rearLeft *= Side; rearRight *= Side; }
                // Normalized so a full-scale signal on every channel does not clip
                left = (_buffer[i] + center + rearLeft) / (1 + 2 * Side);
                right = (_buffer[i + 1] + center + rearRight) / (1 + 2 * Side);
            }
            else
            {
                left = right = 0;
                for (int c = 0; c < _channels; c += 2) left += _buffer[i + c];
                for (int c = 1; c < _channels; c += 2) right += _buffer[i + c];
                left /= (_channels + 1) / 2;
                right /= _channels / 2;
            }
            buffer[offset + 2 * f] = left;
            buffer[offset + 2 * f + 1] = right;
        }
        return read * 2;
    }
}

/// <summary>Averages all channels into one (stereo or multichannel), e.g. for speech recognition.</summary>
sealed class DownmixToMono(ISampleProvider source) : ISampleProvider
{
    readonly int _channels = source.WaveFormat.Channels;
    float[] _buffer = [];

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

    public int Read(float[] buffer, int offset, int count)
    {
        int needed = count * _channels;
        if (_buffer.Length < needed) _buffer = new float[needed];
        int frames = source.Read(_buffer, 0, needed) / _channels;
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < _channels; c++) sum += _buffer[f * _channels + c];
            buffer[offset + f] = sum / _channels;
        }
        return frames;
    }
}
