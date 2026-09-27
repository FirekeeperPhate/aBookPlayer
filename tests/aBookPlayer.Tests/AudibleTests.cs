using System.Text;
using NAudio.Wave;
using static aBookPlayer.Tests.TestFiles;

namespace aBookPlayer.Tests;

/// <summary>
/// Books exported from Audible (Libation, OpenAudible): the long names of the export, the chapter files that
/// come with the whole book as one file, the Audible tags and the subtitles left in the book's folder.
/// </summary>
public class AudibleTests
{
    const string Prefix = "Dungeon Crawler Carl_ Dungeon Crawler Carl, Book 1 [B08V8766SV]";

    static string NewFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aBookPlayer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void WriteWav(string path, double seconds, int rate = 44100, int channels = 2)
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

    // ───────── Names ─────────

    [Fact]
    public void An_export_name_gives_title_series_asin_and_chapter()
    {
        var part = AudibleExport.Parse($"{Prefix} - 02 - Chapter 1");

        Assert.Equal("Dungeon Crawler Carl", part.Title);
        Assert.Equal("Dungeon Crawler Carl", part.Series);
        Assert.Equal(1, part.SeriesNumber);
        Assert.Equal("B08V8766SV", part.Asin);
        Assert.Equal(2, part.Part);
        Assert.Equal("Chapter 1", part.Chapter);
        // Both the chapter file and the whole book file reduce to the same name, which groups them
        Assert.Equal(part.Book, AudibleExport.Parse(Prefix).Book);
    }

    [Fact]
    public void A_folder_and_a_whole_book_file_are_named_after_the_book()
    {
        var folder = AudibleExport.Parse("Dungeon Crawler Carl [B08V8766SV]");
        Assert.Equal("Dungeon Crawler Carl", folder.Title);
        Assert.Equal("B08V8766SV", folder.Asin);
        Assert.Null(folder.Part);

        Assert.Equal("Dungeon Crawler Carl", AudibleExport.DisplayName(Prefix));
        Assert.Equal("Dungeon Crawler Carl", AudibleExport.DisplayName("Dungeon Crawler Carl [B08V8766SV]"));
        // A name that is not an export's is left alone, "01 - Opening" included
        Assert.Equal("01 - Opening", AudibleExport.DisplayName("01 - Opening"));
        Assert.Equal("The Hobbit", AudibleExport.DisplayName("The Hobbit"));
        Assert.Equal("Dungeon Crawler Carl, Book 3", AudibleExport.SeriesLabel("Dungeon Crawler Carl", 3));
        Assert.Null(AudibleExport.SeriesLabel(null, null));
    }

    [Fact]
    public void Only_real_asins_are_taken_from_the_name()
    {
        // Ten letters in brackets are an edition note, not an ASIN: they must not become a shared sync key
        Assert.Null(AudibleExport.Parse("The Hobbit [Unabridged]").Asin);
        Assert.Null(AudibleExport.Parse("Dune [Dramatized]").Asin);
        Assert.Equal("The Hobbit [Unabridged]", AudibleExport.DisplayName("The Hobbit [Unabridged]"));
        // Audible ASINs and ISBN-10s (older titles) are
        Assert.Equal("B08V8766SV", AudibleExport.Parse("Dungeon Crawler Carl [b08v8766sv]").Asin);
        Assert.Equal("014303943X", AudibleExport.Parse("An Old Title [014303943X]").Asin);
    }

    // ───────── The files of one book ─────────

    [Fact]
    public void Chapter_files_win_over_the_whole_book_saved_next_to_them()
    {
        var dir = NewFolder();
        var chapters = new[] { "01 - Opening Credits", "02 - Chapter 1", "10 - Epilogue" };
        foreach (var c in chapters) File.WriteAllBytes(Path.Combine(dir, $"{Prefix} - {c}.mp3"), []);
        File.WriteAllBytes(Path.Combine(dir, $"{Prefix}.mp3"), []);   // Libation keeps the whole book as well
        File.WriteAllBytes(Path.Combine(dir, $"{Prefix}.mp4"), []);   // and the .mp4 it was made from
        File.WriteAllBytes(Path.Combine(dir, $"{Prefix}.jpg"), []);
        File.WriteAllBytes(Path.Combine(dir, $"{Prefix}.srt"), []);

        var parts = BookSource.PartsOf(dir).Select(Path.GetFileNameWithoutExtension).ToArray();
        Assert.Equal(3, parts.Length);
        Assert.DoesNotContain(Prefix, parts);                             // the whole book is not played again
        Assert.EndsWith($" - {chapters[^1]}", parts[^1]);                 // natural order kept
    }

