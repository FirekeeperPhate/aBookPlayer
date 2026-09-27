using NAudio;
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

    /// <summary>Makes the current counter value correspond to <paramref name="bytes"/> played (after a bogus forward jump).</summary>
    public void Rebase(long bytes)
    {
        if (_last >= 0) _offset = bytes - _last;
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
    VolumeSampleProvider? _channel;
    VoiceBoost? _boost;
    bool _voiceBoost;
    TrackingSampleProvider? _tracker;
    float _volume = 0.8f;
    float _fade = 1f;
    double _speed = 1.0;
    readonly PlaybackCounter _counter = new();
    TimeSpan _lastKnownPosition;
    string? _sourcePath;
    int _endCheck;              // bumped by anything that makes a pending early-end check obsolete
    TimeSpan? _resumedAt;       // where playback was last resumed on a reopened file (see CheckEarlyEndAsync)

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

    /// <summary>Evens out the narrator's volume (see <see cref="aBookPlayer.VoiceBoost"/>); switches instantly.</summary>
    public bool VoiceBoost
    {
        get => _voiceBoost;
        set
        {
            _voiceBoost = value;
            if (_boost != null) _boost.Enabled = value;
        }
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
            var t = _output.PlaybackState != PlaybackState.Stopped ? _tracker.TimeAt(PlayedSamples())
                : _tracker.ReachedEnd ? _tracker.TimeAt(long.MaxValue)   // ran out of data: where the audio stopped
                : _tracker.RestTime;
            if (t > Duration) t = Duration;
            _lastKnownPosition = t;
            return t;
        }
    }

    /// <summary>Loads an already-open stream (see <see cref="AudioFormats.Open"/>; opening can scan the whole file, so do it off the UI thread).</summary>
    /// <remarks>On failure the stream is disposed and the player is left unloaded (never half-initialized).</remarks>
    public void Load(WaveStream stream, string? sourcePath = null)
    {
        Unload();
        try
        {
            _stream = stream;
            _sourcePath = sourcePath;
            // Voice boost works on the source level, before the user's volume
            _boost = new VoiceBoost(stream.ToSampleProvider()) { Enabled = _voiceBoost };
            _channel = new VolumeSampleProvider(_boost) { Volume = _volume * _fade };
            ISampleProvider samples = _channel.WaveFormat.Channels > 2 ? new DownmixToStereo(_channel) : _channel;
            var stretch = new TimeStretchSampleProvider(samples);
            stretch.Reset(0, _speed);
            _tracker = new TrackingSampleProvider(stream, stretch);
            _lastKnownPosition = TimeSpan.Zero;
            _resumedAt = null;
            try { CreateOutput(); }
            catch (MmException) { DisposeOutput(); } // no usable audio device right now: Play retries (and reports) later
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
        // Disposing resets the device, which throws when the device is already gone: nothing to clean up then
        try { _output.Dispose(); }
        catch (MmException) { }
        _output = null;
    }

    public void Play()
    {
        _resumedAt = null; // the listener asked: allow one more automatic resume at the same place
        PlayCore();
    }

    void PlayCore()
    {
        if (_tracker == null) return;
        _endCheck++;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (_output == null)
                {
                    // The device was lost earlier (e.g. across standby): use the current default device
                    CreateOutput();
                    _tracker.Seek(_lastKnownPosition);
                }
                if (_output!.PlaybackState == PlaybackState.Stopped)
                {
                    // Fresh start: realign the sound card's counter with the provider's
                    _counter.Reset(RawBytes());
                    _tracker.ResetCounters();
                }
                _output.Play();
                return;
            }
            catch (MmException) when (attempt == 0)
            {
                // A paused output resumes with waveOutRestart, which fails if the device went away while
                // paused (standby, unplugged headphones): start again once on a fresh output
                DropBrokenOutput();
            }
            catch (Exception ex)
            {
                if (IsReadFailure(ex)) { OnReadFailure(_lastKnownPosition); return; }
                DropBrokenOutput();
                Error?.Invoke(this, ex);
                return;
            }
        }
    }

    public void Pause()
    {
        if (!IsPlaying) return;
        try { _output!.Pause(); }
        catch (MmException) { DropBrokenOutput(); } // device gone: stay "paused" at the same place
    }

    /// <summary>Discards an output whose device failed, keeping the position so Play resumes from there.</summary>
    void DropBrokenOutput()
    {
        var position = _lastKnownPosition;
        DisposeOutput();
        TrySeek(position);
        _lastKnownPosition = position;
    }

    /// <summary>Seeks the tracker, which fails when the file's reader is broken (seeking may read the file).</summary>
    bool TrySeek(TimeSpan time)
    {
        try
        {
            _tracker?.Seek(time);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Stop()
    {
        if (_tracker == null) return;
        _endCheck++;
        _output?.Stop();
        TrySeek(TimeSpan.Zero);
        _lastKnownPosition = TimeSpan.Zero;
    }

    public void Seek(TimeSpan time)
    {
        if (_tracker == null) return;
        _endCheck++;
        var max = Duration - TimeSpan.FromMilliseconds(50);
        if (time > max) time = max;
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;

        // While paused, discard queued audio so playback resumes exactly at the chosen point
        if (_output?.PlaybackState == PlaybackState.Paused) _output.Stop();
        // A broken reader (file gone) cannot seek: drop the output and keep the point, so that Play starts
        // over from it (and, when reading fails again, reopens the file there)
        if (!TrySeek(time)) DisposeOutput();
        _lastKnownPosition = time;
    }

    long RawBytes()
    {
        try { return _output!.GetPosition(); }
        catch { return 0; }
    }

    long PlayedSamples()
    {
        int bytesPerSample = _output!.OutputWaveFormat.BitsPerSample / 8;
        long played = _counter.Update(RawBytes()) / bytesPerSample;
        long delivered = _tracker!.Delivered;
        if (played > delivered)
        {
            // Impossible: the counter jumped forward. Re-anchor it just behind what was delivered (half of
            // the ~200 ms device buffer), so the position does not stay ahead of the audio for the session
            long margin = _output.OutputWaveFormat.AverageBytesPerSecond / 10 / bytesPerSample;
            played = Math.Max(0, delivered - margin);
            _counter.Rebase(played * bytesPerSample);
        }
        return played;
    }

    void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // NAudio posts this event with the handler captured when it was raised, so an output that has
        // since been replaced (new book, recreated device) can still deliver it: only the current one counts
        if (!ReferenceEquals(sender, _output)) return;

        if (e.Exception != null && IsReadFailure(e.Exception))
        {
            OnReadFailure(_lastKnownPosition);
            return;
        }
        if (e.Exception != null)
        {
            // The device failed (unplugged, standby, driver reset): keep the position and drop the broken
            // output, so Play resumes from here on a fresh one instead of losing the place in the book
            DropBrokenOutput();
            Error?.Invoke(this, e.Exception);
            return;
        }
        // Stops caused by seeking or a manual stop are ignored: only the end of the file counts
        if (_tracker is { ReachedEnd: true } && _output?.PlaybackState == PlaybackState.Stopped)
        {
            // Data running out well before the declared end: the file may really be shorter than its header
            // says, or reading failed (network/USB drive gone or glitching). Position keeps reporting where
            // the audio stopped (ReachedEnd) while the file is checked off the UI thread.
            var position = Position;
            if (Duration - position > TimeSpan.FromSeconds(30))
            {
                _ = CheckEarlyEndAsync(++_endCheck, position);
                return;
            }
            TrySeek(TimeSpan.Zero);
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Waveout failures are <see cref="MmException"/>s; anything else thrown while playing comes from reading
    /// or decoding the file. Without a path the file cannot be reopened, so it is reported like a device error.
    /// </summary>
    bool IsReadFailure(Exception ex) => ex is not MmException && _sourcePath != null;

    /// <summary>
    /// Reading the file failed. Its reader may stay broken (e.g. a network handle lost with the connection),
    /// so instead of retrying on it the file is checked like an early end: reopened at the same point and
    /// continued on the fresh reader when the audio is there again.
    /// </summary>
    void OnReadFailure(TimeSpan position)
    {
        DisposeOutput();
        _lastKnownPosition = position;
        _ = CheckEarlyEndAsync(++_endCheck, position);
    }

    /// <summary>
    /// Decides what an early end of data means by reopening the file at the point where the audio stopped:
    /// audio there = reading was interrupted (continue on the reopened file); no audio = the file really
    /// ends there (normal end); cannot open = the file is unreachable (keep the place and report it).
    /// </summary>
    async Task CheckEarlyEndAsync(int check, TimeSpan position)
    {
        var path = _sourcePath;
        (bool Readable, WaveStream? Reopened) probe = path == null ? (false, null) : await Task.Run(() => ReopenAt(path, position));
        if (check != _endCheck)
        {
            probe.Reopened?.Dispose(); // the user or a new file moved things on
            return;
        }

        if (probe.Reopened != null)
        {
            if (_resumedAt is not { } last || (position - last).Duration() > TimeSpan.FromSeconds(5))
            {
                ResumeOn(probe.Reopened, position);
                return;
            }
            probe.Reopened.Dispose(); // already resumed here once: stop instead of looping
            KeepPlace(position, $"The audio keeps stopping at {position:h\\:mm\\:ss}: part of the file may be damaged, or its drive is unstable.");
        }
        else if (probe.Readable)
        {
            TrySeek(TimeSpan.Zero);
            _lastKnownPosition = TimeSpan.Zero;
            Ended?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            KeepPlace(position, $"The audio stopped unexpectedly at {position:h\\:mm\\:ss}: the file cannot be read any more (is its drive still connected?).");
        }
    }

    /// <summary>Reopens <paramref name="path"/> at <paramref name="position"/>: whether it could be opened, and the stream there if it still has audio.</summary>
    internal static (bool Readable, WaveStream? Reopened) ReopenAt(string path, TimeSpan position)
    {
        WaveStream? stream = null;
        try
        {
            stream = BookSource.Open(path);
            stream.CurrentTime = position;
            var format = stream.WaveFormat;
            var buffer = new byte[format.AverageBytesPerSecond / 2 / format.BlockAlign * format.BlockAlign]; // 0.5 s
            int total = 0, n;
            while (total < buffer.Length && (n = stream.Read(buffer, total, buffer.Length - total)) > 0) total += n;
            if (total < buffer.Length)
            {
                stream.Dispose(); // the file really ends here
                return (true, null);
            }
            stream.CurrentTime = position;
            return (true, stream);
        }
        catch
        {
            stream?.Dispose();
            return (false, null);
        }
    }

    /// <summary>Continues playback on a freshly opened stream of the same file (the old reader may be broken).</summary>
    void ResumeOn(WaveStream stream, TimeSpan position)
    {
        try { Load(stream, _sourcePath); }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex);
            return;
        }
        Seek(position);
        PlayCore();
        _resumedAt = position;
    }

    void KeepPlace(TimeSpan position, string message)
    {
        // May fail if the reader is broken: Position still reports the place, and Play re-checks the file
        TrySeek(position);
        _lastKnownPosition = position;
        Error?.Invoke(this, new IOException(message));
    }

    public void Unload()
    {
        _endCheck++;
        DisposeOutput();
        if (_tracker != null) _tracker.Close();   // disposes the stream once no read is in progress
        else _stream?.Dispose();
        _stream = null;
        _channel = null;
        _boost = null;
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

        /// <summary>Samples handed to the audio device since playback (re)started.</summary>
        public long Delivered
        {
            get { lock (_lock) return _delivered; }
        }

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
