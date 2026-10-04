using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;

namespace aBookPlayer;

/// <summary>
/// One file out of a zip on a web server, without downloading the rest of it: the zip's directory is at its end,
/// and says where each file is, so only those bytes are asked for. (DirectML.dll is 8 MB of a 200 MB package.)
/// The server must accept ranges; no ZIP64.
/// </summary>
static class RemoteZip
{
    public static async Task ExtractAsync(HttpClient http, string url, string entry, string target, IProgress<long>? progress, CancellationToken ct)
    {
        // The end of the file: the "end of central directory" record is in its last 64 KB
        byte[] tail;
        long length;
        using (var request = new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Range = new RangeHeaderValue(null, 66_000) } })
        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            length = response.Content.Headers.ContentRange?.Length ?? throw new IOException("The server does not allow downloading a part of the file.");
            tail = await response.Content.ReadAsByteArrayAsync(ct);
        }
        int end = tail.Length - 22;
        while (end >= 0 && BitConverter.ToUInt32(tail, end) != 0x06054b50) end--;
        if (end < 0) throw new InvalidDataException("Not a zip file.");
        long directorySize = BitConverter.ToUInt32(tail, end + 12), directoryOffset = BitConverter.ToUInt32(tail, end + 16);
        if (directoryOffset == uint.MaxValue) throw new InvalidDataException("ZIP64 files are not supported.");

        var directory = await RangeAsync(http, url, directoryOffset, directorySize, ct);
        long headerOffset = -1, compressed = 0;
        int method = 0;
        for (int p = 0; p + 46 <= directory.Length && BitConverter.ToUInt32(directory, p) == 0x02014b50;)
        {
            int nameLength = BitConverter.ToUInt16(directory, p + 28), extra = BitConverter.ToUInt16(directory, p + 30), comment = BitConverter.ToUInt16(directory, p + 32);
            string name = Encoding.UTF8.GetString(directory, p + 46, nameLength);
            if (string.Equals(name, entry, StringComparison.OrdinalIgnoreCase))
            {
                method = BitConverter.ToUInt16(directory, p + 10);
                compressed = BitConverter.ToUInt32(directory, p + 20);
                headerOffset = BitConverter.ToUInt32(directory, p + 42);
                break;
            }
            p += 46 + nameLength + extra + comment;
        }
        if (headerOffset < 0) throw new FileNotFoundException($"\"{entry}\" is not in the package.");
        if (method is not (0 or 8)) throw new InvalidDataException("Unknown compression.");

        // The file's own header (its name and extra field come before the data)
        var header = await RangeAsync(http, url, headerOffset, 30, ct);
        long dataOffset = headerOffset + 30 + BitConverter.ToUInt16(header, 26) + BitConverter.ToUInt16(header, 28);
        var partial = target + ".part";
        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Range = new RangeHeaderValue(dataOffset, dataOffset + compressed - 1) } })
            using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var source = new CountingStream(await response.Content.ReadAsStreamAsync(ct), progress);
                await using var output = File.Create(partial);
                if (method == 8)
                {
                    await using var inflate = new DeflateStream(source, CompressionMode.Decompress);
                    await inflate.CopyToAsync(output, ct);
                }
                else await source.CopyToAsync(output, ct);
                if (source.Count != compressed) throw new IOException("The download was incomplete. Please try again.");
            }
            File.Move(partial, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    static async Task<byte[]> RangeAsync(HttpClient http, string url, long offset, long count, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Range = new RangeHeaderValue(offset, offset + count - 1) } };
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return bytes.Length == count ? bytes : throw new IOException("The server does not allow downloading a part of the file.");
    }

    /// <summary>Counts (and reports) the bytes read through it.</summary>
    sealed class CountingStream(Stream inner, IProgress<long>? progress) : Stream
    {
        public long Count { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Counted(await inner.ReadAsync(buffer, ct));
        int Counted(int read)
        {
            Count += read;
            progress?.Report(Count);
            return read;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