    [Fact]
    public void The_same_book_in_two_formats_is_played_once()
    {
        var dir = NewFolder();
        File.WriteAllBytes(Path.Combine(dir, "The Hobbit.mp3"), []);
        File.WriteAllBytes(Path.Combine(dir, "The Hobbit.mp4"), []);

        Assert.Equal(["The Hobbit.mp3"], BookSource.PartsOf(dir).Select(p => Path.GetFileName(p)!).ToArray());
    }

    [Fact]
    public void A_folder_without_an_export_layout_keeps_all_its_files()
    {
        var dir = NewFolder();
        File.WriteAllBytes(Path.Combine(dir, "10 Ten.wav"), []);
        File.WriteAllBytes(Path.Combine(dir, "2 Two.wav"), []);
        File.WriteAllBytes(Path.Combine(dir, "1 One.wav"), []);

        Assert.Equal(["1 One.wav", "2 Two.wav", "10 Ten.wav"], BookSource.PartsOf(dir).Select(p => Path.GetFileName(p)!).ToArray());
    }

    [Fact]
    public void A_whole_book_in_one_file_is_kept()
    {
        var dir = NewFolder();
        File.WriteAllBytes(Path.Combine(dir, $"{Prefix}.m4b"), []);

        Assert.Single(BookSource.PartsOf(dir));
    }

    // ───────── The book's own tags ─────────

    [Fact]
    public void The_Audible_tags_of_a_chapter_file_are_read()
    {
        var tag = Id3Tag(
            Id3Frame("TIT2", Utf16Text("2 - Dungeon Crawler Carl: Chapter 1")),
            Id3Frame("TPE1", [3, .. Encoding.UTF8.GetBytes("Matt Dinniman")]),
            Id3Frame("TALB", [3, .. Encoding.UTF8.GetBytes("Dungeon Crawler Carl")]),
            Id3UserText("AUDIBLE_ASIN", "B08V8766SV"),
            Id3UserText("SERIES", "Dungeon Crawler Carl"),
            Id3UserText("PART", "1"),
            Id3UserText("TOOL", "AAXClean"));

        var info = ReadFile([.. tag, .. new byte[100]], ".mp3");

        Assert.Equal("B08V8766SV", info.Asin);
        Assert.Equal("Dungeon Crawler Carl", info.Series);
        Assert.Equal(1, info.SeriesNumber);
        Assert.Equal("Matt Dinniman", info.Artist);
        Assert.Equal("Dungeon Crawler Carl", info.Album);
    }

    [Fact]
    public void The_narrator_of_a_chapter_file_is_read_and_shown_under_the_title()
    {
        var tag = Id3Tag(
            Id3Frame("TIT2", [3, .. Encoding.UTF8.GetBytes("Dungeon Crawler Carl")]),
            Id3Frame("TPE1", [3, .. Encoding.UTF8.GetBytes("Matt Dinniman")]),
            Id3Frame("TCOM", [3, .. Encoding.UTF8.GetBytes("Jeff Hays")]),
            Id3Frame("TALB", [3, .. Encoding.UTF8.GetBytes("Dungeon Crawler Carl")]));

        var info = ReadFile([.. tag, .. new byte[100]], ".mp3");

        Assert.Equal("Jeff Hays", info.Narrator);
        // The album repeats the title here, so the line is just author and narrator
        Assert.Equal("Matt Dinniman · read by Jeff Hays", MainForm.Byline(info, "Dungeon Crawler Carl"));
    }

