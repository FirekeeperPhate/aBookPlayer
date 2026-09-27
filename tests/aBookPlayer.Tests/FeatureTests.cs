using System.Drawing.Imaging;
using System.Text;
using NAudio.Wave;
using static aBookPlayer.Tests.TestFiles;

namespace aBookPlayer.Tests;

/// <summary>Books made of several files, covers, bookmarks, smart rewind, sync, voice boost and the library scan.</summary>
public class FeatureTests
{
    static string NewFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aBookPlayer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void WriteWav(string path, double seconds, int rate, int channels)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new WaveFileWriter(path, new WaveFormat(rate, 16, channels));
        var samples = (int)(seconds * rate) * channels;
        var data = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            short v = (short)(3000 * Math.Sin(i * 0.05));
            data[2 * i] = (byte)v;
            data[2 * i + 1] = (byte)(v >> 8);
        }
        w.Write(data, 0, data.Length);
    }

    static byte[] PngBytes(Color color)
    {
        using var bmp = new Bitmap(4, 4);
        using (var g = Graphics.FromImage(bmp)) g.Clear(color);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    // ───────── Books in several files ─────────

    [Fact]
    public void Folder_parts_are_in_natural_order_including_cd_subfolders()
    {
        var dir = NewFolder();
        foreach (var name in new[] { "10 Ten.wav", "2 Two.wav", "1 One.wav", "cover.jpg", "notes.txt" })
            File.WriteAllBytes(Path.Combine(dir, name), []);
        Directory.CreateDirectory(Path.Combine(dir, "CD 2"));
        File.WriteAllBytes(Path.Combine(dir, "CD 2", "01.wav"), []);

        var parts = BookSource.PartsOf(dir).Select(p => Path.GetRelativePath(dir, p)).ToArray();
        Assert.Equal(["1 One.wav", "2 Two.wav", "10 Ten.wav", Path.Combine("CD 2", "01.wav")], parts);
        Assert.Equal(Path.Combine(dir, Path.GetFileName(dir) + ".srt"), BookSource.SubtitlePath(dir));
    }

    [Fact]
    public void Several_files_play_as_one_seekable_stream_even_in_different_formats()
    {
        var dir = NewFolder();
        WriteWav(Path.Combine(dir, "01.wav"), 2.0, 44100, 2);
        WriteWav(Path.Combine(dir, "02.wav"), 1.5, 22050, 1);   // other rate and channels: converted
        WriteWav(Path.Combine(dir, "03.wav"), 1.0, 48000, 2);

        using var book = new ConcatenatedWaveStream(BookSource.PartsOf(dir));
        Assert.Equal(44100, book.WaveFormat.SampleRate);
        Assert.Equal(2, book.WaveFormat.Channels);
        Assert.Equal(4.5, book.TotalTime.TotalSeconds, 2);
        Assert.Equal(2.0, book.PartStart(1).TotalSeconds, 3);
        Assert.Equal(3.5, book.PartStart(2).TotalSeconds, 3);

        // Reading the whole book gives (about) its full length, across the part boundaries
        var buffer = new byte[book.WaveFormat.AverageBytesPerSecond];
        long total = 0;
        int n;
        while ((n = book.Read(buffer, 0, buffer.Length)) > 0) total += n;
        Assert.InRange(total / (double)book.WaveFormat.AverageBytesPerSecond, 4.4, 4.6);

        // Seeking into the second part
        book.CurrentTime = TimeSpan.FromSeconds(2.75);
        Assert.Equal(2.75, book.CurrentTime.TotalSeconds, 2);
        Assert.True(book.Read(buffer, 0, buffer.Length) > 0);
        Assert.InRange(book.CurrentTime.TotalSeconds, 3.7, 3.8);
    }

    [Fact]
    public void A_folder_book_has_one_chapter_per_file_with_titles_from_the_tags()
    {
        var dir = NewFolder();
        WriteWav(Path.Combine(dir, "01 - Opening.wav"), 1.0, 44100, 2);
        WriteWav(Path.Combine(dir, "02 - The road.wav"), 2.0, 44100, 2);

        var (info, reader) = BookSource.OpenWithInfo(dir);
        using (reader)
        {
            Assert.Equal(Path.GetFileName(dir), info.Title); // no tags: the folder name
            Assert.Equal([("01 - Opening", 0.0), ("02 - The road", 1.0)],
                info.Chapters.Select(c => (c.Title, Math.Round(c.Start.TotalSeconds, 2))).ToArray());
        }
    }

    // ───────── Covers ─────────

    [Fact]
    public void Covers_are_read_from_id3_flac_mp4_and_the_folder()
    {
        var png = PngBytes(Color.Red);

        byte[] apic = [0, .. Encoding.ASCII.GetBytes("image/png"), 0, 3, .. Encoding.ASCII.GetBytes("front"), 0, .. png];
        var mp3 = TempPath(".mp3");
        File.WriteAllBytes(mp3, [.. Id3Tag(Id3Frame("TIT2", Utf16Text("x")), Id3Frame("APIC", apic)), .. new byte[100]]);
        Assert.Equal(png, MediaMetadata.Read(mp3).Cover);

        var picture = new List<byte>();
        void BE(int v) => picture.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        BE(3); BE(9); picture.AddRange(Encoding.ASCII.GetBytes("image/png")); BE(0); BE(4); BE(4); BE(32); BE(0); BE(png.Length); picture.AddRange(png);
        var flac = TempPath(".flac");
        File.WriteAllBytes(flac, [.. Encoding.ASCII.GetBytes("fLaC"), 0x00, 0, 0, 34, .. new byte[34],
            0x86, (byte)(picture.Count >> 16), (byte)(picture.Count >> 8), (byte)picture.Count, .. picture]);
        Assert.Equal(png, MediaMetadata.Read(flac).Cover);

        var m4b = TempPath(".m4b");
        File.WriteAllBytes(m4b, [.. Box("ftyp", Encoding.ASCII.GetBytes("M4B "), Zeros(4)),
            .. Box("moov", Box("udta", Box("meta", Zeros(4),
                Box("hdlr", Zeros(4), Zeros(4), Encoding.ASCII.GetBytes("mdir"), Zeros(12), Zeros(1)),
                Box("ilst", Box("covr", Box("data", U32(14), Zeros(4), png))))))]);
        Assert.Equal(png, MediaMetadata.Read(m4b).Cover);

        var dir = NewFolder();
        File.WriteAllBytes(Path.Combine(dir, "Folder.JPG"), png);
        Assert.Equal(png, CoverArt.FromFolder(dir));
        Assert.NotNull(CoverArt.ToImage(png));
        Assert.Null(CoverArt.ToImage([1, 2, 3]));
    }

    // ───────── Bookmarks, smart rewind ─────────

    [Fact]
    public void Saving_a_book_keeps_its_bookmarks_and_library_data()
    {
        var settings = new AppSettings();
        var book = settings.RememberBook(@"C:\Books\A.m4b", 10, null, 0);
        book.Bookmarks.Add(new Bookmark { Seconds = 5, Note = "here" });
        book.DurationSeconds = 3600;
        book.Title = "A";

        settings.RememberBook(@"C:\Books\A.m4b", 20, null, 0);
        var again = settings.GetBook(@"C:\Books\A.m4b")!;
        Assert.Single(again.Bookmarks);
        Assert.Equal(3600, again.DurationSeconds);
        Assert.Equal(20, again.PositionSeconds);
    }

    [Fact]
    public void Position_timestamp_only_moves_when_the_position_does()
    {
        var settings = new AppSettings();
        var book = settings.RememberBook(@"C:\Books\A.m4b", 10, null, 0);
        var first = book.PositionUpdated = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        settings.RememberBook(@"C:\Books\A.m4b", 10.2, null, 0);   // same place: e.g. a periodic save while paused
        Assert.Equal(first, book.PositionUpdated);
        settings.RememberBook(@"C:\Books\A.m4b", 30, null, 0);
        Assert.True(book.PositionUpdated > first);
    }

    [Theory]
    [InlineData(20, 0)]
    [InlineData(90, 5)]
    [InlineData(15 * 60, 15)]
    [InlineData(3 * 3600, 30)]
    public void Smart_rewind_grows_with_the_length_of_the_pause(int pauseSeconds, int expected) =>
        Assert.Equal(expected, MainForm.SmartRewindSeconds(TimeSpan.FromSeconds(pauseSeconds)));

    // ───────── Sync between PCs ─────────

    [Fact]
    public async Task Sync_finds_the_newest_position_saved_by_another_pc()
    {
        var shared = NewFolder();
        var file = TempPath(".m4b");
        File.WriteAllBytes(file, new byte[1234]);
        var key = BookSync.KeyFor(file)!;
        Assert.EndsWith("|1234", key);

        var dir = Path.Combine(shared, "aBookPlayer sync");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "LAPTOP.json"),
            $$"""{ "{{key}}": { "Seconds": 100, "Updated": "2026-01-01T10:00:00Z" } }""");
        File.WriteAllText(Path.Combine(dir, "DESKTOP.json"),
            $$"""{ "{{key}}": { "Seconds": 250, "Updated": "2026-01-02T10:00:00Z" } }""");

        var found = BookSync.Find(shared, key)!;
        Assert.Equal(250, found.Seconds);
        Assert.Equal("DESKTOP", found.Machine);

        // This PC's own file is written, and never read back as "another PC"
        var book = new BookState { SyncKey = key, PositionSeconds = 999, PositionUpdated = DateTime.UtcNow };
        await BookSync.Publish(shared, [book]);
        Assert.True(File.Exists(Path.Combine(dir, Environment.MachineName + ".json")));
        Assert.Equal(250, BookSync.Find(shared, key)!.Seconds);
    }

    // ───────── Voice boost ─────────

    [Fact]
    public void Voice_boost_lifts_quiet_speech_and_never_clips()
    {
        float[] Run(float amplitude, bool enabled)
        {
            var samples = new float[44100 * 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = amplitude * (float)Math.Sin(i * 0.03);
            var boost = new VoiceBoost(new AudioTests.ArraySampleProvider(samples, 2)) { Enabled = enabled };
            var output = new float[samples.Length];
            boost.Read(output, 0, output.Length);
            return output;
        }

        float Peak(float[] s) => s.Skip(s.Length / 2).Max(Math.Abs); // after the envelope has settled

        Assert.Equal(0.05f, Peak(Run(0.05f, enabled: false)), 3);   // off: untouched
        Assert.True(Peak(Run(0.05f, enabled: true)) > 0.1f);        // quiet narrator: louder
        Assert.True(Run(0.95f, enabled: true).All(v => Math.Abs(v) <= 1f)); // loud: limited, no clipping
    }

    // ───────── Library ─────────

    [Fact]
    public void Library_scan_tells_book_folders_from_single_file_books()
    {
        var root = NewFolder();
        File.WriteAllBytes(Path.Combine(root, "Standalone.m4b"), []);
        Directory.CreateDirectory(Path.Combine(root, "Author", "Book in chapters"));
        File.WriteAllBytes(Path.Combine(root, "Author", "Book in chapters", "01.mp3"), []);
        File.WriteAllBytes(Path.Combine(root, "Author", "Book in chapters", "02.mp3"), []);
        Directory.CreateDirectory(Path.Combine(root, "Author", "Two discs", "CD 1"));
        Directory.CreateDirectory(Path.Combine(root, "Author", "Two discs", "CD 2"));
        File.WriteAllBytes(Path.Combine(root, "Author", "Two discs", "CD 1", "01.mp3"), []);
        File.WriteAllBytes(Path.Combine(root, "Author", "Two discs", "CD 2", "01.mp3"), []);
        Directory.CreateDirectory(Path.Combine(root, "Series"));
        File.WriteAllBytes(Path.Combine(root, "Series", "Volume 1.m4b"), []);
        File.WriteAllBytes(Path.Combine(root, "Series", "Volume 2.m4b"), []);

        var books = LibraryScanner.Scan([root], CancellationToken.None).Select(p => Path.GetRelativePath(root, p)).Order().ToArray();
        Assert.Equal(
        [
            Path.Combine("Author", "Book in chapters"),
            Path.Combine("Author", "Two discs"),
            Path.Combine("Series", "Volume 1.m4b"),
            Path.Combine("Series", "Volume 2.m4b"),
            "Standalone.m4b",
        ], books);
    }
}
