using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using NVorbis;

namespace aBookPlayer;

sealed record Chapter(string Title, TimeSpan Start, TimeSpan End);

sealed class MediaInfo
{
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public List<Chapter> Chapters { get; } = [];
    /// <summary>Embedded cover picture (JPEG/PNG bytes), if any.</summary>
    public byte[]? Cover { get; set; }
    /// <summary>Who reads the book (ID3 "TCOM", the tag audiobook tools write it in).</summary>
    public string? Narrator { get; set; }
    /// <summary>Audible book id, series and series number (see <see cref="AudibleExport"/>), if the tags or the name carry them.</summary>
    public string? Asin { get; set; }
    public string? Series { get; set; }
    public int? SeriesNumber { get; set; }
}

/// <summary>Book covers: embedded in the audio file, or an image next to it.</summary>
static class CoverArt
{
    static readonly string[] PreferredNames = ["cover", "folder", "front", "albumart", "albumartlarge"];
    static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png"];

    /// <summary>
    /// cover.jpg / folder.jpg / front.jpg…; for a book's own folder also the only image in it (not for a single
    /// file, whose folder may be Downloads or the like, full of unrelated pictures).
    /// </summary>
    public static byte[]? FromFolder(string? folder, bool bookFolder = true)
    {
        try
        {
            if (folder == null || !Directory.Exists(folder)) return null;
            var images = Directory.EnumerateFiles(folder)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToList();
            var chosen = images.FirstOrDefault(f => PreferredNames.Contains(Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase))
                         ?? (bookFolder && images.Count == 1 ? images[0] : null);
            return chosen != null && new FileInfo(chosen).Length < 20 * 1024 * 1024 ? File.ReadAllBytes(chosen) : null;
        }
        catch { return null; }
    }

    /// <summary>FLAC PICTURE block (also base64-encoded in OGG's METADATA_BLOCK_PICTURE): type, mime, description, size, data.</summary>
    public static (int Type, byte[] Data)? ParseFlacPicture(byte[] b)
    {
        try
        {
            int p = 0;
            int type = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p)); p += 4;
            p += 4 + BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p));   // mime
            p += 4 + BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p));   // description
            p += 16;                                                      // width, height, depth, colors
            int length = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p)); p += 4;
            if (length <= 0 || p + length > b.Length) return null;
            return (type, b.AsSpan(p, length).ToArray());
        }
        catch { return null; }
    }
}

/// <summary>Reads title, artist, album and chapters from the tag format used by each audio format.</summary>
static class MediaMetadata
{
    public static MediaInfo Read(string path)
    {
        var info = new MediaInfo();
        try
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".mp3" or ".aac": Id3Reader.Read(path, info); break;
                case ".m4a" or ".m4b" or ".mp4": Mp4MetadataReader.Read(path, info); break;
                case ".flac": VorbisCommentReader.ReadFlac(path, info); break;
                case ".ogg": VorbisCommentReader.ReadOgg(path, info); break;
            }
        }
        catch { /* malformed tags: the file still plays */ }

        // An Audible export names its files after the book, series and ASIN: use them when the tags miss them
        var name = AudibleExport.Parse(Path.GetFileNameWithoutExtension(path));
        info.Asin ??= name.Asin;
        info.Series ??= name.Series;
        info.SeriesNumber ??= name.SeriesNumber;
        return info;
    }
}

/// <summary>
/// MP4/M4A/M4B metadata: iTunes tags (ilst) and chapters, either from a QuickTime chapter track
/// (Apple/iTunes audiobooks) or from a Nero "chpl" box.
/// </summary>
static class Mp4MetadataReader
{
    readonly record struct Box(string Type, int Start, int Length)
    {
        public int End => Start + Length;
    }

    public static void Read(string path, MediaInfo info)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var moov = ReadTopLevelBox(fs, "moov");
        if (moov == null) return;