    [Fact]
    public void The_byline_leaves_out_what_the_file_does_not_carry()
    {
        Assert.Equal("", MainForm.Byline(new MediaInfo(), "Some Book"));
        Assert.Equal("read by Jeff Hays",
            MainForm.Byline(new MediaInfo { Narrator = "Jeff Hays" }, "Some Book"));
        Assert.Equal("An Author · Another Album",
            MainForm.Byline(new MediaInfo { Artist = "An Author", Album = "Another Album" }, "Some Book"));
    }

    [Fact]
    public void The_narrator_of_an_mp4_export_is_read()
    {
        var moov = Box("moov", Box("udta", Mp4Tags(
            Mp4Item("©nam", "Dungeon Crawler Carl"),
            Mp4Item("©wrt", "Jeff Hays"))));
        var info = ReadFile([.. Box("ftyp", Encoding.ASCII.GetBytes("M4B "), Zeros(4)), .. moov], ".m4b");

        Assert.Equal("Jeff Hays", info.Narrator);
    }

    [Fact]
    public void The_subtitle_tag_is_used_as_the_series_line_when_the_series_tag_is_missing()
    {
        var tag = Id3Tag(Id3Frame("TIT3", [3, .. Encoding.UTF8.GetBytes("Dungeon Crawler Carl, Book 5")]));
        var info = ReadFile([.. tag, .. new byte[100]], ".mp3");

        Assert.Equal("Dungeon Crawler Carl", info.Series);
        Assert.Equal(5, info.SeriesNumber);
    }

    [Fact]
    public void The_Audible_tags_of_an_mp4_export_are_read()
    {
        var moov = Box("moov", Box("udta", Mp4Tags(
            Mp4Item("©nam", "Dungeon Crawler Carl"),
            Mp4Item("©ART", "Matt Dinniman"),
            Mp4Freeform("AUDIBLE_ASIN", "B08V8766SV"),
            Mp4Freeform("SERIES", "Dungeon Crawler Carl"),
            Mp4Freeform("PART", "3"))));
        var info = ReadFile([.. Box("ftyp", Encoding.ASCII.GetBytes("M4B "), Zeros(4)), .. moov], ".m4b");

        Assert.Equal("Dungeon Crawler Carl", info.Title);
        Assert.Equal("Matt Dinniman", info.Artist);
        Assert.Equal("B08V8766SV", info.Asin);
        Assert.Equal("Dungeon Crawler Carl", info.Series);
        Assert.Equal(3, info.SeriesNumber);
    }

    [Fact]
    public void Audible_tags_in_utf16_user_text_frames_are_read()
    {
        static byte[] Utf16UserText(string description, string value) => Id3Frame("TXXX",
            [1, .. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(description), 0, 0,
             .. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(value)]);

        var info = ReadFile([.. Id3Tag(Utf16UserText("AUDIBLE_ASIN", "B08V8766SV"), Utf16UserText("PART", "4")), .. new byte[100]], ".mp3");

        Assert.Equal("B08V8766SV", info.Asin);
        Assert.Equal(4, info.SeriesNumber);
    }

    [Fact]
    public void Damaged_user_text_frames_do_not_throw()
    {
        var info = ReadFile([.. Id3Tag(Id3Frame("TXXX", [0]), Id3Frame("TXXX", [])), .. new byte[20]], ".mp3");
        Assert.Null(info.Asin);
    }

    static MediaInfo ReadFile(byte[] content, string extension)
    {
        var path = TempPath(extension);
        File.WriteAllBytes(path, content);
        return MediaMetadata.Read(path);
    }

    // ───────── A book of the export, read as a whole ─────────

