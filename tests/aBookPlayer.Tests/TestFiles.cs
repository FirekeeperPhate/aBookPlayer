using System.Buffers.Binary;
using System.Text;

namespace aBookPlayer.Tests;

/// <summary>Builds small synthetic media files for the tests (no real audio needed for metadata).</summary>
static class TestFiles
{
    public static string TempPath(string extension)
    {
        var dir = Path.Combine(Path.GetTempPath(), "aBookPlayer.Tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{Guid.NewGuid():N}{extension}");
    }

    // ---------- MP4 boxes ----------

    public static byte[] Box(string type, params byte[][] parts)
    {
        int length = 8 + parts.Sum(p => p.Length);
        var box = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)length);
        Encoding.Latin1.GetBytes(type, 0, 4, box, 4);
        int offset = 8;
        foreach (var part in parts) { part.CopyTo(box, offset); offset += part.Length; }
        return box;
    }

    public static byte[] U32(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); return b; }
    public static byte[] U64(ulong value) { var b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, value); return b; }
    public static byte[] Zeros(int count) => new byte[count];

    public static byte[] ITunesTags(string title, string artist, string album) =>
        Box("meta", Zeros(4),
            Box("hdlr", Zeros(4), Zeros(4), Encoding.ASCII.GetBytes("mdir"), Zeros(12), Zeros(1)),
            Box("ilst",
                Box("©nam", Box("data", U32(1), Zeros(4), Encoding.UTF8.GetBytes(title))),
                Box("©ART", Box("data", U32(1), Zeros(4), Encoding.UTF8.GetBytes(artist))),
                Box("©alb", Box("data", U32(1), Zeros(4), Encoding.UTF8.GetBytes(album)))));

    /// <summary>An iTunes tag item, e.g. Item("©nam", "Title").</summary>
    public static byte[] Mp4Item(string type, string value) =>
        Box(type, Box("data", U32(1), Zeros(4), Encoding.UTF8.GetBytes(value)));

    /// <summary>A freeform tag ("----"): its domain ("mean"), key ("name") and value ("data").</summary>
    public static byte[] Mp4Freeform(string name, string value) =>
        Box("----",
            Box("mean", Zeros(4), Encoding.ASCII.GetBytes("com.apple.iTunes")),
            Box("name", Zeros(4), Encoding.ASCII.GetBytes(name)),
            Box("data", U32(1), Zeros(4), Encoding.UTF8.GetBytes(value)));

    /// <summary>A metadata-only MP4 with the given tag items.</summary>
    public static byte[] Mp4Tags(params byte[][] items) =>
        Box("meta", Zeros(4),
            Box("hdlr", Zeros(4), Zeros(4), Encoding.ASCII.GetBytes("mdir"), Zeros(12), Zeros(1)),
            Box("ilst", [.. items.SelectMany(i => i)]));

    /// <summary>Nero chapters (udta/chpl, version 1).</summary>
    public static byte[] NeroChapters(params (string Title, int StartMs)[] chapters)
    {
        var chpl = new List<byte> { 1, 0, 0, 0, 0, 0, 0, 0, (byte)chapters.Length };
        foreach (var (title, ms) in chapters)
        {
            var text = Encoding.UTF8.GetBytes(title);
            chpl.AddRange(U64((ulong)ms * 10_000));
            chpl.Add((byte)text.Length);
            chpl.AddRange(text);
        }
        return Box("chpl", [.. chpl]);
    }

    /// <summary>
    /// A metadata-only MP4: an "audio" track referencing a QuickTime chapter text track via tref/chap,
    /// iTunes tags, and the chapter title samples stored right after the moov box.
    /// </summary>
    public static byte[] QuickTimeChapterFile((string Title, int StartMs)[] chapters, int durationMs, byte[]? extraUdta = null)
    {
        const uint audioId = 1, chapterId = 2;
        var samples = chapters.Select(c =>
        {
            var text = Encoding.UTF8.GetBytes(c.Title);
            return (byte[])[(byte)(text.Length >> 8), (byte)text.Length, .. text];
        }).ToArray();
        var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("M4B "), Zeros(4), Encoding.ASCII.GetBytes("M4B isom"));

        byte[] Moov(long samplesOffset)
        {
            var offsets = new List<byte>();
            long offset = samplesOffset;
            foreach (var s in samples) { offsets.AddRange(U32((uint)offset)); offset += s.Length; }
            var stts = new List<byte>(); stts.AddRange(Zeros(4)); stts.AddRange(U32((uint)chapters.Length));
            for (int i = 0; i < chapters.Length; i++)
            {
                int end = i + 1 < chapters.Length ? chapters[i + 1].StartMs : durationMs;
                stts.AddRange(U32(1)); stts.AddRange(U32((uint)(end - chapters[i].StartMs)));
            }
            var stsz = new List<byte>(); stsz.AddRange(Zeros(4)); stsz.AddRange(U32(0)); stsz.AddRange(U32((uint)samples.Length));
            foreach (var s in samples) stsz.AddRange(U32((uint)s.Length));

            var audioTrak = Box("trak", Box("tkhd", Zeros(12), U32(audioId), Zeros(68)), Box("tref", Box("chap", U32(chapterId))));
            var chapterTrak = Box("trak",
                Box("tkhd", Zeros(12), U32(chapterId), Zeros(68)),
                Box("mdia",
                    Box("mdhd", Zeros(12), U32(1000), U32((uint)durationMs), Zeros(4)),
                    Box("minf", Box("stbl",
                        Box("stts", [.. stts]),
                        Box("stsz", [.. stsz]),
                        Box("stsc", Zeros(4), U32(1), U32(1), U32(1), U32(1)),
                        Box("stco", [.. Zeros(4), .. U32((uint)samples.Length), .. offsets])))));
            // An encoder's own (empty) udta first, as Windows writes it: tags live in a second one
            return Box("moov", audioTrak, chapterTrak,
                Box("udta", Box("Xtra", Zeros(0))),
                Box("udta", ITunesTags("QT Title", "QT Artist", "QT Album"), extraUdta ?? []));
        }

        int moovLength = Moov(0).Length;
        return [.. ftyp, .. Moov(ftyp.Length + moovLength), .. samples.SelectMany(s => s)];
    }

    // ---------- ID3v2.3 ----------

    public static byte[] Id3Frame(string id, byte[] body)
    {
        var frame = new List<byte>(Encoding.ASCII.GetBytes(id));
        frame.AddRange(U32((uint)body.Length));
        frame.AddRange([0, 0]);
        frame.AddRange(body);
        return [.. frame];
    }

    /// <summary>A TXXX frame: encoding, description, 0-terminator, value (as AAXClean writes the Audible tags).</summary>
    public static byte[] Id3UserText(string description, string value) =>
        Id3Frame("TXXX", [0, .. Encoding.Latin1.GetBytes(description), 0, .. Encoding.Latin1.GetBytes(value)]);

    public static byte[] Utf16Text(string text) => [1, .. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text), 0, 0];

    public static byte[] Id3Chapter(string elementId, uint startMs, uint endMs, string title) =>
        Id3Frame("CHAP", [.. Encoding.ASCII.GetBytes(elementId), 0, .. U32(startMs), .. U32(endMs),
            .. U32(uint.MaxValue), .. U32(uint.MaxValue), .. Id3Frame("TIT2", Utf16Text(title))]);

    public static byte[] Id3Tag(params byte[][] frames)
    {
        byte[] body = [.. frames.SelectMany(f => f), .. new byte[64]]; // + padding
        int size = body.Length;
        byte[] header = [(byte)'I', (byte)'D', (byte)'3', 3, 0, 0,
            (byte)((size >> 21) & 0x7F), (byte)((size >> 14) & 0x7F), (byte)((size >> 7) & 0x7F), (byte)(size & 0x7F)];
        return [.. header, .. body];
    }

    // ---------- FLAC metadata ----------

    /// <summary>"fLaC" + STREAMINFO + VORBIS_COMMENT (no audio frames: enough for the metadata reader).</summary>
    public static byte[] FlacWithComments(params string[] comments)
    {
        var vc = new List<byte>();
        var vendor = Encoding.UTF8.GetBytes("tests");
        vc.AddRange(BitConverter.GetBytes(vendor.Length)); vc.AddRange(vendor);
        vc.AddRange(BitConverter.GetBytes(comments.Length));
        foreach (var c in comments) { var b = Encoding.UTF8.GetBytes(c); vc.AddRange(BitConverter.GetBytes(b.Length)); vc.AddRange(b); }
        return [.. Encoding.ASCII.GetBytes("fLaC"),
            0x00, 0, 0, 34, .. new byte[34],                                            // STREAMINFO
            0x84, (byte)(vc.Count >> 16), (byte)(vc.Count >> 8), (byte)vc.Count, .. vc]; // VORBIS_COMMENT (last)
    }
}