        ReadTags(moov, info);
        if (!ReadChapterTrack(moov, fs, info.Chapters))
            ReadNeroChapters(moov, info.Chapters);
    }

    static void ReadTags(byte[] moov, MediaInfo info)
    {
        // A file can hold several udta/meta boxes (encoders and tag editors each add their own)
        var items = from udta in Children(moov, 0, moov.Length, "udta")
                    from meta in Children(moov, udta.Start, udta.End, "meta")
                    // ISO "meta" is a FullBox (4 bytes of version/flags before its children); the QuickTime variant is not
                    let metaStart = meta.Length >= 12 && Latin1(moov, meta.Start + 8) == "hdlr" ? meta.Start + 4 : meta.Start
                    from ilst in Children(moov, metaStart, meta.End, "ilst")
                    from item in Boxes(moov, ilst.Start, ilst.End)
                    select item;

        foreach (var item in items)
        {
            if (item.Type == "----")
            {
                // Freeform tag: "mean" (its domain, e.g. com.apple.iTunes), "name" (the key), "data" (the value)
                if (FreeformName(moov, item) is { } key && Child(moov, item.Start, item.End, "data") is { Length: >= 8 } payload)
                    AudibleExport.ApplyTag(info, key, Encoding.UTF8.GetString(moov, payload.Start + 8, payload.Length - 8).Trim('\0', ' '));
                continue;
            }
            if (Child(moov, item.Start, item.End, "data") is not { } data || data.Length < 8) continue;
            if (item.Type == "covr")
            {
                info.Cover ??= moov.AsSpan(data.Start + 8, data.Length - 8).ToArray();
                continue;
            }
            var value = Encoding.UTF8.GetString(moov, data.Start + 8, data.Length - 8).Trim('\0', ' ');
            if (value.Length == 0) continue;
            switch (item.Type)
            {
                case "©nam": info.Title = value; break;
                case "©ART": info.Artist ??= value; break;
                case "aART": info.Artist = value; break;
                case "©alb": info.Album = value; break;
                // Audiobook tools put the narrator in the QuickTime "composer" tag
                case "©wrt": info.Narrator ??= value; break;
            }
        }
    }

    /// <summary>The key of a freeform tag ("----"): its "name" box, e.g. "AUDIBLE_ASIN" inside com.apple.iTunes.</summary>
    static string? FreeformName(byte[] d, Box item)
    {
        if (Child(d, item.Start, item.End, "name") is not { Length: > 4 } name) return null;
        var text = Encoding.UTF8.GetString(d, name.Start + 4, name.Length - 4).Trim('\0', ' ');
        return text.Length > 0 ? text : null;
    }

    /// <summary>QuickTime chapter track: a text track referenced by "tref/chap"; each sample is a chapter title.</summary>
    static bool ReadChapterTrack(byte[] d, FileStream fs, List<Chapter> chapters)
    {
        var traks = Boxes(d, 0, d.Length).Where(b => b.Type == "trak").ToList();
        uint? chapterTrackId = null;
        foreach (var trak in traks)
            if (Child(d, trak.Start, trak.End, "tref") is { } tref && Child(d, tref.Start, tref.End, "chap") is { Length: >= 4 } chap)
                chapterTrackId = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(chap.Start));
        if (chapterTrackId == null) return false;

        foreach (var trak in traks)
        {
            if (Child(d, trak.Start, trak.End, "tkhd") is not { } tkhd) continue;
            int idOffset = tkhd.Start + (d[tkhd.Start] == 1 ? 20 : 12);
            if (BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(idOffset)) != chapterTrackId) continue;

            if (Child(d, trak.Start, trak.End, "mdia") is not { } mdia) return false;
            if (Child(d, mdia.Start, mdia.End, "mdhd") is not { } mdhd) return false;
            uint timescale = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(mdhd.Start + (d[mdhd.Start] == 1 ? 20 : 12)));
            if (timescale == 0) return false;
            if (Child(d, mdia.Start, mdia.End, "minf") is not { } minf) return false;
            if (Child(d, minf.Start, minf.End, "stbl") is not { } stbl) return false;

            var starts = SampleStarts(d, stbl);
            var offsets = SampleOffsets(d, stbl, out var sizes);
            int count = Math.Min(starts.Count, offsets.Count);
            for (int i = 0; i < count; i++)
            {
                chapters.Add(new Chapter(ReadChapterTitle(fs, offsets[i], sizes[i]),
                    TimeSpan.FromSeconds(starts[i] / (double)timescale), TimeSpan.Zero));
            }
            return chapters.Count > 0;
        }
        return false;
    }

    static string ReadChapterTitle(FileStream fs, long offset, int size)
    {
        if (size < 2 || size > 64 * 1024) return "";
        var buf = new byte[size];
        fs.Position = offset;
        if (fs.ReadAtLeast(buf, size, throwOnEndOfStream: false) < size) return "";
        int len = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(buf), size - 2);
        if (len >= 2 && buf[2] == 0xFE && buf[3] == 0xFF) return Encoding.BigEndianUnicode.GetString(buf, 4, len - 2).Trim();
        return Encoding.UTF8.GetString(buf, 2, len).Trim();
    }

    /// <summary>Start of each sample in timescale units, from the time-to-sample table (stts).</summary>
    static List<long> SampleStarts(byte[] d, Box stbl)
    {
        var result = new List<long>();
        if (Child(d, stbl.Start, stbl.End, "stts") is not { } stts) return result;
        uint entries = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(stts.Start + 4));
        long time = 0;
        for (int e = 0; e < entries && stts.Start + 8 + e * 8 + 8 <= stts.End; e++)
        {
            uint samples = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(stts.Start + 8 + e * 8));
            uint delta = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(stts.Start + 12 + e * 8));
            for (uint s = 0; s < samples && result.Count < 10_000; s++)
            {
                result.Add(time);
                time += delta;
            }
        }
        return result;
    }

    /// <summary>File offset of each sample, from the chunk tables (stsc + stco/co64) and sample sizes (stsz).</summary>
    static List<long> SampleOffsets(byte[] d, Box stbl, out List<int> sizes)
    {
        var offsets = new List<long>();
        sizes = [];
        if (Child(d, stbl.Start, stbl.End, "stsz") is not { } stsz || Child(d, stbl.Start, stbl.End, "stsc") is not { } stsc) return offsets;

        uint fixedSize = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(stsz.Start + 4));
        int sampleCount = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(stsz.Start + 8)), 10_000);
        for (int i = 0; i < sampleCount; i++)
            sizes.Add(fixedSize != 0 ? (int)fixedSize : (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(stsz.Start + 12 + i * 4)));

        var chunkOffsets = new List<long>();
        if (Child(d, stbl.Start, stbl.End, "stco") is { } stco)
        {
            uint n = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(stco.Start + 4));
            for (int i = 0; i < n && stco.Start + 8 + i * 4 + 4 <= stco.End; i++)
                chunkOffsets.Add(BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(stco.Start + 8 + i * 4)));
        }
        else if (Child(d, stbl.Start, stbl.End, "co64") is { } co64)
        {
            uint n = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(co64.Start + 4));
            for (int i = 0; i < n && co64.Start + 8 + i * 8 + 8 <= co64.End; i++)
                chunkOffsets.Add((long)BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(co64.Start + 8 + i * 8)));
        }

        uint stscEntries = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(stsc.Start + 4));
        var runs = new List<(int FirstChunk, int SamplesPerChunk)>();
        for (int e = 0; e < stscEntries && stsc.Start + 8 + e * 12 + 12 <= stsc.End; e++)
            runs.Add(((int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(stsc.Start + 8 + e * 12)),
                      (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(stsc.Start + 12 + e * 12))));

        int sample = 0;
        for (int chunk = 1; chunk <= chunkOffsets.Count && sample < sizes.Count; chunk++)
        {
            int perChunk = runs.LastOrDefault(r => r.FirstChunk <= chunk).SamplesPerChunk;
            long offset = chunkOffsets[chunk - 1];
            for (int s = 0; s < perChunk && sample < sizes.Count; s++, sample++)
            {
                offsets.Add(offset);
                offset += sizes[sample];
            }
        }
        return offsets;
    }

    /// <summary>Nero chapters: udta/chpl with 100-ns start times and length-prefixed UTF-8 titles.</summary>
    static void ReadNeroChapters(byte[] d, List<Chapter> chapters)
    {
        var chpl = Children(d, 0, d.Length, "udta")
            .SelectMany(udta => Children(d, udta.Start, udta.End, "chpl"))
            .FirstOrDefault(b => b.Length >= 5);
        if (chpl.Type == null) return;
        int p = chpl.Start + 4;                  // version + flags
        if (d[chpl.Start] != 0) p += 4;          // version 1 has 4 extra bytes
        int count = d[p++];
        for (int i = 0; i < count && p + 9 <= chpl.End; i++)
        {
            long start100ns = (long)BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(p));
            int len = d[p + 8];
            p += 9;
            if (p + len > chpl.End) break;
            chapters.Add(new Chapter(Encoding.UTF8.GetString(d, p, len).Trim(), TimeSpan.FromTicks(start100ns), TimeSpan.Zero));
            p += len;
        }
    }

    /// <summary>Reads the payload of a top-level box, skipping the others (e.g. a large "mdat") without reading them.</summary>
    static byte[]? ReadTopLevelBox(FileStream fs, string wanted)
    {
        var header = new byte[16];
        long pos = 0;
        while (pos + 8 <= fs.Length)
        {
            fs.Position = pos;
            if (fs.ReadAtLeast(header, 8, throwOnEndOfStream: false) < 8) return null;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = Latin1(header, 4);
            int headerSize = 8;
            if (size == 1)
            {
                if (fs.ReadAtLeast(header.AsSpan(8, 8), 8, throwOnEndOfStream: false) < 8) return null;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = fs.Length - pos;
            }
            if (size < headerSize) return null;

            if (type == wanted)
            {
                long length = size - headerSize;
                if (length > 256L * 1024 * 1024) return null;
                var payload = new byte[length];
                fs.Position = pos + headerSize;
                return fs.ReadAtLeast(payload, payload.Length, throwOnEndOfStream: false) == payload.Length ? payload : null;
            }
            pos += size;
        }
        return null;
    }

    static IEnumerable<Box> Boxes(byte[] d, int start, int end)
    {
        int pos = start;
        while (pos + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(pos));
            int headerSize = 8;
            if (size == 1 && pos + 16 <= end)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(pos + 8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = end - pos;
            }
            if (size < headerSize || pos + size > end) yield break;
            yield return new Box(Latin1(d, pos + 4), pos + headerSize, (int)size - headerSize);
            pos += (int)size;
        }
    }

    static Box? Child(byte[] d, int start, int end, string type)
    {
        foreach (var box in Boxes(d, start, end))
            if (box.Type == type) return box;
        return null;
    }

    static IEnumerable<Box> Children(byte[] d, int start, int end, string type) =>
        Boxes(d, start, end).Where(b => b.Type == type);

    // Latin-1 maps byte 0xA9 to '©', as used by iTunes tag names ("©nam", "©ART", ...)
    static string Latin1(byte[] d, int offset) => Encoding.Latin1.GetString(d, offset, 4);
}

