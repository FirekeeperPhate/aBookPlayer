namespace aBookPlayer;

/// <summary>
/// A long MP3 served to phones in parts of a few minutes, cut between its frames (not re-encoded), each opened by a
/// Xing frame made from the real frame index: its exact length and a fine seek table.
/// </summary>
/// <remarks>
/// Android's player seeks in an MP3 through its Xing table (100 points: 6.6 minutes apart in an 11-hour book) or,
/// without one, as if the bitrate were constant: in a long variable-bitrate book it lands minutes away from the
/// position it reports, so the audio and the subtitles part. In parts of a few minutes with exact lengths, a seek
/// goes to the right part and, inside it, within a fraction of a second.
/// </remarks>
static class Mp3Segments
{
    /// <summary>A part: the file's bytes from <paramref name="Offset"/> (whole frames), after <paramref name="Prefix"/> (its Xing frame).</summary>
    public sealed record Segment(long Offset, long Length, int Frames, double Seconds, byte[] Prefix);

    static readonly int[] BitratesV1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    static readonly int[] BitratesV2 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    static readonly int[] RatesV1 = [44100, 48000, 32000];
    static readonly int[] RatesV2 = [22050, 24000, 16000];
    static readonly int[] RatesV25 = [11025, 12000, 8000];

    /// <summary>A Layer III frame header's fields, or null for anything else (free bitrate, Layer I/II, reserved values).</summary>
    readonly record struct Header(int Version, int SampleRate, int Bitrate, bool Padding, bool Mono, byte B1, byte B3)
    {
        // Version: 3 = MPEG-1, 2 = MPEG-2, 0 = MPEG-2.5
        public bool Mpeg1 => Version == 3;
        public int SamplesPerFrame => Mpeg1 ? 1152 : 576;
        public int FrameSize => (Mpeg1 ? 144 : 72) * Bitrate * 1000 / SampleRate + (Padding ? 1 : 0);
        public int SideInfo => Mpeg1 ? (Mono ? 17 : 32) : (Mono ? 9 : 17);

        public static Header? Parse(ReadOnlySpan<byte> b)
        {
            if (b.Length < 4 || b[0] != 0xFF || (b[1] & 0xE0) != 0xE0) return null;
            int version = (b[1] >> 3) & 3, layer = (b[1] >> 1) & 3, bitrateIndex = (b[2] >> 4) & 15, rateIndex = (b[2] >> 2) & 3;
            if (version == 1 || layer != 1 || bitrateIndex is 0 or 15 || rateIndex == 3) return null;
            int rate = (version == 3 ? RatesV1 : version == 2 ? RatesV2 : RatesV25)[rateIndex];
            int bitrate = (version == 3 ? BitratesV1 : BitratesV2)[bitrateIndex];
            return new Header(version, rate, bitrate, (b[2] & 2) != 0, ((b[3] >> 6) & 3) == 3, b[1], b[3]);
        }
    }

    /// <summary>
    /// The parts of a variable-bitrate MP3 of about <paramref name="seconds"/> each, cut in pauses; null when it is
    /// not a plain MPEG Layer III stream with one sample rate, or its bitrate is constant (then it is played whole).
    /// </summary>
    public static List<Segment>? Split(string file, double seconds = 180)
    {
        var frames = new List<(long Offset, int Size)>();
        Header? first = null;
        try
        {
            using var reader = new FrameReader(file);
            long position = reader.SkipId3v2();
            long end = reader.Length;
            while (position + 4 <= end)
            {
                var header = Header.Parse(reader.Peek(position, 4));
                if (header is not { } h || (first is { } f && (h.SampleRate != f.SampleRate || h.Version != f.Version)))
                {
                    // The end (an ID3v1 or APE tag), or damage: look for the next frame a little further, else stop
                    if (reader.StartsWith(position, "TAG"u8) || reader.StartsWith(position, "APETAGEX"u8)) break;
                    long next = reader.FindSync(position + 1, 64 * 1024);
                    if (next < 0) break;
                    position = next;
                    continue;
                }
                int size = h.FrameSize;
                if (size < 4 || position + size > end) break;
                if (first == null)
                {
                    first = h;
                    // A Xing, Info or VBRI frame of the file itself: not audio, and replaced by each part's own
                    int tag = 4 + h.SideInfo;
                    if (reader.StartsWith(position + tag, "Xing"u8) || reader.StartsWith(position + tag, "Info"u8) || reader.StartsWith(position + 36, "VBRI"u8))
                    {
                        position += size;
                        continue;
                    }
                }
                frames.Add((position, size));
                position += size;
            }
        }
        catch (IOException) { return null; }
        if (first is not { } format || frames.Count == 0) return null;
        // A constant bitrate (frames of one size, give or take the padding byte): Android seeks in it exactly
        if (frames.Max(f => f.Size) - frames.Min(f => f.Size) <= 1) return null;

        int perSegment = Math.Max(1, (int)Math.Round(seconds * format.SampleRate / format.SamplesPerFrame));
        // Each part starts in a pause: its first frames play as silence, since they draw on bytes of the frames
        // before them (the bit reservoir) and on the previous frame's overlap. The quietest place near the planned
        // cut is where the frames are smallest (a variable bitrate spends the fewest bits on silence). The reservoir
        // reaches up to 511 bytes back (255 in MPEG-2), so up to about 10 small frames of silence decode wrong: the
        // pause must be longer than that
        int quiet = format.Mpeg1 ? 12 : 16;
        int window = perSegment / 12; // ±15 s for parts of 3 minutes
        var sums = new long[frames.Count + 1];
        for (int i = 0; i < frames.Count; i++) sums[i + 1] = sums[i] + frames[i].Size;
        var segments = new List<Segment>();
        int start = 0;
        while (start < frames.Count)
        {
            int count = frames.Count - start;
            // A last bit of a few seconds goes with the part before it
            if (count > perSegment + perSegment / 10)
            {
                int target = start + perSegment, cut = target;
                long least = long.MaxValue;
                for (int i = target - window; i <= target + window && i + quiet <= frames.Count; i++)
                {
                    long loudness = sums[i + quiet] - sums[i];
                    if (loudness < least || (loudness == least && Math.Abs(i - target) < Math.Abs(cut - target))) (cut, least) = (i, loudness);
                }
                count = cut - start;
            }
            long offset = frames[start].Offset;
            long length = frames[start + count - 1].Offset + frames[start + count - 1].Size - offset;
            var prefix = XingFrame(format, frames, start, count, offset, length);
            segments.Add(new Segment(offset, length, count, (double)count * format.SamplesPerFrame / format.SampleRate, prefix));
            start += count;
        }
        return segments;
    }

