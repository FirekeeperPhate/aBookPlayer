using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace aBookPlayer.Tests;

public class AudioTests
{
    const int Rate = 44100;

    /// <summary>Speech-like test signal: a tone whose pitch and loudness keep changing.</summary>
    static ISampleProvider Signal(double seconds, int channels = 2)
    {
        var total = (int)(seconds * Rate);
        var data = new float[total * channels];
        for (int i = 0; i < total; i++)
        {
            double t = i / (double)Rate;
            float v = (float)(0.3 * Math.Sin(2 * Math.PI * (180 + 60 * Math.Sin(t * 3)) * t) * (0.5 + 0.5 * Math.Sin(t * 7)));
            for (int c = 0; c < channels; c++) data[i * channels + c] = v;
        }
        return new ArraySampleProvider(data, channels);
    }

    static (long Frames, double EndSourceTime) Drain(TimeStretchSampleProvider stretch)
    {
        var buffer = new float[4096];
        long samples = 0;
        int n;
        while ((n = stretch.Read(buffer, 0, buffer.Length)) > 0) samples += n;
        return (samples / stretch.WaveFormat.Channels, stretch.NextSourceTime);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Time_stretch_changes_duration_and_keeps_source_time(double speed)
    {
        var stretch = new TimeStretchSampleProvider(Signal(10));
        stretch.Reset(0, speed);
        var (frames, end) = Drain(stretch);

        Assert.InRange(frames / (double)Rate, 10 / speed - 0.06, 10 / speed + 0.06);
        Assert.InRange(end, 9.95, 10.05);
    }

    [Fact]
    public void Time_stretch_at_1x_is_a_bit_exact_passthrough()
    {
        var reference = new float[Rate * 2];
        Signal(1).Read(reference, 0, reference.Length);
        var stretch = new TimeStretchSampleProvider(Signal(1));
        stretch.Reset(0, 1.0);
        var output = new float[reference.Length];
        int read = stretch.Read(output, 0, output.Length);

        Assert.Equal(reference.Length, read);
        Assert.Equal(reference, output);
    }

    [Fact]
    public void Time_stretch_reports_source_time_while_playing()
    {
        var stretch = new TimeStretchSampleProvider(Signal(10));
        stretch.Reset(3.0, 1.5); // as after a seek to 3 s
        var buffer = new float[Rate * 2 * 2];     // 2 s of stereo output
        stretch.Read(buffer, 0, buffer.Length);

        Assert.InRange(stretch.NextSourceTime, 3.0 + 3.0 - 0.03, 3.0 + 3.0 + 0.03); // 2 s at 1.5x = 3 s of source
    }

    [Fact]
    public void Downmix_5_1_to_stereo_uses_center_and_surrounds()
    {
        // FL FR FC LFE BL BR
        var input = new ArraySampleProvider([1f, 0f, 0.5f, 0.9f, 0.2f, 0f], 6);
        var output = new float[2];
        new DownmixToStereo(input).Read(output, 0, 2);

        const float k = 0.7071f, norm = 1 + 2 * k;
        Assert.Equal((1 + 0.5f * k + 0.2f * k) / norm, output[0], 4);
        Assert.Equal((0 + 0.5f * k + 0) / norm, output[1], 4);  // LFE is ignored
    }

    [Fact]
    public void Downmix_to_mono_averages_channels()
    {
        var output = new float[2];
        new DownmixToMono(new ArraySampleProvider([1f, 0f, 0.5f, 0.5f], 2)).Read(output, 0, 2);
        Assert.Equal([0.5f, 0.5f], output);
    }

    [Theory]
    [InlineData(16, 2)]
    [InlineData(24, 2)]
    [InlineData(16, 6)]
    public void Wave_format_extensible_files_open(int bits, int channels)
    {
        if (!MediaFoundationAvailable()) Assert.Skip("Media Foundation is not installed (e.g. Windows Server without the feature)");
        var path = TestFiles.TempPath(".wav");
        using (var writer = new WaveFileWriter(path, new WaveFormatExtensible(Rate, bits, channels)))
        {
            var buffer = new float[Rate * channels];
            Signal(1, channels).Read(buffer, 0, buffer.Length);
            writer.WriteSamples(buffer, 0, buffer.Length);
        }

        using var stream = AudioDecoder.Open(path);
        var samples = new SampleChannel(stream);
        var probe = new float[Rate * channels * 2];
        int total = 0, n;
        while ((n = samples.Read(probe, 0, probe.Length)) > 0) total += n;
        Assert.InRange(total / (double)(Rate * channels), 0.99, 1.01);
    }

    static bool MediaFoundationAvailable()
    {
        try { MediaFoundationApi.Startup(); return true; }
        catch { return false; }
    }

    internal sealed class ArraySampleProvider(float[] data, int channels) : ISampleProvider
    {
        int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, channels);
        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }
}
