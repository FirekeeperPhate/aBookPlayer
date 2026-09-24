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
            if (_channel != null) _channel.Volume = _volume;
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
    public void Load(WaveStream stream)
    {
        Unload();
        _stream = stream;
        _channel = new SampleChannel(stream) { Volume = _volume };
        _tracker = new TrackingSampleProvider(stream, _channel);
        _output = new WaveOutEvent { DesiredLatency = 200, NumberOfBuffers = 3 };
        _output.PlaybackStopped += OnPlaybackStopped;
        _output.Init(_tracker);
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
        _stream?.Dispose();
        _stream = null;
        _channel = null;
        _tracker = null;
    }

    public void Dispose() => Unload();

    /// <summary>Tracks which point in the file each sample delivered to the audio output corresponds to.</summary>
    /// <param name="source">Seekable stream (position and seeking).</param>
    /// <param name="samples">Float samples decoded from <paramref name="source"/> (with volume applied).</param>
    sealed class TrackingSampleProvider(WaveStream source, ISampleProvider samples) : ISampleProvider
    {
        readonly object _lock = new();
        readonly List<(long Index, double Time)> _marks = [];
        readonly double _samplesPerSecond = samples.WaveFormat.SampleRate * samples.WaveFormat.Channels;
        long _delivered, _pendingIndex;
        double _pendingTime;

        public WaveFormat WaveFormat => samples.WaveFormat;

        /// <summary>Position playback will resume from while the output is stopped.</summary>
        public TimeSpan RestTime { get; private set; }

        public bool ReachedEnd { get; private set; }

        public int Read(float[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                double t = source.CurrentTime.TotalSeconds;
                int n = samples.Read(buffer, offset, count);
                if (n > 0)
                {
                    _marks.Add((_delivered, t));
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
                RestTime = time;
                ReachedEnd = false;
                // Until the queued (pre-seek) audio has played, already report the new position
                _pendingIndex = _delivered;
                _pendingTime = time.TotalSeconds;
            }
        }

        public void ResetCounters()
        {
            lock (_lock)
            {
                _marks.Clear();
                _delivered = 0;
                _pendingIndex = 0;
                RestTime = source.CurrentTime;
            }
        }

        public TimeSpan TimeAt(long playedSamples)
        {
            lock (_lock)
            {
                if (playedSamples < _pendingIndex) return TimeSpan.FromSeconds(_pendingTime);
                for (int i = _marks.Count - 1; i >= 0; i--)
                {
                    var (index, time) = _marks[i];
                    if (index <= playedSamples)
                        return TimeSpan.FromSeconds(time + (playedSamples - index) / _samplesPerSecond);
                }
                return _marks.Count > 0 ? TimeSpan.FromSeconds(_marks[0].Time) : RestTime;
            }
        }
    }
}
