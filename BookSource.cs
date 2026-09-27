using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace aBookPlayer;

/// <summary>
/// A book is either a single audio file or a folder of audio files (one per chapter, often split in
/// "CD 1", "CD 2"… subfolders) played in Explorer's order as one continuous book.
/// </summary>
static class BookSource
{
    public static bool IsFolder(string path) => Directory.Exists(path);

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>The audio files of a folder book, in Explorer's natural order ("2" before "10").</summary>
    public static string[] PartsOf(string folder)
    {
        var files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(AudioFormats.IsSupported)
            .Select(f => Path.GetRelativePath(folder, f))
            .ToList();
        files.Sort(NaturalCompare);
        return AudibleExport.SelectParts(files.Select(f => Path.Combine(folder, f))).ToArray();
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int StrCmpLogicalW(string a, string b);

    internal static int NaturalCompare(string a, string b) => StrCmpLogicalW(a, b);

    /// <summary>Book name for lists: the file name without extension, or the folder name.</summary>
    public static string DisplayName(string path) =>
        AudibleExport.DisplayName(IsFolder(path)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path))
            : Path.GetFileNameWithoutExtension(path));

    /// <summary>Like <see cref="DisplayName"/> but without touching the disk (for lists that may include offline drives).</summary>
    public static string NameFromPath(string path) =>
        AudibleExport.DisplayName(AudioFormats.IsSupported(path)
            ? Path.GetFileNameWithoutExtension(path)
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));

    /// <summary>Where the book's subtitles go: next to the file, or inside the folder named after it.</summary>
    public static string SubtitlePath(string path) =>
        IsFolder(path) ? Path.Combine(path, DisplayName(path) + ".srt") : Path.ChangeExtension(path, ".srt");

    /// <summary>
    /// The subtitles to load with this book, if there are any. An Audible export (Libation) keeps them in the
    /// book's folder under its own long name ("Book_ Series, Book 2 [ASIN].srt"), not under the folder's name.
    /// </summary>
    public static string? FindSubtitle(string path)
    {
        var named = SubtitlePath(path);
        if (File.Exists(named)) return named;
        if (!IsFolder(path)) return null;
        try
        {
            var candidates = Directory.EnumerateFiles(path, "*.srt")
                .Select(f => (Path: f, Name: Path.GetFileNameWithoutExtension(f)))
                // The subtitle file of one chapter covers only part of the book: never load it for all of it
                .Where(c => AudibleExport.Parse(c.Name).Part == null)
                .OrderByDescending(c => new FileInfo(c.Path).Length)
                .ToList();
            if (candidates.Count == 0) return null;
            var asin = AudibleExport.Parse(Path.GetFileName(Path.TrimEndingDirectorySeparator(path))).Asin;
            return (candidates.FirstOrDefault(c => asin != null && c.Name.Contains(asin, StringComparison.OrdinalIgnoreCase)).Path
                    ?? candidates[0].Path);
        }
        catch { return null; }
    }

    /// <summary>Opens the book's audio (see <see cref="AudioFormats.Open"/>). Can take a while: call it off the UI thread.</summary>
    public static WaveStream Open(string path) => IsFolder(path) ? new ConcatenatedWaveStream(PartsOf(path)) : AudioFormats.Open(path);

    /// <summary>Opens the audio and reads title, author, chapters and cover: for a folder, one chapter per file.</summary>
    public static (MediaInfo Info, WaveStream Reader) OpenWithInfo(string path)
    {
        if (!IsFolder(path))
        {
            var info = MediaMetadata.Read(path);
            info.Cover ??= CoverArt.FromFolder(Path.GetDirectoryName(path), bookFolder: false);
            return (info, AudioFormats.Open(path));
        }

        var parts = PartsOf(path);
        if (parts.Length == 0) throw new IOException("The folder does not contain any supported audio files.");
        var reader = new ConcatenatedWaveStream(parts);
        var book = new MediaInfo();
        for (int i = 0; i < parts.Length; i++)
        {
            var part = MediaMetadata.Read(parts[i]);
            var start = reader.PartStart(i);
            if (i == 0)
            {
                // Audiobook rips usually put the book title in the album tag and the author in the artist
                book.Title = part.Album ?? DisplayName(path);
                book.Artist = part.Artist;
                book.Cover = part.Cover;
            }
            book.Asin ??= part.Asin;
            book.Series ??= part.Series;
            book.SeriesNumber ??= part.SeriesNumber;
            if (part.Chapters.Count > 1)
                foreach (var c in part.Chapters)
                    book.Chapters.Add(new Chapter(c.Title, start + c.Start, c.End > TimeSpan.Zero ? start + c.End : TimeSpan.Zero));
            else
                book.Chapters.Add(new Chapter(ChapterName(parts[i], part), start, reader.PartStart(i + 1)));
        }
        // The folder name carries the export's title and ASIN even when the tags do not
        var folder = AudibleExport.Parse(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));
        book.Title ??= folder.Title;
        book.Asin ??= folder.Asin;
        book.Cover ??= CoverArt.FromFolder(path);
        return (book, reader);
    }

    /// <summary>
    /// Chapter title of one file of a folder book: the export names each file after its chapter
    /// ("… - 07 - Chapter 6"), which beats the tag, where the book and the number are repeated
    /// ("2 - Dungeon Crawler Carl: Chapter 1").
    /// </summary>
    static string ChapterName(string path, MediaInfo info)
    {
        var name = AudibleExport.Parse(Path.GetFileNameWithoutExtension(path));
        if (name.Chapter != null && (name.Asin != null || info.Asin != null)) return name.Chapter;
        return info.Title ?? Path.GetFileNameWithoutExtension(path);
    }
}