    [Fact]
    public void An_export_folder_plays_as_one_book_with_clean_chapter_names()
    {
        var book = Path.Combine(NewFolder(), "Dungeon Crawler Carl [B08V8766SV]");
        WriteWav(Path.Combine(book, $"{Prefix} - 01 - Opening Credits.wav"), 1.0);
        WriteWav(Path.Combine(book, $"{Prefix} - 02 - Chapter 1.wav"), 2.0);
        File.WriteAllBytes(Path.Combine(book, $"{Prefix}.wav"), []); // the whole book, and its .mp4
        File.WriteAllBytes(Path.Combine(book, $"{Prefix}.mp4"), []);

        Assert.Equal(2, BookSource.PartsOf(book).Length);

        var (info, reader) = BookSource.OpenWithInfo(book);
        using (reader)
        {
            Assert.Equal("Dungeon Crawler Carl", info.Title);
            Assert.Equal("B08V8766SV", info.Asin);
            Assert.Equal("Dungeon Crawler Carl", info.Series);
            Assert.Equal(1, info.SeriesNumber);
            Assert.Equal([("Opening Credits", 0.0), ("Chapter 1", 1.0)],
                info.Chapters.Select(c => (c.Title, Math.Round(c.Start.TotalSeconds, 2))).ToArray());
        }

        Assert.Equal("Dungeon Crawler Carl", BookSource.DisplayName(book));
        Assert.Equal("Dungeon Crawler Carl", BookSource.NameFromPath(book));
        Assert.Equal("asin:B08V8766SV", BookSync.KeyFor(book, info.Asin));
    }

    [Fact]
    public void A_book_the_export_did_not_name_keeps_its_own_chapter_names()
    {
        var dir = NewFolder();
        WriteWav(Path.Combine(dir, "01 - Opening.wav"), 1.0);
        WriteWav(Path.Combine(dir, "02 - The road.wav"), 1.0);

        var (info, reader) = BookSource.OpenWithInfo(dir);
        using (reader)
            Assert.Equal(["01 - Opening", "02 - The road"], info.Chapters.Select(c => c.Title).ToArray());
    }

    // ───────── Subtitles ─────────

    [Fact]
    public void The_subtitles_of_an_export_are_found_under_their_own_name()
    {
        var book = Path.Combine(NewFolder(), "Dungeon Crawler Carl [B08V8766SV]");
        WriteWav(Path.Combine(book, $"{Prefix} - 01 - Opening Credits.wav"), 1.0);
        WriteWav(Path.Combine(book, $"{Prefix} - 02 - Chapter 1.wav"), 1.0);
        File.WriteAllText(Path.Combine(book, $"{Prefix}.srt"), new string('x', 500));       // the whole book
        File.WriteAllText(Path.Combine(book, $"{Prefix} - 02 - Chapter 1.srt"), "small");   // one chapter only

        Assert.Equal(Path.Combine(book, $"{Prefix}.srt"), BookSource.FindSubtitle(book));

        // A book aBookPlayer wrote subtitles for (or the user made) uses the simple name and wins
        var own = Path.Combine(book, "Dungeon Crawler Carl.srt");
        File.WriteAllText(own, "…");
        Assert.Equal(own, BookSource.FindSubtitle(book));
    }

    [Fact]
    public void Per_chapter_subtitles_are_not_loaded_for_the_whole_book()
    {
        var book = Path.Combine(NewFolder(), "Dungeon Crawler Carl [B08V8766SV]");
        WriteWav(Path.Combine(book, $"{Prefix} - 01 - Opening Credits.wav"), 1.0);
        File.WriteAllText(Path.Combine(book, $"{Prefix} - 02 - Chapter 1.srt"), "small");

        Assert.Null(BookSource.FindSubtitle(book));
    }

    // ───────── Library ─────────

