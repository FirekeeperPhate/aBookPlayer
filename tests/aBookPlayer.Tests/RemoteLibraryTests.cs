using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace aBookPlayer.Tests;

/// <summary>The PC's library served to phones: the protocol, the access key and the byte ranges a player seeks with.</summary>
public class RemoteLibraryTests
{
    const string Key = "K7PX-M2QA-9TRD";

    sealed class FakeLibrary : IRemoteLibrary
    {
        public string File1 = "";
        public string Srt = "";
        public string Machine => "TESTPC";

        public IReadOnlyList<RemoteBookSummary> Books() =>
            [new RemoteBookSummary("b1", "The Book", "Ann Author", "Saga", 2, 120, 30, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), false, "asin:B1")];

        public RemoteBook? Book(string id) => id != "b1" ? null : new RemoteBook("b1", "The Book", "Ann Author", "Saga", 2, "B1", "asin:B1",
            [new RemotePart("01.mp3", 60), new RemotePart("02.mp3", 60)], [new RemoteChapter("One", 0), new RemoteChapter("Two", 60)],
            HasCover: true, HasSubtitles: true, 30, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), false);

        public RemotePartData? Served;
        public RemotePartData? Part(string id, int index) => id == "b1" && index == 0 ? Served ?? new RemotePartData(File1, 0, new FileInfo(File1).Length, []) : null;
        public byte[]? Cover(string id) => id == "b1" ? [0x89, 0x50, 0x4E, 0x47, 1, 2, 3] : null;
        public string? SubtitleFile(string id) => id == "b1" ? Srt : null;
        public string? Apk;
        public string? AppPackage => Apk;
        public RemotePosition? Posted;
        public bool SetPosition(string id, RemotePosition position)
        {
            if (id != "b1") return false;
            Posted = position;
            return true;
        }
    }

    static (LibraryServer Server, FakeLibrary Library, byte[] Audio) Start()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aBookPlayer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var audio = Enumerable.Range(0, 100_000).Select(i => (byte)(i * 7)).ToArray();
        var library = new FakeLibrary { File1 = Path.Combine(dir, "01.mp3"), Srt = Path.Combine(dir, "book.srt") };
        File.WriteAllBytes(library.File1, audio);
        File.WriteAllText(library.Srt, "1\n00:00:01,000 --> 00:00:02,000\nHello\n");
        var server = new LibraryServer(library, Key, "1.11.0");
        server.Start(0, discoveryPort: 0);
        return (server, library, audio);
    }

    [Fact]
    public async Task A_phone_reads_the_library_and_a_book_with_the_key()
    {
        var (server, _, _) = Start();
        using var _s = server;
        // Typed on a phone: lower case, no dashes
        using var client = new RemoteLibraryClient($"127.0.0.1:{server.Port}", "k7pxm2qa9trd");

        var hello = await client.HelloAsync();
        Assert.Equal(("aBookPlayer", LibraryServer.Protocol, "TESTPC", "1.11.0"), (hello.App, hello.Protocol, hello.Machine, hello.Version));

        var books = await client.LibraryAsync();
        Assert.Equal("The Book", Assert.Single(books).Title);

        var book = await client.BookAsync("b1");
        Assert.Equal([60.0, 60.0], book.Parts.Select(p => p.Seconds));
        Assert.Equal("Two", book.Chapters[1].Title);
        Assert.Equal(DateTimeKind.Utc, book.PositionUpdated.Kind);

        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 1, 2, 3], await client.CoverAsync("b1"));
        Assert.Contains("Hello", Encoding.UTF8.GetString((await client.SubtitlesAsync("b1"))!));
        Assert.Null(await client.CoverAsync("nope"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.BookAsync("nope"));
    }

    [Fact]
    public async Task Without_the_right_key_nothing_is_served()
    {
        var (server, _, _) = Start();
        using var _s = server;
        using var wrong = new RemoteLibraryClient($"127.0.0.1:{server.Port}", "AAAA-AAAA-AAAA");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => wrong.HelloAsync());

        using var http = new HttpClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"http://127.0.0.1:{server.Port}/api/library")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"http://127.0.0.1:{server.Port}/api/books/b1/parts/0")).StatusCode);
        // The key in the address, to try it from a browser
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://127.0.0.1:{server.Port}/api/hello?key={Key}")).StatusCode);
    }

    [Fact]
    public async Task Audio_is_served_whole_or_in_the_ranges_a_player_seeks_to()
    {
        var (server, library, audio) = Start();
        using var _s = server;
        using var client = new RemoteLibraryClient($"127.0.0.1:{server.Port}", Key);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(client.Authorization);
        var uri = client.PartUri("b1", 0);

        using (var whole = await http.GetAsync(uri))
        {
            Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
            Assert.Equal("audio/mpeg", whole.Content.Headers.ContentType!.MediaType);
            Assert.Equal(audio, await whole.Content.ReadAsByteArrayAsync());
        }

        async Task<(HttpStatusCode Status, byte[] Body, string? Range)> Get(string range)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Range", range);
            using var response = await http.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(), response.Content.Headers.ContentRange?.ToString());
        }

        var middle = await Get("bytes=1000-1999");
        Assert.Equal(HttpStatusCode.PartialContent, middle.Status);
        Assert.Equal(audio[1000..2000], middle.Body);
        Assert.Equal("bytes 1000-1999/100000", middle.Range);

        var rest = await Get("bytes=99000-");
        Assert.Equal(audio[99000..], rest.Body);

        var tail = await Get("bytes=-500");
        Assert.Equal(audio[^500..], tail.Body);

        var beyond = await Get("bytes=99000-200000"); // clipped to the end
        Assert.Equal(audio[99000..], beyond.Body);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, (await Get("bytes=100000-")).Status);

        // Parts that are not there
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(client.PartUri("b1", 5))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(client.PartUri("b1", -1))).StatusCode);
    }

    [Fact]
    public async Task A_phone_copies_a_part_and_goes_on_where_an_attempt_stopped()
    {
        var (server, _, audio) = Start();
        using var _s = server;
        using var client = new RemoteLibraryClient($"127.0.0.1:{server.Port}", Key);
        var file = Path.Combine(Path.GetTempPath(), "aBookPlayer.Tests", Guid.NewGuid().ToString("N") + ".part");

        long lastHave = 0, lastTotal = 0;
        await client.DownloadPartAsync("b1", 0, file, (have, total) => (lastHave, lastTotal) = (have, total));
        Assert.Equal(audio, File.ReadAllBytes(file));
        Assert.Equal((audio.Length, audio.Length), (lastHave, lastTotal));

        // A connection lost after 30 000 bytes: only the rest is asked for
        File.WriteAllBytes(file, audio[..30_000]);
        long first = -1;
        await client.DownloadPartAsync("b1", 0, file, (have, _) => { if (first < 0) first = have; });
        Assert.Equal(audio, File.ReadAllBytes(file));
        Assert.True(first > 30_000);

        // Already whole: nothing to do
        await client.DownloadPartAsync("b1", 0, file);
        Assert.Equal(audio, File.ReadAllBytes(file));

        // Longer than the file on the PC (a smaller one put in its place): fetched again, not taken as whole
        File.WriteAllBytes(file, [.. audio, .. new byte[500]]);
        await client.DownloadPartAsync("b1", 0, file);
        Assert.Equal(audio, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task A_phone_tells_the_PC_where_it_is_in_a_book()
    {
        var (server, library, _) = Start();
        using var _s = server;
        using var client = new RemoteLibraryClient($"127.0.0.1:{server.Port}", Key);
        var position = new RemotePosition(1234.5, new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc), false, "Pixel 8");
        await client.SendPositionAsync("b1", position);
        Assert.Equal(position, library.Posted);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendPositionAsync("nope", position));

        // Not without the key, not something else, and nothing else is posted
        using var wrong = new RemoteLibraryClient($"127.0.0.1:{server.Port}", "AAAA-AAAA-AAAA");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => wrong.SendPositionAsync("b1", position with { Seconds = 1 }));
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(client.Authorization);
        var url = $"http://127.0.0.1:{server.Port}/api/books/b1/position";
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync(url, new StringContent("not json"))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.PostAsync($"http://127.0.0.1:{server.Port}/api/library", new StringContent("{}"))).StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(() => http.PostAsync(url, new ByteArrayContent(new byte[100_000])));
        Assert.Equal(1234.5, library.Posted!.Seconds);
    }

    [Fact]
    public async Task Phones_find_the_PCs_sharing_their_library_on_the_network()
    {
        var (server, _, _) = Start();
        using var _s = server;
        // An answering port of its own, not the real one (a running app may have it)
        int port;
        using (var probe = new UdpClient(0)) port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        server.StartAnswering(port);

        var found = await RemoteLibraryClient.DiscoverAsync(TimeSpan.FromSeconds(1), [IPAddress.Loopback], port);
        var (address, pc) = Assert.Single(found);
        Assert.EndsWith($":{server.Port}", address);
        Assert.Equal("TESTPC", pc.Machine);
    }

    [Fact]
    public async Task The_QR_code_opens_a_page_that_opens_the_app_connected()
    {
        var (server, _, _) = Start();
        using var _s = server;
        using var http = new HttpClient();
        var page = await http.GetStringAsync($"http://127.0.0.1:{server.Port}/connect?key={Key}");
        Assert.Contains("TESTPC", page);
        Assert.Contains("intent://connect?address=127.0.0.1%3A", page);
        Assert.Contains("package=io.github.marcotrombetta.abookplayer", page);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"http://127.0.0.1:{server.Port}/connect?key=WRONG")).StatusCode);

        var link = LibraryServer.AppLink("192.168.1.20:52780", "k7px-m2qa-9trd");
        Assert.Equal(("192.168.1.20:52780", "K7PXM2QA9TRD"), RemoteLibraryClient.ParseAppLink(link));
        Assert.Null(RemoteLibraryClient.ParseAppLink("https://example.com/connect?address=x&key=y"));
        Assert.Null(RemoteLibraryClient.ParseAppLink("abookplayer://connect?address=x"));
    }

    [Fact]
    public async Task A_phone_without_the_app_downloads_it_from_the_PC()
    {
        var (server, library, _) = Start();
        using var _s = server;
        using var http = new HttpClient();
        string url = $"http://127.0.0.1:{server.Port}";

        // Not downloaded by the PC yet: the page sends to GitHub
        var page = await http.GetStringAsync($"{url}/connect?key={Key}");
        Assert.Contains("S.browser_fallback_url=https%3A%2F%2Fgithub.com%2FMarcoTrombetta%2FaBookPlayer%2Freleases%2Flatest", page);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"{url}/app/aBookPlayer-1.11.1.apk?key={Key}")).StatusCode);

        library.Apk = Path.Combine(Path.GetDirectoryName(library.File1)!, "aBookPlayer-1.11.1.apk");
        var apk = Enumerable.Range(0, 3 * 1024 * 1024).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(library.Apk, apk);
        page = await http.GetStringAsync($"{url}/connect?key={Key}");
        Assert.Contains($"S.browser_fallback_url=http%3A%2F%2F127.0.0.1%3A{server.Port}%2Fapp%2FaBookPlayer-1.11.1.apk%3Fkey%3DK7PXM2QA9TRD", page);
        Assert.Contains($"href=\"http://127.0.0.1:{server.Port}/app/aBookPlayer-1.11.1.apk?key=K7PXM2QA9TRD\"", page);
        Assert.Contains("Download the app (3 MB)", page);

        using var response = await http.GetAsync($"{url}/app/aBookPlayer-1.11.1.apk?key={Key}");
        Assert.Equal("application/vnd.android.package-archive", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(apk, await response.Content.ReadAsByteArrayAsync());
        // Resumed after a lost connection
        using var rest = new HttpRequestMessage(HttpMethod.Get, $"{url}/app/aBookPlayer-1.11.1.apk?key={Key}");
        rest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1_000_000, null);
        using var partial = await http.SendAsync(rest);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(apk[1_000_000..], await partial.Content.ReadAsByteArrayAsync());
        // The key is needed, and only the app's own file is served
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"{url}/app/aBookPlayer-1.11.1.apk")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"{url}/app/01.mp3?key={Key}")).StatusCode);

        Assert.Equal(new Version(1, 11, 1), PhoneApp.VersionOf(@"C:\x\android\aBookPlayer-1.11.1.apk"));
        Assert.Null(PhoneApp.VersionOf("other-1.0.apk"));
    }

    [Fact]
    public void A_long_MP3_is_cut_between_its_frames_into_parts_with_exact_lengths()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "TestData", "vbr-20s.mp3");
        var parts = Mp3Segments.Split(file, seconds: 5)!;

        Assert.Equal(4, parts.Count);
        // All the audio (the file's ID3 tag and its own Xing frame left out), part after part with nothing lost
        Assert.True(parts[0].Offset > 100);
        for (int i = 1; i < parts.Count; i++) Assert.Equal(parts[i - 1].Offset + parts[i - 1].Length, parts[i].Offset);
        Assert.Equal(new FileInfo(file).Length, parts[^1].Offset + parts[^1].Length);
        // 20 s plus the encoder's priming samples, which the PC (NAudio) counts as well: both clocks agree
        Assert.InRange(parts.Sum(p => p.Seconds), 20.0, 20.1);
        // Each part opens with a Xing frame telling its exact frame count
        foreach (var part in parts)
        {
            int tag = part.Prefix.AsSpan().IndexOf("Xing"u8);
            Assert.True(tag > 0);
            int frames = (part.Prefix[tag + 8] << 24) | (part.Prefix[tag + 9] << 16) | (part.Prefix[tag + 10] << 8) | part.Prefix[tag + 11];
            Assert.Equal(part.Frames, frames);
            Assert.Equal(part.Frames * 576 / 22050.0, part.Seconds, 3);   // MPEG-2: 576 samples per frame
        }
        // Not an MP3: served whole
        Assert.Null(Mp3Segments.Split(Path.Combine(AppContext.BaseDirectory, "aBookPlayer.Tests.dll")));
    }

    [Fact]
    public async Task A_part_is_served_as_its_prefix_and_its_stretch_of_the_file()
    {
        var (server, library, audio) = Start();
        using var _s = server;
        byte[] prefix = [1, 2, 3, 4, 5];
        library.Served = new RemotePartData(library.File1, 1000, 2000, prefix);
        using var client = new RemoteLibraryClient($"127.0.0.1:{server.Port}", Key);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(client.Authorization);
        byte[] whole = [.. prefix, .. audio[1000..3000]];

        Assert.Equal(whole, await http.GetByteArrayAsync(client.PartUri("b1", 0)));
        using var request = new HttpRequestMessage(HttpMethod.Get, client.PartUri("b1", 0));
        request.Headers.TryAddWithoutValidation("Range", "bytes=3-10");   // across the prefix's end
        using var response = await http.SendAsync(request);
        Assert.Equal(whole[3..11], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes 3-10/2005", response.Content.Headers.ContentRange!.ToString());
    }

    [Fact]
    public async Task A_broken_request_does_not_stop_the_server()
    {
        var (server, _, _) = Start();
        using var _s = server;
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
            await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes("garbage\r\n\r\n"));
        }
        using var client = new RemoteLibraryClient($"127.0.0.1:{server.Port}", Key);
        Assert.Equal("TESTPC", (await client.HelloAsync()).Machine);
    }

    [Fact]
    public async Task The_PC_serves_its_library_folders_with_lengths_subtitles_and_its_position()
    {
        var root = Path.Combine(Path.GetTempPath(), "aBookPlayer.Tests", Guid.NewGuid().ToString("N"));
        var book = Path.Combine(root, "Dune");
        WriteWav(Path.Combine(book, "01 - Arrakis.wav"), 1.0);
        WriteWav(Path.Combine(book, "02 - Muad'Dib.wav"), 2.0);
        File.WriteAllText(Path.Combine(book, "Dune.srt"), "1\n00:00:01,000 --> 00:00:02,000\nSpice\n");
        WriteWav(Path.Combine(root, "Emma.wav"), 1.5);
        var state = new BookState { Title = "Dune", PositionSeconds = 2.5, PositionUpdated = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc) };
        // The details read are cached in memory only, not in the app's real cache file (CoreSetup points it there
        // when the app's code is first used: run it first)
        RuntimeHelpers.RunModuleConstructor(typeof(SharedLibrary).Module.ModuleHandle);
        LibraryDetailsCache.FilePath = null;
        var shared = new SharedLibrary(() => new SharedLibrary.Snapshot([root], new() { [book] = state }));

        using var server = new LibraryServer(shared, Key, "1.11.0");
        server.Start(0, discoveryPort: 0);
        using var client = new RemoteLibraryClient($"127.0.0.1:{server.Port}", Key);

        var list = await client.LibraryAsync();
        Assert.Equal(["Dune", "Emma"], list.Select(b => b.Title));
        Assert.Equal(2.5, list[0].PositionSeconds);

        var dune = await client.BookAsync(list[0].Id);
        Assert.Equal(["01 - Arrakis.wav", "02 - Muad'Dib.wav"], dune.Parts.Select(p => p.Name));
        Assert.Equal(1.0, dune.Parts[0].Seconds, 2);
        Assert.Equal(2.0, dune.Parts[1].Seconds, 2);
        Assert.Equal(["01 - Arrakis", "02 - Muad'Dib"], dune.Chapters.Select(c => c.Title)); // as the PC names them
        Assert.Equal(1.0, dune.Chapters[1].Start, 2);
        Assert.True(dune.HasSubtitles);
        Assert.Equal((2.5, state.PositionUpdated), (dune.PositionSeconds, dune.PositionUpdated));
        Assert.Equal(BookSync.KeyFor(book), dune.SyncKey);   // the phone syncs it with this PC's key

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(client.Authorization);
        Assert.Equal(File.ReadAllBytes(Path.Combine(book, "02 - Muad'Dib.wav")), await http.GetByteArrayAsync(client.PartUri(dune.Id, 1)));
        Assert.Contains("Spice", Encoding.UTF8.GetString((await client.SubtitlesAsync(dune.Id))!));

        // A single-file book is one part
        var emma = await client.BookAsync(list[1].Id);
        Assert.Equal(1.5, Assert.Single(emma.Parts).Seconds, 2);
        Assert.False(emma.HasSubtitles);
        // The PC moved on: the next answer says so
        state.PositionSeconds = 3.0;
        Assert.Equal(3.0, (await client.BookAsync(dune.Id)).PositionSeconds);
    }

    static void WriteWav(string path, double seconds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(8000, 16, 1));
        w.Write(new byte[(int)(seconds * 8000) * 2], 0, (int)(seconds * 8000) * 2);
    }

    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20:52780")]
    [InlineData(" 192.168.1.20:8080 ", "192.168.1.20:8080")]
    [InlineData("http://mypc.local:5000/", "mypc.local:5000")]
    [InlineData("MYPC", "mypc:52780")]
    public void Addresses_are_completed_with_the_default_port(string typed, string expected) =>
        Assert.Equal(expected, RemoteLibraryClient.NormalizeAddress(typed));

    [Theory]
    [InlineData("")]
    [InlineData("192.168.1.20:52780/api")]
    [InlineData("not an address")]
    public void Other_text_is_not_an_address(string typed) =>
        Assert.Throws<FormatException>(() => RemoteLibraryClient.NormalizeAddress(typed));

    [Fact]
    public void New_keys_are_easy_to_type_and_match_however_they_are_written()
    {
        var key = AccessKey.New();
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", key);
        Assert.DoesNotContain('O', key);
        Assert.DoesNotContain('1', key);
        Assert.True(AccessKey.Matches(key.ToLowerInvariant().Replace("-", " "), key));
        Assert.False(AccessKey.Matches(null, key));
        Assert.False(AccessKey.Matches(key[..^1], key));
        Assert.NotEqual(key, AccessKey.New());
    }

    [Fact]
    public void Book_ids_do_not_depend_on_case_and_do_not_show_the_path()
    {
        var id = RemoteIds.For(@"C:\Books\Dune");
        Assert.Equal(id, RemoteIds.For(@"c:\books\dune"));
        Assert.Matches("^[0-9a-f]{16}$", id);
        Assert.NotEqual(id, RemoteIds.For(@"C:\Books\Emma"));
    }
}
