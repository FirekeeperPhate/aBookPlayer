using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace aBookPlayer;

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
    long _baseBytes, _lastRawBytes, _wrapBytes;

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
            if (_output == null || _tracker == null) return TimeSpan.Zero;
            if (_output.PlaybackState == PlaybackState.Stopped) return _tracker.RestTime;
            var t = _tracker.TimeAt(PlayedSamples());
            return t > Duration ? Duration : t;
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
            _output = new WaveOutEvent { DesiredLatency = 200, NumberOfBuffers = 3 };
            _output.PlaybackStopped += OnPlaybackStopped;
            _output.Init(_tracker);
        }
        catch
        {
            Unload();
            throw;
        }
    }

    public void Play()
    {
        if (_output == null || _tracker == null) return;
        if (_output.PlaybackState == PlaybackState.Stopped)
        {
            // Fresh start: realign the sound card's counter with the provider's
            _baseBytes = _lastRawBytes = RawBytes();
            _wrapBytes = 0;
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
        if (_output == null || _tracker == null) return;
        _output.Stop();
        _tracker.Seek(TimeSpan.Zero);
    }

    public void Seek(TimeSpan time)
    {
        if (_output == null || _tracker == null) return;
        var max = Duration - TimeSpan.FromMilliseconds(50);
        if (time > max) time = max;
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;

        // While paused, discard queued audio so playback resumes exactly at the chosen point
        if (_output.PlaybackState == PlaybackState.Paused) _output.Stop();
        _tracker.Seek(time);
    }

    long RawBytes()
    {
        try { return _output!.GetPosition(); }
        catch { return 0; }
    }

    long PlayedSamples()
    {
        long raw = RawBytes();
        if (raw < _lastRawBytes) _wrapBytes += 1L << 32; // the waveOut counter is 32-bit
        _lastRawBytes = raw;
        long bytes = raw + _wrapBytes - _baseBytes;
        return Math.Max(0, bytes / (_output!.OutputWaveFormat.BitsPerSample / 8));
    }

    void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            Error?.Invoke(this, e.Exception);
            return;
        }
        // Stops caused by seeking or a manual stop are ignored: only the end of the file counts
        if (_tracker is { ReachedEnd: true } && _output?.PlaybackState == PlaybackState.Stopped)
        {
            _tracker.Seek(TimeSpan.Zero);
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Unload()
    {
        if (_output != null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            _output.Dispose();
            _output = null;
        }
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
    sealed class TrackingSampleProvider(WaveStream source, TimeStretchSampleProvider stretch) : ISampleProvider
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
