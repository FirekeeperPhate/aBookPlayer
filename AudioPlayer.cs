using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace aBookPlayer;

/// <summary>
/// Turns the waveOut byte counter into a monotonic count of bytes played. The counter is 32-bit, so it
/// wraps after ~3.4 hours of continuous playback; but it can also restart from zero or jump back after
/// standby, a driver reset or a device change. Treating every backward step as a wrap (as 1.0–1.2 did)
/// adds 4 GB ≈ 3.4 hours of audio and throws the position to the end of the book.
/// </summary>
sealed class PlaybackCounter
{
    const long Wrap = 1L << 32;
    long _last = -1, _offset;

    /// <summary>Starts counting from <paramref name="raw"/> (the counter value when playback starts).</summary>
    public void Reset(long raw)
    {
        _last = raw;
        _offset = -raw;
    }

    /// <summary>Bytes played since <see cref="Reset"/>, never decreasing.</summary>
    public long Update(long raw)
    {
        if (_last >= 0 && raw < _last)
        {
            // A genuine wrap goes from the top of the 32-bit range back to the bottom;
            // anything else is a restart/glitch: continue from where the count was
            bool wrapped = _last >= Wrap * 3 / 4 && raw < Wrap / 4;
            _offset += wrapped ? Wrap : _last - raw;
        }
        _last = raw;
        return Math.Max(0, raw + _offset);
    }
}

/// <summary>
/// Audio playback with NAudio. The reported position is what is actually audible
/// (samples already played by the sound card), not the decoder position, which runs a few
/// hundred ms ahead because of buffering: this keeps subtitles accurately in sync.
/// </summary>
sealed class AudioPlayer : IDisposable
{
    WaveOutEvent? _output;
    WaveStream? _stream;
    SampleChannel? _channel;
    TrackingSampleProvider? _tracker;
    float _volume = 0.8f;
    float _fade = 1f;
    double _speed = 1.0;
    readonly PlaybackCounter _counter = new();
    TimeSpan _lastKnownPosition;

    public event EventHandler? Ended;
    public event EventHandler<Exception>? Error;

