using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace aBookPlayer;

// The PC's library shared with phones on the same network: the Windows app serves its books (details, audio files,
// covers, subtitles) over HTTP, the Android app lists them and plays them streaming. Plain HTTP on the home network,
// every request carrying the access key the PC shows.

/// <summary>The server introducing itself (and proving the key is right).</summary>
sealed record RemoteHello(string App, int Protocol, string Machine, string Version);

/// <summary>A book in the list: what the library shows.</summary>
sealed record RemoteBookSummary(string Id, string Title, string? Author, string? Series, int? Number,
    double DurationSeconds, double PositionSeconds, DateTime PositionUpdated, bool Finished, string? SyncKey);

/// <summary>A book to play: its files with their lengths (the book is them joined), chapters, and where the PC is in it.</summary>
sealed record RemoteBook(string Id, string Title, string? Author, string? Series, int? Number, string? Asin, string? SyncKey,
    List<RemotePart> Parts, List<RemoteChapter> Chapters, bool HasCover, bool HasSubtitles,
    double PositionSeconds, DateTime PositionUpdated, bool Finished);

sealed record RemotePart(string Name, double Seconds);
sealed record RemoteChapter(string Title, double Start);

/// <summary>Where a phone is in a book (sent to the PC, which takes it when it is newer than its own).</summary>
sealed record RemotePosition(double Seconds, DateTime Updated, bool Finished, string Machine);

/// <summary>A PC answering a phone looking for aBookPlayer on the network (no key needed: it says only who and where).</summary>
sealed record RemoteFound(string App, int Protocol, string Machine, int Port);

[JsonSerializable(typeof(RemoteHello))]
[JsonSerializable(typeof(List<RemoteBookSummary>))]
[JsonSerializable(typeof(RemoteBook))]
[JsonSerializable(typeof(RemotePosition))]
[JsonSerializable(typeof(RemoteFound))]
sealed partial class RemoteJson : JsonSerializerContext;

/// <summary>What the server serves: the PC's books (the Windows app implements it; tests use their own).</summary>
interface IRemoteLibrary
{
    string Machine { get; }
    IReadOnlyList<RemoteBookSummary> Books();
    RemoteBook? Book(string id);
    /// <summary>The file of part <paramref name="index"/>, or null.</summary>
    string? PartFile(string id, int index);
    byte[]? Cover(string id);
    string? SubtitleFile(string id);
    /// <summary>A phone's position in the book; false when there is no such book.</summary>
    bool SetPosition(string id, RemotePosition position);
}

static class RemoteIds
{
    /// <summary>A book's id in the protocol: stable while it stays where it is, and says nothing about the PC's folders.</summary>
    public static string For(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))[..16].ToLowerInvariant();
}

/// <summary>The key a phone needs to read the PC's library: short enough to type, in letters that cannot be mistaken.</summary>
static class AccessKey
{
    const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>"K7PX-M2QA-9TRD": 12 characters of 32 (60 bits).</summary>
    public static string New()
    {
        var chars = new char[12];
        for (int i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        var key = new string(chars);
        return $"{key[..4]}-{key[4..8]}-{key[8..]}";
    }

    /// <summary>As typed or as shown: dashes, spaces and case do not matter.</summary>
    public static string Normalize(string key) => new(key.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static bool Matches(string? given, string expected) =>
        given != null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Normalize(given)), Encoding.ASCII.GetBytes(Normalize(expected)));
}

/// <summary>
/// A small HTTP server for the library: GET (and HEAD), plus the phone's position posted; one request per connection,
/// byte ranges for the audio (a player seeks by asking for the part of the file it needs). No ASP.NET: the Windows
/// app's lighter setup only needs the desktop runtime. It also answers phones looking for it on the network (UDP).
/// </summary>
sealed class LibraryServer : IDisposable
{
    public const int Protocol = 1;
    public const int DefaultPort = 52780;
    const int MaxHeaderBytes = 16 * 1024;

    readonly IRemoteLibrary _library;
    readonly string _key;
    readonly string _version;
    readonly CancellationTokenSource _stop = new();
    TcpListener? _listener;

