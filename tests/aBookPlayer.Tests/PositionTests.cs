using NAudio.Wave;

namespace aBookPlayer.Tests;

/// <summary>
/// The audible position comes from the sound card's 32-bit byte counter. These tests reproduce what
/// happens across standby or a device change: the counter restarts from zero (or jumps), and the
/// position must stay where the listener was instead of jumping to the end of the book.
/// </summary>
public class PositionTests
{
    const long BytesPerSecond = 44100 * 2 * 4; // 44.1 kHz stereo float

    [Fact]
    public void Counter_restart_after_standby_keeps_the_count_continuous()
    {
        var counter = new PlaybackCounter();
        counter.Reset(1_000);
        Assert.Equal(30 * BytesPerSecond, counter.Update(1_000 + 30 * BytesPerSecond));

        // Standby: the device counter restarts from 0, then keeps counting
        Assert.Equal(30 * BytesPerSecond, counter.Update(0));
        Assert.Equal(31 * BytesPerSecond, counter.Update(BytesPerSecond));
        // (the old logic treated the restart as a 32-bit wrap: +4 GB = +3.4 hours of audio)
    }

    [Fact]
    public void Genuine_32_bit_wrap_is_still_handled()
    {
        var counter = new PlaybackCounter();
        counter.Reset(0);
        const long nearTop = (1L << 32) - 1_000;
        Assert.Equal(nearTop, counter.Update(nearTop));
        Assert.Equal(nearTop + 3_000, counter.Update(2_000)); // wrapped past 2^32
    }

    [Fact]
    public void Small_backward_glitch_does_not_move_the_count_back()
    {
        var counter = new PlaybackCounter();
        counter.Reset(0);
        counter.Update(10_000_000);
        Assert.Equal(10_000_000, counter.Update(9_000_000));
        Assert.Equal(10_000_500, counter.Update(9_000_500));
    }

    [Fact]
    public void Position_never_runs_ahead_of_the_audio_delivered_to_the_device()
    {
        using var stream = FloatStream(seconds: 60);
        var stretch = new TimeStretchSampleProvider(stream.ToSampleProvider());
        stretch.Reset(0, 1.0);
        var tracker = new AudioPlayer.TrackingSampleProvider(stream, stretch);

        var buffer = new float[44100 * 2];            // the device has received 1 s of audio
        tracker.Read(buffer, 0, buffer.Length);

        Assert.Equal(0.5, tracker.TimeAt(44100).TotalSeconds, 3);        // normal case: half of it played
        var bogus = tracker.TimeAt(long.MaxValue / 4);                   // a crazy counter value
        Assert.InRange(bogus.TotalSeconds, 0.99, 1.01);                  // capped at what was delivered, not 60 s
    }

    [Fact]
    public void Position_is_exact_after_a_counter_restart_mid_book()
    {
        using var stream = FloatStream(seconds: 60);
        var stretch = new TimeStretchSampleProvider(stream.ToSampleProvider());
        stretch.Reset(0, 1.0);
        var tracker = new AudioPlayer.TrackingSampleProvider(stream, stretch);
        var buffer = new float[44100 * 2];
        for (int i = 0; i < 20; i++) tracker.Read(buffer, 0, buffer.Length); // 20 s delivered

        var counter = new PlaybackCounter();
        counter.Reset(0);
        long samples(long raw) => counter.Update(raw) / 4;
        Assert.Equal(19.0, tracker.TimeAt(samples(19 * BytesPerSecond)).TotalSeconds, 2);
        // Standby: counter back to 0, then 0.5 s more is played
        tracker.TimeAt(samples(0));
        Assert.Equal(19.5, tracker.TimeAt(samples(BytesPerSecond / 2)).TotalSeconds, 2);
    }

    static RawSourceWaveStream FloatStream(int seconds)
    {
        var bytes = new byte[seconds * BytesPerSecond];
        return new RawSourceWaveStream(new MemoryStream(bytes), WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
    }
}