    [Fact]
    public void Books_of_a_series_keep_together_when_the_library_is_ordered()
    {
        var settings = new AppSettings();
        var first = settings.RememberBook(@"C:\Books\1.m4b", 0, null, 0);
        first.Title = "Dungeon Crawler Carl";
        first.Author = "Matt Dinniman";
        first.Series = "Dungeon Crawler Carl";
        first.SeriesNumber = 1;
        var second = settings.RememberBook(@"C:\Books\2.m4b", 0, null, 0);
        second.Title = "Carl's Doomsday Scenario";
        second.Author = "Matt Dinniman";
        second.Series = "Dungeon Crawler Carl";
        second.SeriesNumber = 2;
        var other = settings.RememberBook(@"C:\Books\3.m4b", 0, null, 0);
        other.Title = "Another Book";
        other.Author = "Jane Roe";
        var loose = settings.RememberBook(@"C:\Books\4.m4b", 0, null, 0);
        loose.Title = "Zeta Without Author";

        var entries = settings.Books.Select(b => new LibraryEntry { Path = b.Key, State = b.Value }).ToList();

        Assert.Equal(["Dungeon Crawler Carl", "Carl's Doomsday Scenario", "Another Book", "Zeta Without Author"],
            LibraryOrder.Sort(entries, LibrarySort.Series).Select(e => e.Title));
        // By author: alphabetical, and the books of one author in series order
        Assert.Equal(["Another Book", "Dungeon Crawler Carl", "Carl's Doomsday Scenario", "Zeta Without Author"],
            LibraryOrder.Sort(entries, LibrarySort.Author).Select(e => e.Title));
        Assert.Equal("Dungeon Crawler Carl, Book 2", entries.Single(e => e.Title.StartsWith("Carl's")).SeriesLabel);
        Assert.Null(entries.Single(e => e.Title.StartsWith("Another")).SeriesLabel);
    }

    [Fact]
    public void A_series_of_ten_or_more_books_stays_in_reading_order()
    {
        var settings = new AppSettings();
        foreach (var n in new[] { 2, 10, 1 })
        {
            var b = settings.RememberBook($@"C:\Books\{n}.m4b", 0, null, 0);
            b.Title = $"Volume {n}";
            b.Author = "Matt Dinniman";
            b.Series = "Dungeon Crawler Carl";
            b.SeriesNumber = n;
        }
        var entries = settings.Books.Select(b => new LibraryEntry { Path = b.Key, State = b.Value }).ToList();

        Assert.Equal(["Volume 1", "Volume 2", "Volume 10"], LibraryOrder.Sort(entries, LibrarySort.Series).Select(e => e.Title));
        Assert.Equal(["Volume 1", "Volume 2", "Volume 10"], LibraryOrder.Sort(entries, LibrarySort.Author).Select(e => e.Title));
    }

    [Fact]
    public void The_sync_key_uses_the_asin_when_the_book_has_one()
    {
        var file = TempPath(".m4b");
        File.WriteAllBytes(file, new byte[1234]);

        Assert.Equal("asin:B08V8766SV", BookSync.KeyFor(file, "b08v8766sv"));
        Assert.EndsWith("|1234", BookSync.KeyFor(file));   // no ASIN: name and size, as before
    }

    [Fact]
    public void Positions_saved_by_an_earlier_version_are_still_found()
    {
        var shared = NewFolder();
        var book = Path.Combine(NewFolder(), "Dungeon Crawler Carl [B08V8766SV]");
        WriteWav(Path.Combine(book, $"{Prefix} - 01 - Opening Credits.wav"), 1.0);
        WriteWav(Path.Combine(book, $"{Prefix} - 02 - Chapter 1.wav"), 1.0);
        WriteWav(Path.Combine(book, $"{Prefix}.wav"), 2.0);                     // the whole book: skipped now,
        File.WriteAllBytes(Path.Combine(book, $"{Prefix}.mp4"), new byte[777]);  // but 1.6 counted both

        // What 1.6 saved under: the raw folder name (ASIN included) and the size of every audio file
        long all = Directory.GetFiles(book).Sum(f => new FileInfo(f).Length);
        var legacy = $"dungeon crawler carl [b08v8766sv]|{all}";
        Assert.Equal(legacy, BookSync.LegacyKeyFor(book));
        Assert.NotEqual(legacy, BookSync.KeyFor(book));   // the current rules give another key

        var dir = Path.Combine(shared, "aBookPlayer sync");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "LAPTOP.json"),
            $$"""{ "{{legacy}}": { "Seconds": 4200, "Updated": "2026-01-01T10:00:00Z" } }""");

        var found = BookSync.Find(shared, BookSync.KeyFor(book, "B08V8766SV")!, BookSync.LegacyKeyFor(book));
        Assert.Equal(4200, found!.Seconds);
        Assert.Equal("LAPTOP", found.Machine);
    }
}