/// <summary>
/// Plays several audio files as one seekable stream. Output is 32-bit float in the first file's sample
/// rate and channel count; files in another format are converted on the fly. Only the file being played
/// is open (plus the next one, opened ahead so the switch does not stall playback).
/// </summary>
sealed class ConcatenatedWaveStream : WaveStream
{
    readonly string[] _paths;
    readonly TimeSpan[] _starts;     // start of each part in the book, plus the total at the end
    readonly WaveFormat _format;
    readonly object _lock = new();
    int _index = -1;
    WaveStream? _part;
    ISampleProvider? _samples;
    long _position;                  // bytes, in _format
    Task<WaveStream>? _prefetch;
    int _prefetchIndex = -1;
    float[] _temp = [];
    readonly Func<string, WaveStream> _open;
    static readonly float[] Silence = new float[4096];

    public ConcatenatedWaveStream(string[] paths) : this(paths, AudioFormats.Open) { }

    /// <param name="open">Opens one part (tests pass their own streams).</param>
    internal ConcatenatedWaveStream(string[] paths, Func<string, WaveStream> open)
    {
        if (paths.Length == 0) throw new ArgumentException("No audio files.", nameof(paths));
        _paths = paths;
        _open = open;
        _starts = new TimeSpan[paths.Length + 1];
        // Durations are needed up front for the length and the chapter positions
        for (int i = 0; i < paths.Length; i++)
        {
            using var part = open(paths[i]);
            if (i == 0) _format = WaveFormat.CreateIeeeFloatWaveFormat(part.WaveFormat.SampleRate, Math.Min(2, part.WaveFormat.Channels));
            _starts[i + 1] = _starts[i] + part.TotalTime;
        }
        _format ??= WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        OpenPart(0, TimeSpan.Zero);
    }

    public int PartCount => _paths.Length;

    /// <summary>Where part <paramref name="index"/> starts in the book (the total length for index = count).</summary>
    public TimeSpan PartStart(int index) => _starts[Math.Clamp(index, 0, _paths.Length)];

    public override WaveFormat WaveFormat => _format;

    public override long Length => BytesAt(_starts[^1]);