/// <summary>
/// Vorbis comments (FLAC and OGG): TITLE/ARTIST/ALBUM and chapters in the common
/// CHAPTER001=HH:MM:SS.mmm / CHAPTER001NAME=Title convention.
/// </summary>
static class VorbisCommentReader
{
    public static void ReadFlac(string path, MediaInfo info)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[10];
        if (fs.ReadAtLeast(header, 10, throwOnEndOfStream: false) < 10) return;
        long pos = 0;
        if (header[0] == 'I' && header[1] == 'D' && header[2] == '3') // FLAC with a leading ID3 tag
            pos = 10 + ((header[6] & 0x7F) << 21 | (header[7] & 0x7F) << 14 | (header[8] & 0x7F) << 7 | (header[9] & 0x7F));

        fs.Position = pos;
        if (fs.ReadAtLeast(header.AsSpan(0, 4), 4, throwOnEndOfStream: false) < 4 || Encoding.ASCII.GetString(header, 0, 4) != "fLaC") return;

        while (true)
        {
            if (fs.ReadAtLeast(header.AsSpan(0, 4), 4, throwOnEndOfStream: false) < 4) return;
            bool last = (header[0] & 0x80) != 0;
            int type = header[0] & 0x7F;
            int length = header[1] << 16 | header[2] << 8 | header[3];
            if (type is 4 or 6) // VORBIS_COMMENT, PICTURE
            {
                var block = new byte[length];
                if (fs.ReadAtLeast(block, length, throwOnEndOfStream: false) < length) return;
                if (type == 4) Apply(ParseCommentBlock(block), info);
                else if (CoverArt.ParseFlacPicture(block) is { } picture && (info.Cover == null || picture.Type == 3))
                    info.Cover = picture.Data; // type 3 = front cover
            }
            else
            {
                fs.Position += length;
            }
            if (last) return;
        }
    }

    public static void ReadOgg(string path, MediaInfo info)
    {
        using var reader = new VorbisReader(path);
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in reader.Tags.All)
            if (values.Count > 0) tags.TryAdd(key, values[0]);
        Apply(tags, info);
        if (tags.TryGetValue("METADATA_BLOCK_PICTURE", out var picture))
        {
            try { info.Cover = CoverArt.ParseFlacPicture(Convert.FromBase64String(picture.Trim()))?.Data; }
            catch (FormatException) { }
        }
    }

    /// <summary>Little-endian layout: vendor length + vendor, count, then "KEY=value" entries.</summary>
    static Dictionary<string, string> ParseCommentBlock(byte[] b)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int p = 0;
        if (b.Length < 8) return tags;
        p += 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(b);
        if (p + 4 > b.Length) return tags;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
        p += 4;
        for (uint i = 0; i < count && p + 4 <= b.Length; i++)
        {
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
            p += 4;
            if (len < 0 || p + len > b.Length) break;
            var entry = Encoding.UTF8.GetString(b, p, len);
            p += len;
            int eq = entry.IndexOf('=');
            if (eq > 0) tags.TryAdd(entry[..eq], entry[(eq + 1)..]);
        }
        return tags;
    }

    static void Apply(Dictionary<string, string> tags, MediaInfo info)
    {
        if (tags.TryGetValue("TITLE", out var title)) info.Title = title;
        if (tags.TryGetValue("ARTIST", out var artist)) info.Artist = artist;
        if (tags.TryGetValue("ALBUM", out var album)) info.Album = album;

        for (int i = 0; i <= 999; i++)
        {
            if (!tags.TryGetValue($"CHAPTER{i:000}", out var time))
            {
                if (i > 1) break; // numbering may start at 000 or 001
                continue;
            }
            if (!TimeSpan.TryParse(time.Trim(), CultureInfo.InvariantCulture, out var start)) continue;
            tags.TryGetValue($"CHAPTER{i:000}NAME", out var name);
            info.Chapters.Add(new Chapter(name?.Trim() ?? "", start, TimeSpan.Zero));
        }
    }
}