    /// <summary>
    /// A Xing frame for the part: its frame count (so its exact length) and its size, and a table of where each
    /// hundredth of it starts, from the real frame offsets. Players skip it: it holds no audio.
    /// </summary>
    static byte[] XingFrame(Header format, List<(long Offset, int Size)> frames, int start, int count, long offset, long length)
    {
        const int xingBytes = 4 + 4 + 4 + 4 + 100; // "Xing", flags, frames, bytes, table
        // The smallest bitrate whose frame holds the tag
        var bitrates = format.Mpeg1 ? BitratesV1 : BitratesV2;
        int index = 1;
        while (index < 14 && (format.Mpeg1 ? 144 : 72) * bitrates[index] * 1000 / format.SampleRate < 4 + format.SideInfo + xingBytes + 24) index++;
        int rateBits = format.SampleRate switch { 44100 or 22050 or 11025 => 0, 48000 or 24000 or 12000 => 1, _ => 2 };
        int size = (format.Mpeg1 ? 144 : 72) * bitrates[index] * 1000 / format.SampleRate;
        var frame = new byte[size];
        frame[0] = 0xFF;
        frame[1] = (byte)(format.B1 | 0x01); // no CRC
        frame[2] = (byte)((index << 4) | (rateBits << 2));
        frame[3] = format.B3;
        int p = 4 + format.SideInfo;
        "Xing"u8.CopyTo(frame.AsSpan(p));
        WriteInt(frame, p + 4, 0x7); // frames, bytes, table
        WriteInt(frame, p + 8, count);
        long dataSize = size + length;
        WriteInt(frame, p + 12, (int)Math.Min(int.MaxValue, dataSize));
        for (int i = 0; i < 100; i++)
        {
            int at = start + (int)((long)i * count / 100);
            long bytes = size + frames[at].Offset - offset;
            frame[p + 16 + i] = (byte)Math.Min(255, bytes * 256 / dataSize);
        }
        return frame;
    }

    static void WriteInt(byte[] b, int at, int value)
    {
        b[at] = (byte)(value >> 24);
        b[at + 1] = (byte)(value >> 16);
        b[at + 2] = (byte)(value >> 8);
        b[at + 3] = (byte)value;
    }

    /// <summary>Reads a file forwards through a window, for its frame headers (an 11-hour book is a few hundred MB).</summary>
    sealed class FrameReader : IDisposable
    {
        readonly FileStream _file;
        readonly byte[] _window = new byte[1 << 20];
        long _windowStart;
        int _windowLength;

        public FrameReader(string path)
        {
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            Length = _file.Length;
        }

        public long Length { get; }

        public ReadOnlySpan<byte> Peek(long position, int count)
        {
            if (position < _windowStart || position + count > _windowStart + _windowLength)
            {
                _file.Position = _windowStart = position;
                _windowLength = 0;
                int read;
                while (_windowLength < _window.Length && (read = _file.Read(_window, _windowLength, _window.Length - _windowLength)) > 0) _windowLength += read;
            }
            int from = (int)(position - _windowStart);
            return _window.AsSpan(from, Math.Max(0, Math.Min(count, _windowLength - from)));
        }

        public bool StartsWith(long position, ReadOnlySpan<byte> text) => position + text.Length <= Length && Peek(position, text.Length).SequenceEqual(text);

        /// <summary>Where the audio starts: after an ID3v2 tag (covers, titles), if there is one.</summary>
        public long SkipId3v2()
        {
            var head = Peek(0, 10);
            if (head.Length < 10 || !head[..3].SequenceEqual("ID3"u8)) return 0;
            long size = (head[6] << 21) | (head[7] << 14) | (head[8] << 7) | head[9];
            return 10 + size + ((head[5] & 0x10) != 0 ? 10 : 0);
        }

        /// <summary>The next frame header within <paramref name="limit"/> bytes, followed by another one (not a chance 0xFF), or -1.</summary>
        public long FindSync(long from, int limit)
        {
            for (long p = from; p < Math.Min(Length - 4, from + limit); p++)
            {
                if (Peek(p, 1)[0] != 0xFF) continue;
                if (Header.Parse(Peek(p, 4)) is { } h && p + h.FrameSize + 4 <= Length && Header.Parse(Peek(p + h.FrameSize, 4)) != null) return p;
            }
            return -1;
        }

        public void Dispose() => _file.Dispose();
    }
}