    public override long Position
    {
        get { lock (_lock) return _position; }
        set
        {
            lock (_lock)
            {
                var time = TimeSpan.FromSeconds(Math.Clamp(value, 0, Length) / (double)_format.AverageBytesPerSecond);
                // Last part starting at or before the time (FindLastIndex searches backwards from startIndex)
                int index = Array.FindLastIndex(_starts, _paths.Length - 1, _paths.Length, s => s <= time);
                OpenPart(Math.Max(0, index), time - _starts[Math.Max(0, index)]);
                _position = BytesAt(time);
            }
        }
    }

    long BytesAt(TimeSpan time)
    {
        long bytes = (long)(time.TotalSeconds * _format.AverageBytesPerSecond);
        return bytes - bytes % _format.BlockAlign;
    }

    void OpenPart(int index, TimeSpan offset)
    {
        if (index != _index)
        {
            _part?.Dispose();
            _part = null;
            _samples = null;
            _index = index;
            _part = TakePrefetched(index) ?? _open(_paths[index]);
            Prefetch(index + 1);
        }
        _part!.CurrentTime = offset < _part.TotalTime ? offset : _part.TotalTime;
        _samples = Conform(_part);
    }

    WaveStream? TakePrefetched(int index)
    {
        var task = _prefetch;
        if (task == null) return null;
        _prefetch = null;
        if (_prefetchIndex == index)
        {
            try { return task.GetAwaiter().GetResult(); }
            catch { return null; } // open it again below, reporting the error there
        }
        _ = task.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); });
        return null;
    }

    void Prefetch(int index)
    {
        if (index >= _paths.Length) return;
        var path = _paths[index];
        _prefetchIndex = index;
        var open = _open;
        _prefetch = Task.Run(() => open(path));
    }

    /// <summary>Converts a part to the book's format (float, same channels and sample rate as the first part).</summary>
    ISampleProvider Conform(WaveStream part)
    {
        ISampleProvider s = part.ToSampleProvider();
        int channels = _format.Channels;
        if (s.WaveFormat.Channels > 2) s = new DownmixToStereo(s);
        if (s.WaveFormat.Channels == 2 && channels == 1) s = new DownmixToMono(s);
        else if (s.WaveFormat.Channels == 1 && channels == 2) s = new MonoToStereoSampleProvider(s);
        if (s.WaveFormat.SampleRate != _format.SampleRate) s = new WdlResamplingSampleProvider(s, _format.SampleRate);
        return s;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            count -= count % _format.BlockAlign;
            int total = 0;
            while (total < count && _samples != null)
            {
                // Each part lasts exactly its declared duration, the one the chapters and seeking are based on:
                // extra decoded audio is cut and a part that ends early is padded with silence. Otherwise the few
                // ms by which decoders differ from the headers would add up, file after file, and move the
                // chapters and the subtitles away from the audio.
                long room = BytesAt(_starts[_index + 1]) - _position;
                if (room <= 0)
                {
                    if (_index + 1 >= _paths.Length) break;
                    OpenPart(_index + 1, TimeSpan.Zero);
                    _position = BytesAt(_starts[_index]);
                    continue;
                }
                int wanted = (int)Math.Min((count - total) / 4, room / 4);
                if (_temp.Length < wanted) _temp = new float[wanted];
                int n = _samples.Read(_temp, 0, wanted);
                if (n <= 0)
                {
                    // Much too early is not a rounding difference but a read failure (drive gone, network glitch):
                    // report the end of data, so the player reopens the book there and continues
                    if (room > _format.AverageBytesPerSecond * 2L) break;
                    // A few ms short: silence up to the declared end (Buffer.BlockCopy counts bytes whatever the array type)
                    n = Math.Min(wanted, Silence.Length);
                    Buffer.BlockCopy(Silence, 0, buffer, offset + total, n * 4);
                }
                else
                {
                    Buffer.BlockCopy(_temp, 0, buffer, offset + total, n * 4);
                }
                total += n * 4;
                _position += n * 4;
            }
            return total;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_lock)
            {
                _part?.Dispose();
                _part = null;
                _samples = null;
                TakePrefetched(-1);
            }
        }
        base.Dispose(disposing);
    }
}