    public LibraryServer(IRemoteLibrary library, string key, string version)
    {
        _library = library;
        _key = key;
        _version = version;
    }

    /// <summary>The port listened on (the one asked for, or the one the system chose for 0).</summary>
    public int Port { get; private set; }

    /// <summary>Listens on every network of the PC (IPv4 and IPv6). Throws if the port is taken.</summary>
    public void Start(int port, int discoveryPort = DiscoveryPort)
    {
        try
        {
            _listener = new TcpListener(IPAddress.IPv6Any, port);
            _listener.Server.DualMode = true;
            _listener.Start();
        }
        catch (SocketException) when (_listener?.Server.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // IPv6 turned off on this PC: IPv4 only
            _listener.Stop();
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
        }
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync(_listener, _stop.Token);
        StartAnswering(discoveryPort);
    }

    /// <summary>Where phones look for PCs (UDP, always this port: the answer says the library's own).</summary>
    public const int DiscoveryPort = DefaultPort;
    internal const string DiscoveryQuestion = "aBookPlayer?";

    UdpClient? _udp;

    /// <summary>Answers "aBookPlayer?" broadcast by a phone with this PC's name and port. Skipped if the port is taken.</summary>
    internal void StartAnswering(int port)
    {
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port)) { EnableBroadcast = true };
        }
        catch (SocketException)
        {
            return; // another program (or another server in the tests): phones then type the address
        }
        var udp = _udp;
        var answer = JsonSerializer.SerializeToUtf8Bytes(new RemoteFound("aBookPlayer", Protocol, _library.Machine, Port), RemoteJson.Default.RemoteFound);
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var question = await udp.ReceiveAsync(_stop.Token);
                    if (Encoding.ASCII.GetString(question.Buffer) == DiscoveryQuestion)
                        await udp.SendAsync(answer, question.RemoteEndPoint, _stop.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { /* one bad datagram: go on */ }
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener?.Stop(); } catch { /* already stopped */ }
        _udp?.Dispose();
    }

    async Task AcceptAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { return; } // stopped
            _ = Task.Run(() => ServeAsync(client, ct));
        }
    }

    sealed record Request(string Method, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers, byte[] Body);

    /// <summary>A position is a few dozen bytes: anything longer is not one.</summary>
    const int MaxBodyBytes = 8 * 1024;

    async Task ServeAsync(TcpClient client, CancellationToken stop)
    {
        using var _ = client;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            // The request's head must come quickly; the answer (a whole audio file) may take as long as it takes
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var request = await ReadRequestAsync(stream, timeout.Token);
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            if (request == null) return;
            await RespondAsync(stream, request, timeout.Token);
            // A clean end: the phone reads the answer to its last byte before the connection goes (closing at once
            // can reset it, and the phone gets an error instead of the end of the answer)
            client.Client.Shutdown(SocketShutdown.Send);
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var rest = new byte[512];
            while (await stream.ReadAsync(rest, drain.Token) > 0) { }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The phone went away (a seek closes the connection mid-file), or the server stopped
        }
        catch (Exception)
        {
            // A book that cannot be read (damaged, being copied): the phone gets an error, the server goes on
            try { await SendTextAsync(client.GetStream(), 500, "Internal Server Error", "The book could not be read", head: false, stop); }
            catch { /* the answer had already started */ }
        }
    }

    static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes];
        int length = 0;
        while (true)
        {
            if (length == buffer.Length) return null; // too long: not ours
            int read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) return null;
            length += read;
            int end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                var lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
                var first = lines[0].Split(' ');
                if (first.Length != 3) return null;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                    if (line.IndexOf(':') is var colon and > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                var target = first[1];
                int q = target.IndexOf('?');
                var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (q >= 0)
                    foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                        if (pair.IndexOf('=') is var eq and > 0) query[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
                var path = Uri.UnescapeDataString(q >= 0 ? target[..q] : target);
                // A body (a position posted): what came with the head, then the rest
                var body = Array.Empty<byte>();
                if (headers.TryGetValue("Content-Length", out var size) && long.TryParse(size, out var bodyLength) && bodyLength > 0)
                {
                    if (bodyLength > MaxBodyBytes) return null;
                    body = new byte[bodyLength];
                    int have = Math.Min(length - (end + 4), (int)bodyLength);
                    Array.Copy(buffer, end + 4, body, 0, have);
                    while (have < bodyLength)
                    {
                        int more = await stream.ReadAsync(body.AsMemory(have), ct);
                        if (more == 0) return null;
                        have += more;
                    }
                }
                return new Request(first[0].ToUpperInvariant(), path, query, headers, body);
            }
        }
    }

    async Task RespondAsync(NetworkStream stream, Request request, CancellationToken ct)
    {
        if (request.Method is not ("GET" or "HEAD" or "POST"))
        {
            await SendTextAsync(stream, 405, "Method Not Allowed", "Only GET and POST", head: false, ct);
            return;
        }
        bool head = request.Method == "HEAD";
        // The key in the Authorization header (the app), or ?key= (a browser, to try it)
        var given = request.Headers.TryGetValue("Authorization", out var auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth["Bearer ".Length..]
            : request.Query.GetValueOrDefault("key");
        if (!AccessKey.Matches(given, _key))
        {
            await Task.Delay(500, ct); // guessing keys is slow
            await SendTextAsync(stream, 401, "Unauthorized", "Wrong or missing access key", head, ct);
            return;
        }

        var parts = request.Path.Trim('/').Split('/');
        // Only the position is posted; everything else is read
        if (request.Method == "POST")
        {
            if (parts is ["api", "books", var bookId, "position"])
            {
                RemotePosition? position = null;
                try { position = JsonSerializer.Deserialize(request.Body, RemoteJson.Default.RemotePosition); }
                catch (JsonException) { /* not a position */ }
                if (position == null) await SendTextAsync(stream, 400, "Bad Request", "Not a position", head: false, ct);
                else if (_library.SetPosition(bookId, position)) await SendAsync(stream, 204, "No Content", "text/plain", [], head: false, ct);
                else await SendTextAsync(stream, 404, "Not Found", "Not found", head: false, ct);
            }
            else await SendTextAsync(stream, 405, "Method Not Allowed", "Only GET", head: false, ct);
            return;
        }
        switch (parts)
        {
            case ["connect"]:
                // The QR code shown by the PC opens this page on the phone: its button opens the app, connected
                var html = ConnectPage(request.Headers.GetValueOrDefault("Host") ?? $"localhost:{Port}", given!);
                await SendAsync(stream, 200, "OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), head, ct);
                return;
            case ["api", "hello"]:
                await SendJsonAsync(stream, new RemoteHello("aBookPlayer", Protocol, _library.Machine, _version), RemoteJson.Default.RemoteHello, head, ct);
                return;
            case ["api", "library"]:
                await SendJsonAsync(stream, _library.Books().ToList(), RemoteJson.Default.ListRemoteBookSummary, head, ct);
                return;
            case ["api", "books", var id] when _library.Book(id) is { } book:
                await SendJsonAsync(stream, book, RemoteJson.Default.RemoteBook, head, ct);
                return;
            case ["api", "books", var id, "parts", var n] when int.TryParse(n, out var index) && _library.PartFile(id, index) is { } file:
                await SendFileAsync(stream, file, request.Headers.GetValueOrDefault("Range"), head, ct);
                return;
            case ["api", "books", var id, "cover"] when _library.Cover(id) is { Length: > 0 } cover:
                await SendAsync(stream, 200, "OK", ImageType(cover), cover, head, ct);
                return;
            case ["api", "books", var id, "subtitles"] when _library.SubtitleFile(id) is { } srt:
                await SendFileAsync(stream, srt, null, head, ct);
                return;
            default:
                await SendTextAsync(stream, 404, "Not Found", "Not found", head, ct);
                return;
        }
    }

    /// <summary>The link that opens the Android app, connected to this PC ("abookplayer://connect?address=…&amp;key=…").</summary>
    public static string AppLink(string address, string key) =>
        $"abookplayer://connect?address={Uri.EscapeDataString(address)}&key={Uri.EscapeDataString(AccessKey.Normalize(key))}";

    /// <summary>
    /// What the phone's browser shows after scanning the PC's QR code: a button that opens aBookPlayer (Chrome's
    /// "intent:" link, which says where to get the app when it is not installed).
    /// </summary>
    string ConnectPage(string host, string key)
    {
        var link = AppLink(host, key);
        var intent = "intent://" + link["abookplayer://".Length..] + "#Intent;scheme=abookplayer;package=io.github.marcotrombetta.abookplayer;"
                     + "S.browser_fallback_url=" + Uri.EscapeDataString("https://github.com/MarcoTrombetta/aBookPlayer/releases/latest") + ";end";
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>aBookPlayer</title><style>
            body { background: #16161A; color: #EDEDF0; font-family: sans-serif; text-align: center; padding: 48px 24px; }
            a { display: inline-block; margin-top: 24px; padding: 14px 28px; border-radius: 8px; background: #4C9BFF; color: #fff; text-decoration: none; font-size: 18px; }
            p { color: #A0A0AA; }</style></head><body>
            <h2>{{WebUtility.HtmlEncode(_library.Machine)}}</h2>
            <p>Open aBookPlayer on this phone, connected to this PC's library.</p>
            <a href="{{WebUtility.HtmlEncode(intent)}}">Open aBookPlayer</a>
            </body></html>
            """;
    }

    static string ImageType(byte[] bytes) => bytes is [0x89, 0x50, ..] ? "image/png" : "image/jpeg";

    static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".m4a" or ".m4b" or ".mp4" => "audio/mp4",
        ".aac" => "audio/aac",
        ".ogg" or ".oga" or ".opus" => "audio/ogg",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".wma" => "audio/x-ms-wma",
        ".srt" or ".vtt" or ".txt" => "text/plain",
        _ => "application/octet-stream",
    };

    /// <summary>A file, whole or the range asked for ("bytes=1000-", "bytes=1000-1999", "bytes=-500").</summary>
    static async Task SendFileAsync(NetworkStream stream, string file, string? range, bool head, CancellationToken ct)
    {
        await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
        long length = fs.Length;
        long start = 0, end = length - 1;
        int status = 200;
        if (range != null && !range.Contains(','))
        {
            if (!TryParseRange(range, length, out start, out end))
            {
                await WriteHeadAsync(stream, 416, "Range Not Satisfiable", ContentType(file), 0, ct, $"Content-Range: bytes */{length}");
                return;
            }
            status = 206;
        }
        long count = Math.Max(0, end - start + 1);
        await WriteHeadAsync(stream, status, status == 206 ? "Partial Content" : "OK", ContentType(file), count, ct,
            status == 206 ? $"Content-Range: bytes {start}-{end}/{length}" : null);
        if (head || count == 0) return;
        fs.Position = start;
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            int read = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), ct);
            if (read == 0) break;
            await stream.WriteAsync(buffer.AsMemory(0, read), ct);
            count -= read;
        }
    }

    internal static bool TryParseRange(string range, long length, out long start, out long end)
    {
        start = 0;
        end = length - 1;
        if (!range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || length == 0) return false;
        var spec = range["bytes=".Length..].Trim();
        int dash = spec.IndexOf('-');
        if (dash < 0) return false;
        var from = spec[..dash].Trim();
        var to = spec[(dash + 1)..].Trim();
        if (from.Length == 0)
        {
            // The last n bytes
            if (!long.TryParse(to, out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, length - suffix);
            return true;
        }
        if (!long.TryParse(from, out start) || start < 0 || start >= length) return false;
        if (to.Length > 0)
        {
            if (!long.TryParse(to, out end) || end < start) return false;
            end = Math.Min(end, length - 1);
        }
        return true;
    }

    static Task SendJsonAsync<T>(NetworkStream stream, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, bool head, CancellationToken ct) =>
        SendAsync(stream, 200, "OK", "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, type), head, ct);

    static Task SendTextAsync(NetworkStream stream, int status, string reason, string text, bool head, CancellationToken ct) =>
        SendAsync(stream, status, reason, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), head, ct);

    static async Task SendAsync(NetworkStream stream, int status, string reason, string type, byte[] body, bool head, CancellationToken ct)
    {
        await WriteHeadAsync(stream, status, reason, type, body.Length, ct);
        if (!head) await stream.WriteAsync(body, ct);
    }

    static async Task WriteHeadAsync(NetworkStream stream, int status, string reason, string type, long length, CancellationToken ct, string? extra = null)
    {
        var head = new StringBuilder()
            .Append($"HTTP/1.1 {status} {reason}\r\n")
            .Append($"Content-Type: {type}\r\n")
            .Append($"Content-Length: {length}\r\n")
            .Append("Accept-Ranges: bytes\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n");
        if (extra != null) head.Append(extra).Append("\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
    }
}

/// <summary>The phone's side: reads a PC's library, and gives the player the addresses of its files.</summary>
sealed class RemoteLibraryClient : IDisposable
{
    readonly HttpClient _http;

    /// <param name="address">"192.168.1.20:52780", or just the host (the default port).</param>
    public RemoteLibraryClient(string address, string key, HttpMessageHandler? handler = null)
    {
        Address = NormalizeAddress(address);
        Key = AccessKey.Normalize(key);
        BaseUri = new Uri($"http://{Address}/");
        _http = new HttpClient(handler ?? new HttpClientHandler()) { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        // The server answers one request per connection: never send one on a connection it is closing
        _http.DefaultRequestHeaders.ConnectionClose = true;
    }

    public string Address { get; }
    public string Key { get; }
    public Uri BaseUri { get; }

    /// <summary>The header every request carries (the player adds it to its own).</summary>
    public string Authorization => "Bearer " + Key;

    /// <summary>"host:port", the default port added when missing. Throws <see cref="FormatException"/> for something else.</summary>
    public static string NormalizeAddress(string address)
    {
        var text = address.Trim();
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) text = text["http://".Length..];
        text = text.TrimEnd('/');
        if (!Uri.TryCreate("http://" + text, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/" || uri.Host.Length == 0)
            throw new FormatException($"\"{address}\" is not an address such as 192.168.1.20:{LibraryServer.DefaultPort}.");
        return $"{uri.Host}:{(uri.IsDefaultPort && !text.EndsWith(":80") ? LibraryServer.DefaultPort : uri.Port)}";
    }

    public Task<RemoteHello> HelloAsync(CancellationToken ct = default) => GetJsonAsync("api/hello", RemoteJson.Default.RemoteHello, ct);
    public Task<List<RemoteBookSummary>> LibraryAsync(CancellationToken ct = default) => GetJsonAsync("api/library", RemoteJson.Default.ListRemoteBookSummary, ct);
    public Task<RemoteBook> BookAsync(string id, CancellationToken ct = default) => GetJsonAsync($"api/books/{id}", RemoteJson.Default.RemoteBook, ct);

    public Task<byte[]?> CoverAsync(string id, CancellationToken ct = default) => GetBytesAsync($"api/books/{id}/cover", ct);
    public Task<byte[]?> SubtitlesAsync(string id, CancellationToken ct = default) => GetBytesAsync($"api/books/{id}/subtitles", ct);

    public Uri PartUri(string id, int index) => new(BaseUri, $"api/books/{id}/parts/{index}");

    /// <summary>Tells the PC where this phone is in a book (it keeps it when it is newer than its own).</summary>
    public async Task SendPositionAsync(string id, RemotePosition position, CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(position, RemoteJson.Default.RemotePosition));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.PostAsync($"api/books/{id}/position", content, ct);
        Check(response);
    }

    /// <summary>
    /// The PCs sharing their library on this network: asked by a broadcast, their answers gathered for
    /// <paramref name="wait"/>. Each comes with the address to connect to ("192.168.1.20:52780").
    /// </summary>
    public static async Task<List<(string Address, RemoteFound Pc)>> DiscoverAsync(TimeSpan wait, IEnumerable<IPAddress>? broadcasts = null,
        int port = LibraryServer.DiscoveryPort, CancellationToken ct = default)
    {
        var found = new List<(string, RemoteFound)>();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        var question = Encoding.ASCII.GetBytes(LibraryServer.DiscoveryQuestion);
        foreach (var target in (broadcasts ?? []).Append(IPAddress.Broadcast).Distinct())
        {
            try { await udp.SendAsync(question, new IPEndPoint(target, port), ct); }
            catch (SocketException) { /* a network that does not allow it: the others may */ }
        }
        using var until = CancellationTokenSource.CreateLinkedTokenSource(ct);
        until.CancelAfter(wait);
        try
        {
            while (true)
            {
                var answer = await udp.ReceiveAsync(until.Token);
                RemoteFound? pc = null;
                try { pc = JsonSerializer.Deserialize(answer.Buffer, RemoteJson.Default.RemoteFound); }
                catch (JsonException) { /* not ours */ }
                if (pc is { App: "aBookPlayer" })
                {
                    var address = $"{answer.RemoteEndPoint.Address.MapToIPv4()}:{pc.Port}";
                    // The same PC answering through two of its networks: once
                    if (!found.Any(f => f.Item2.Machine == pc.Machine && f.Item2.Port == pc.Port)) found.Add((address, pc));
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* the time to wait is over */ }
        return found;
    }

    /// <summary>The address and key in a link made by <see cref="LibraryServer.AppLink"/>, or null for another link.</summary>
    public static (string Address, string Key)? ParseAppLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != "abookplayer" || uri.Host != "connect") return null;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]), StringComparer.OrdinalIgnoreCase);
        return query.TryGetValue("address", out var address) && query.TryGetValue("key", out var key) && address.Length > 0 && key.Length > 0
            ? (address, key)
            : null;
    }

    /// <summary>
    /// Copies a part of the book into <paramref name="file"/>, going on from where an earlier attempt stopped (the
    /// bytes already there are not asked again). <paramref name="progress"/> gets the bytes written so far and the
    /// part's size.
    /// </summary>
    public async Task DownloadPartAsync(string id, int index, string file, Action<long, long>? progress = null, CancellationToken ct = default)
    {
        long have = File.Exists(file) ? new FileInfo(file).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, PartUri(id, index));
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Nothing after what the phone has: whole if it is exactly the file's size ("bytes */size"). Longer, the
            // file changed on the PC (a smaller one in its place): fetched again from the start
            if (response.Content.Headers.ContentRange?.Length == have)
            {
                progress?.Invoke(have, have);
                return;
            }
            response.Dispose();
            File.Delete(file);
            await DownloadPartAsync(id, index, file, progress, ct);
            return;
        }
        Check(response);
        // The server may answer with the whole file (no ranges): start again
        bool resumed = response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed) have = 0;
        long total = have + (response.Content.Headers.ContentLength ?? 0);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(file, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            have += read;
            progress?.Invoke(have, total);
        }
        if (total > 0 && have < total) throw new IOException("The connection to the PC was lost.");
    }

    Task<T> GetJsonAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct) =>
        WithRetryAsync(async () =>
        {
            using var response = await _http.GetAsync(path, ct);
            Check(response);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync(body, type, ct) ?? throw new InvalidDataException("Empty answer from the PC.");
        }, ct);

    Task<byte[]?> GetBytesAsync(string path, CancellationToken ct) =>
        WithRetryAsync(async () =>
        {
            using var response = await _http.GetAsync(path, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            Check(response);
            return await response.Content.ReadAsByteArrayAsync(ct);
        }, ct);

    /// <summary>
    /// A read that failed on the way (a connection dropped, as the Android emulator's network sometimes does) is
    /// asked once more; an answer of the PC (no key, no such book) is not.
    /// </summary>
    static async Task<T> WithRetryAsync<T>(Func<Task<T>> get, CancellationToken ct)
    {
        try { return await get(); }
        catch (HttpRequestException ex) when (ex.StatusCode == null && !ct.IsCancellationRequested)
        {
            await Task.Delay(300, ct);
            return await get();
        }
    }

    static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("The PC did not accept the access key.");
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}
