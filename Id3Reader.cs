using System.Text;

namespace aBookPlayer;

/// <summary>Minimal ID3v2.3/2.4 tag reader: title, artist, album and chapters (CHAP frames).</summary>
static class Id3Reader
{
    public static void Read(string path, MediaInfo info)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[10];
        if (fs.ReadAtLeast(header, 10, throwOnEndOfStream: false) < 10) return;
        if (header[0] != 'I' || header[1] != 'D' || header[2] != '3') return;

        int major = header[3];
        if (major is < 3 or > 4) return;
        int flags = header[5];
        int size = SyncSafe(header, 6);

        var tag = new byte[size];
        int read = fs.ReadAtLeast(tag, size, throwOnEndOfStream: false);
        if (read < size) Array.Resize(ref tag, read);

        if (major == 3 && (flags & 0x80) != 0) tag = RemoveUnsync(tag);

        int pos = 0;
        if ((flags & 0x40) != 0) // extended header
            pos = major == 3 ? 4 + BE32(tag, 0) : SyncSafe(tag, 0);

        foreach (var (id, data) in Frames(tag, pos, tag.Length, major, id => id is "TIT2" or "TPE1" or "TALB" or "CHAP"))
        {
            switch (id)
            {
                case "TIT2": info.Title = DecodeText(data); break;
                case "TPE1": info.Artist = DecodeText(data); break;
                case "TALB": info.Album = DecodeText(data); break;
                case "CHAP": ParseChapter(data, major, info.Chapters); break;
            }
        }
    }

    static void ParseChapter(byte[] d, int major, List<Chapter> chapters)
    {
        int i = Array.IndexOf(d, (byte)0);          // null-terminated element ID
        if (i < 0 || i + 17 > d.Length) return;
        i++;
        uint startMs = (uint)BE32(d, i);
        uint endMs = (uint)BE32(d, i + 4);
        i += 16;                                    // start, end, start offset, end offset

        string? title = null;
        foreach (var (_, data) in Frames(d, i, d.Length, major, id => id == "TIT2"))
            title = DecodeText(data);

        chapters.Add(new Chapter(
            title ?? "",
            TimeSpan.FromMilliseconds(startMs),
            endMs == uint.MaxValue ? TimeSpan.Zero : TimeSpan.FromMilliseconds(endMs)));
    }

    static IEnumerable<(string Id, byte[] Data)> Frames(byte[] d, int pos, int end, int major, Func<string, bool> wanted)
    {
        while (pos + 10 <= end)
        {
            if (d[pos] == 0) yield break; // padding
            string id = Encoding.ASCII.GetString(d, pos, 4);
            if (!id.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9')) yield break;

            // Some taggers write non-syncsafe sizes even in v2.4
            bool syncSafe = major == 4 && (d[pos + 4] | d[pos + 5] | d[pos + 6] | d[pos + 7]) < 0x80;
            int size = syncSafe ? SyncSafe(d, pos + 4) : BE32(d, pos + 4);
            int flags = (d[pos + 8] << 8) | d[pos + 9];
            int start = pos + 10;
            if (size < 0 || start + size > end) yield break;
            pos = start + size;

            if (!wanted(id)) continue;

            bool compressedOrEncrypted = major == 3 ? (flags & 0x00C0) != 0 : (flags & 0x000C) != 0;
            if (compressedOrEncrypted) continue;

            int s = start, len = size;
            if (major == 3 && (flags & 0x0020) != 0) { s++; len--; }         // group id
            if (major == 4 && (flags & 0x0040) != 0) { s++; len--; }         // group id
            if (major == 4 && (flags & 0x0001) != 0) { s += 4; len -= 4; }   // data length indicator
            if (len < 0) continue;

            var data = new byte[len];
            Buffer.BlockCopy(d, s, data, 0, len);
            if (major == 4 && (flags & 0x0002) != 0) data = RemoveUnsync(data);
            yield return (id, data);
        }
    }

    static string DecodeText(byte[] d)
    {
        if (d.Length < 2) return "";
        int enc = d[0];
        string s = enc switch
        {
            0 => Encoding.Latin1.GetString(d, 1, d.Length - 1),
            1 => DecodeUtf16WithBom(d, 1),
            2 => Encoding.BigEndianUnicode.GetString(d, 1, d.Length - 1),
            3 => Encoding.UTF8.GetString(d, 1, d.Length - 1),
            _ => "",
        };
        // v2.4 may hold several null-separated values: take the first non-empty one
        return s.Split('\0').Select(p => p.Trim()).FirstOrDefault(p => p.Length > 0) ?? "";
    }

    static string DecodeUtf16WithBom(byte[] d, int offset)
    {
        int len = d.Length - offset;
        if (len >= 2 && d[offset] == 0xFE && d[offset + 1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(d, offset + 2, len - 2);
        if (len >= 2 && d[offset] == 0xFF && d[offset + 1] == 0xFE)
            return Encoding.Unicode.GetString(d, offset + 2, len - 2);
        return Encoding.Unicode.GetString(d, offset, len);
    }

    static byte[] RemoveUnsync(byte[] d)
    {
        var result = new List<byte>(d.Length);
        for (int i = 0; i < d.Length; i++)
        {
            result.Add(d[i]);
            if (d[i] == 0xFF && i + 1 < d.Length && d[i + 1] == 0x00) i++;
        }
        return [.. result];
    }

    static int SyncSafe(byte[] d, int i) =>
        (d[i] & 0x7F) << 21 | (d[i + 1] & 0x7F) << 14 | (d[i + 2] & 0x7F) << 7 | (d[i + 3] & 0x7F);

    static int BE32(byte[] d, int i) =>
        d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3];
}