    public bool IsLoaded => _stream != null;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public TimeSpan Duration => _stream?.TotalTime ?? TimeSpan.Zero;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            ApplyVolume();
        }
    }

    /// <summary>Extra 0–1 gain on top of <see cref="Volume"/> (sleep-timer fade-out), not part of the user's volume.</summary>
    public float Fade
    {
        get => _fade;
        set
        {
            _fade = Math.Clamp(value, 0f, 1f);
            ApplyVolume();
        }
    }

    void ApplyVolume()
    {
        if (_channel != null) _channel.Volume = _volume * _fade;
    }

    /// <summary>Playback speed (0.5–2.0) without pitch change. Position and subtitles stay in source time.</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            _speed = Math.Clamp(value, 0.5, 2.0);
            _tracker?.SetSpeed(_speed);
        }
    }

    public TimeSpan Position
    {
        get
        {
            if (_tracker == null) return TimeSpan.Zero;
            if (_output == null) return _lastKnownPosition;   // output lost (device error): keep the last position
            var t = _output.PlaybackState == PlaybackState.Stopped ? _tracker.RestTime : _tracker.TimeAt(PlayedSamples());
            if (t > Duration) t = Duration;
            _lastKnownPosition = t;
            return t;
        }
    }

    /// <summary>Loads an already-open stream (see <see cref="AudioFormats.Open"/>; opening can scan the whole file, so do it off the UI thread).</summary>
    /// <remarks>On failure the stream is disposed and the player is left unloaded (never half-initialized).</remarks>
    public void Load(WaveStream stream)
    {
        Unload();
        try
        {
            _stream = stream;
            _channel = new SampleChannel(stream) { Volume = _volume * _fade };
            ISampleProvider samples = _channel.WaveFormat.Channels > 2 ? new DownmixToStereo(_channel) : _channel;
            var stretch = new TimeStretchSampleProvider(samples);
            stretch.Reset(0, _speed);
            _tracker = new TrackingSampleProvider(stream, stretch);
            _lastKnownPosition = TimeSpan.Zero;
            CreateOutput();
        }
        catch
        {
            Unload();
            throw;
        }
    }

    void CreateOutput()
    {
        _output = new WaveOutEvent { DesiredLatency = 200, NumberOfBuffers = 3 };
        _output.PlaybackStopped += OnPlaybackStopped;
        _output.Init(_tracker);
    }

    void DisposeOutput()
    {
        if (_output == null) return;
        _output.PlaybackStopped -= OnPlaybackStopped;
        _output.Dispose();
        _output = null;
    }

    public void Play()
    {
        if (_tracker == null) return;
        if (_output == null)
        {
            // The device was lost earlier (e.g. across standby): try again with the current default device
            try { CreateOutput(); }
            catch (Exception ex) { DisposeOutput(); Error?.Invoke(this, ex); return; }
            _tracker.Seek(_lastKnownPosition);
        }
        if (_output!.PlaybackState == PlaybackState.Stopped)
        {
            // Fresh start: realign the sound card's counter with the provider's
            _counter.Reset(RawBytes());
            _tracker.ResetCounters();
        }
        _output.Play();
    }

    public void Pause()
    {
        if (IsPlaying) _output!.Pause();
    }

    public void Stop()
    {
        if (_tracker == null) return;
        _output?.Stop();
        _tracker.Seek(TimeSpan.Zero);
        _lastKnownPosition = TimeSpan.Zero;
    }

    public void Seek(TimeSpan time)
    {
        if (_tracker == null) return;
        var max = Duration - TimeSpan.FromMilliseconds(50);
        if (time > max) time = max;
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;

        // While paused, discard queued audio so playback resumes exactly at the chosen point
        if (_output?.PlaybackState == PlaybackState.Paused) _output.Stop();
        _tracker.Seek(time);
        _lastKnownPosition = time;
    }

    long RawBytes()
    {
        try { return _output!.GetPosition(); }
        catch { return 0; }
    }

    long PlayedSamples() =>
        _counter.Update(RawBytes()) / (_output!.OutputWaveFormat.BitsPerSample / 8);

    void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            // The device failed (unplugged, standby, driver reset): keep the position and drop the broken
            // output, so Play resumes from here on a fresh one instead of losing the place in the book
            var position = _lastKnownPosition;
            DisposeOutput();
            _tracker?.Seek(position);
            _lastKnownPosition = position;
            Error?.Invoke(this, e.Exception);
            return;
        }
        // Stops caused by seeking or a manual stop are ignored: only the end of the file counts
        if (_tracker is { ReachedEnd: true } && _output?.PlaybackState == PlaybackState.Stopped)
        {
            // Data running out well before the end is not the end of the book: typically a network or
            // USB drive that went away during standby. Keep the place instead of rewinding to 0:00.
            if (Duration - _lastKnownPosition > TimeSpan.FromSeconds(30))
            {
                var position = _lastKnownPosition;
                _tracker.Seek(position);
                _lastKnownPosition = position;
                Error?.Invoke(this, new IOException(
                    $"The audio stopped unexpectedly at {position:h\\:mm\\:ss} (is the file still reachable?)."));
                return;
            }
            _tracker.Seek(TimeSpan.Zero);
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Unload()
    {
        DisposeOutput();
        if (_tracker != null) _tracker.Close();   // disposes the stream once no read is in progress
        else _stream?.Dispose();
        _stream = null;
        _channel = null;
        _tracker = null;
    }

    public void Dispose() => Unload();

    /// <summary>
    /// Tracks which point in the file each sample delivered to the audio output corresponds to,
    /// including the playback speed in effect, so the audible position is exact at any speed.
    /// </summary>
    /// <param name="source">Seekable stream (position and seeking).</param>
    /// <param name="stretch">Speed-adjusted samples decoded from <paramref name="source"/>.</param>
    internal sealed class TrackingSampleProvider(WaveStream source, TimeStretchSampleProvider stretch) : ISampleProvider
    {
        readonly object _lock = new();
        readonly List<(long Index, double Time, double Speed)> _marks = [];
        readonly double _samplesPerSecond = stretch.WaveFormat.SampleRate * stretch.WaveFormat.Channels;
        long _delivered, _pendingIndex;
        double _pendingTime;
        bool _closed;

        public WaveFormat WaveFormat => stretch.WaveFormat;

        /// <summary>
        /// Disposes the source under the same lock as <see cref="Read"/>: WaveOutEvent.Stop does not wait for
        /// its playback thread, which may still be decoding (a Media Foundation reader must not be released
        /// mid-read). Later reads just report end of data.
        /// </summary>
        public void Close()
        {
            lock (_lock)
            {
                _closed = true;
                source.Dispose();
            }
        }

        /// <summary>Position playback will resume from while the output is stopped.</summary>
        public TimeSpan RestTime { get; private set; }

        public bool ReachedEnd { get; private set; }

        public int Read(float[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                if (_closed) return 0;
                double t = stretch.NextSourceTime;
                int n = stretch.Read(buffer, offset, count);
                if (n > 0)
                {
                    _marks.Add((_delivered, t, stretch.Rate));
                    if (_marks.Count > 64) _marks.RemoveRange(0, _marks.Count - 64);
                    _delivered += n;
                }
                else
                {
                    ReachedEnd = true;
                }
                return n;
            }
        }

        public void Seek(TimeSpan time)
        {
            lock (_lock)
            {
                source.CurrentTime = time;
                stretch.Reset(time.TotalSeconds, stretch.Rate);
                RestTime = time;
                ReachedEnd = false;
                // Until the queued (pre-seek) audio has played, already report the new position
                _pendingIndex = _delivered;
                _pendingTime = time.TotalSeconds;
            }
        }

        /// <summary>
        /// Changes speed from the next sample on. The stretcher reads ahead of what it outputs, so the
        /// source is repositioned at the exact point reached; audio already queued keeps its old speed
        /// and is still mapped correctly by its marks.
        /// </summary>
        public void SetSpeed(double speed)
        {
            lock (_lock)
            {
                if (speed == stretch.Rate) return;
                double t = stretch.NextSourceTime;
                source.CurrentTime = TimeSpan.FromSeconds(t);
                stretch.Reset(t, speed);
            }
        }

        public void ResetCounters()
        {
            lock (_lock)
            {
                _marks.Clear();
                _delivered = 0;
                _pendingIndex = 0;
                RestTime = TimeSpan.FromSeconds(stretch.NextSourceTime);
            }
        }

        public TimeSpan TimeAt(long playedSamples)
        {
            lock (_lock)
            {
                // The device cannot have played more than was delivered: a bogus counter (driver reset,
                // standby, device switch) must never push the position ahead, e.g. to the end of the book
                playedSamples = Math.Min(playedSamples, _delivered);
                if (playedSamples < _pendingIndex) return TimeSpan.FromSeconds(_pendingTime);
                for (int i = _marks.Count - 1; i >= 0; i--)
                {
                    var (index, time, speed) = _marks[i];
                    if (index <= playedSamples)
                        return TimeSpan.FromSeconds(time + (playedSamples - index) / _samplesPerSecond * speed);
                }
                return _marks.Count > 0 ? TimeSpan.FromSeconds(_marks[0].Time) : RestTime;
            }
        }
    }
}
