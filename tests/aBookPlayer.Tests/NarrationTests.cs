using System.IO.Compression;
using System.Text;

namespace aBookPlayer.Tests;

public class NarrationTests
{
    [Fact]
    public void A_paragraph_is_cut_into_sentences_not_at_abbreviations_or_numbers()
    {
        var sentences = SpeechText.Sentences("Mr. Hale paid $3.50 to J. K. Rowling in 1999. \"Was it worth it?\" he asked. It was, e.g. for the story... Yes!");
        Assert.Equal(
        [
            "Mr. Hale paid $3.50 to J. K. Rowling in 1999.",
            "\"Was it worth it?\" he asked.",
            "It was, e.g. for the story...",
            "Yes!",
        ], sentences);
        // Nothing to say in an ornament
        Assert.Empty(SpeechText.Sentences("* * *"));
    }

    [Fact]
    public void A_very_long_sentence_is_cut_at_its_strongest_break()
    {
        string a = string.Join(' ', Enumerable.Repeat("word", 40)), b = string.Join(' ', Enumerable.Repeat("other", 40));
        var pieces = SpeechText.Sentences($"{a}; {b}, and {a}.", maxLength: 300);
        Assert.True(pieces.Count >= 2);
        Assert.All(pieces, p => Assert.True(p.Length <= 300));
        Assert.EndsWith(";", pieces[0]);
        Assert.Equal($"{a}; {b}, and {a}.", string.Join(' ', pieces));
    }

    [Fact]
    public void Text_is_written_out_as_it_is_read()
    {
        Assert.Equal("Doctor Smith paid 3 dollars and 50 cents, and Mister Hale 1 pound.", SpeechText.Spell("Dr. Smith paid $3.50 — and Mr. Hale £1."));
        Assert.Equal("It cost 3 million dollars in 19 99, 20 percent more than in 19 oh 5.", SpeechText.Spell("It cost $3 million in 1999, 20% more than in 1905."));
        // Not years: a longer number, a price, the 2000s
        Assert.Equal("12,1999 and 2005 and 18 hundred", SpeechText.Spell("12,1999 and 2005 and 1800"));
        Assert.Equal("\"Quotes\" and 'these', a pause... here", SpeechText.Spell("“Quotes” and ‘these’, a pause… here"));
    }

    [Fact]
    public void A_text_file_is_split_at_its_chapter_lines()
    {
        var book = TextBookReader.ReadText("file", "Title: The Lighthouse\nAuthor: Clara Dunmore\n\nChapter 1: Snowfall\n\nSnow had fallen\nduring the night.\n\nSecond paragraph.\n\n" +
                                                  "CHAPTER 2\n\nShe opened the letter.\n");
        Assert.Equal("The Lighthouse", book.Title);
        Assert.Equal("Clara Dunmore", book.Author);
        Assert.Equal(["Chapter 1: Snowfall", "CHAPTER 2"], book.Chapters.Select(c => c.Title));
        // The heading is the chapter's first paragraph (it is read aloud); wrapped lines are one paragraph
        Assert.Equal(["Chapter 1: Snowfall", "Snow had fallen during the night.", "Second paragraph."], book.Chapters[0].Paragraphs);
    }

    /// <summary>A chapter of a few words goes with the next: the ones of these books are longer.</summary>
    static readonly string Filler = string.Join(' ', Enumerable.Repeat("snow", 40)) + ".";

    [Fact]
    public void An_EPUB_gives_its_title_author_cover_and_chapters_in_reading_order()
    {
        var path = Path.Combine(Path.GetTempPath(), "aBookPlayer.Tests", Guid.NewGuid().ToString("N") + ".epub");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Add(string name, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(content);
            }
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='OEBPS/content.opf'/></rootfiles></container>");
            Add("OEBPS/content.opf", """
                <package xmlns="http://www.idpf.org/2007/opf" xmlns:dc="http://purl.org/dc/elements/1.1/">
                  <metadata><dc:title>Winter Lights</dc:title><dc:creator>Clara Dunmore</dc:creator><meta name="cover" content="img"/></metadata>
                  <manifest>
                    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                    <item id="c1" href="text/one.xhtml" media-type="application/xhtml+xml"/>
                    <item id="c1b" href="text/one-b.xhtml" media-type="application/xhtml+xml"/>
                    <item id="c2" href="text/two.xhtml" media-type="application/xhtml+xml"/>
                    <item id="img" href="images/cover.jpg" media-type="image/jpeg"/>
                  </manifest>
                  <spine><itemref idref="nav"/><itemref idref="c1"/><itemref idref="c1b"/><itemref idref="c2"/></spine>
                </package>
                """);
            Add("OEBPS/nav.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml' xmlns:epub='http://www.idpf.org/2007/ops'><body><nav epub:type='toc'><ol>" +
                                   "<li><a href='text/one.xhtml'>Snowfall</a></li><li><a href='text/two.xhtml#start'>The Letter</a></li></ol></nav></body></html>");
            Add("OEBPS/text/one.xhtml", "<html><head><title>x</title><style>p{}</style></head><body><h1>Snowfall</h1><p>Snow had fallen<sup>1</sup> &amp; stayed.</p><p>Second&nbsp;one.</p><p>" + Filler + "</p></body></html>");
            Add("OEBPS/text/one-b.xhtml", "<html><body><p>Still the first chapter.</p></body></html>");
            Add("OEBPS/text/two.xhtml", "<html><body><p>The end of the first.</p><h2 id='start'>The Letter</h2><div>She opened it.<br/>Slowly.</div><p>" + Filler + "</p></body></html>");
            using (var s = zip.CreateEntry("OEBPS/images/cover.jpg").Open()) s.Write([0xFF, 0xD8, 1, 2, 3]);
        }
        var book = TextBookReader.Read(path);
        Assert.Equal(("Winter Lights", "Clara Dunmore"), (book.Title, book.Author));
        Assert.Equal([0xFF, 0xD8, 1, 2, 3], book.Cover);
        Assert.Equal(["Snowfall", "The Letter"], book.Chapters.Select(c => c.Title));
        // A document the contents do not list goes on with the chapter before, and so does what comes before the
        // place a chapter starts at; footnote marks are left out
        Assert.Equal(["Snowfall", "Snow had fallen & stayed.", "Second one.", Filler, "Still the first chapter.", "The end of the first."], book.Chapters[0].Paragraphs);
        Assert.Equal(["The Letter", "She opened it.", "Slowly.", Filler], book.Chapters[1].Paragraphs);
    }

    [Fact]
    public void Chapter_titles_are_told_from_the_text()
    {
        Assert.All(new[] { "Chapter 3", "CHAPTER XII", "Part Two: The Sea", "STAVE I: MARLEY'S GHOST", "Book the First", "Prologue", "Epilogue.", "IV", "# The Sea", "THE LAST OF THE SPIRITS" },
            title => Assert.True(TextBookReader.IsHeading(title), title));
        Assert.All(new[] { "NO!", "\"STOP\"", "Part of him wanted to stay.", "He read the chapter twice.", "#hashtag", "It was late." },
            text => Assert.False(TextBookReader.IsHeading(text), text));
    }

    [Fact]
    public void What_is_not_the_story_is_told_apart_and_a_title_alone_goes_with_what_follows()
    {
        var text = "The Project Gutenberg eBook of Winter Lights\n\nTitle: Winter Lights\n\nAuthor: Clara Dunmore\n\n*** START OF THE PROJECT GUTENBERG EBOOK WINTER LIGHTS ***\n\n" +
                   "CONTENTS\n\nPART ONE\n\nChapter 1\n\nChapter 2\n\nPART ONE\n\nChapter 1\n\n" + string.Join(' ', Enumerable.Repeat("Snow fell.", 30)) +
                   "\n\nChapter 2\n\n" + string.Join(' ', Enumerable.Repeat("She read.", 30)) + "\n\n*** END OF THE PROJECT GUTENBERG EBOOK WINTER LIGHTS ***\n\nThe license.\n";
        var path = Path.Combine(Path.GetTempPath(), "aBookPlayer.Tests", Guid.NewGuid().ToString("N") + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        var book = TextBookReader.Read(path);
        Assert.Equal(("Winter Lights", "Clara Dunmore"), (book.Title, book.Author));
        Assert.Equal(["CONTENTS", "Chapter 1", "Chapter 2"], book.Chapters.Select(c => c.Title));
        Assert.Equal([true, false, false], book.Chapters.Select(c => c.FrontMatter));
        // "PART ONE" is read before the chapter it comes before; nothing of Project Gutenberg's is
        Assert.Equal(["PART ONE", "Chapter 1"], book.Chapters[1].Paragraphs.Take(2));
        Assert.DoesNotContain(book.Chapters.SelectMany(c => c.Paragraphs), p => p.Contains("Gutenberg") || p.Contains("license"));

        Assert.True(new TextChapter("The Full Project Gutenberg™ License", []).FrontMatter);
        Assert.True(new TextChapter("Copyright", []).FrontMatter);
        Assert.False(new TextChapter("The Cover of Night", []).FrontMatter);
    }

    [Fact]
    public void An_EPUB_with_everything_in_one_document_is_cut_where_its_contents_point()
    {
        var paragraphs = TextBookReader.HtmlParagraphs(
            "<html><head><title>x</title></head><body><p>Before.</p>\n<h2 id='c1'>One</h2><p>First\nline\n goes on.</p><div><a name=\"c2\"></a><h2>Two</h2></div><p>Second.</p></body></html>",
            out var heading, ["c1", "c2", "missing"]);
        Assert.Equal("One", heading);
        // (A place that is not in the document is its start; a line break in the source is a space)
        Assert.Equal(["\u0001missing\u0001", "Before.", "\u0001c1\u0001", "One", "First line goes on.", "\u0001c2\u0001", "Two", "Second."], paragraphs);
    }

    static PdfLine Line(string text, double left, double right, double baseline, double size = 10) => new(text, left, right, baseline, size, FirstWord: 20);

    [Fact]
    public void PDF_pages_lose_their_running_headers_and_numbers_and_become_paragraphs()
    {
        // Seven pages: a header with the page's number (the title on the even pages, the author on the odd ones),
        // text from 50 to 300 with paragraphs indented, the page's number at the bottom
        var pages = Enumerable.Range(1, 7).Select(n => new List<PdfLine>
        {
            n % 2 == 0 ? Line($"{n + 10} The Lighthouse", 50, 130, 420, 8) : Line($"Clara Dunmore {n + 10}", 220, 300, 420, 8),
            Line("on to the top of this page and ends.", 50, 200, 392),
            Line($"This is page {n} and its text goes on to the next line with a hyphen-", 62, 300, 380),
            Line("ated word, and then it", 50, 300, 368),
            Line("ends here.", 50, 100, 356),
            Line($"Another paragraph on page {n} that runs", 62, 300, 344),
            Line(n.ToString(), 170, 180, 30, 8),
        }).ToList();
        // A chapter's title at the top of the last page, bigger than the text, is no header
        pages[6].Insert(1, Line("Chapter 2", 140, 210, 404, 14));

        var cleaned = PdfText.Clean(pages);
        Assert.All(cleaned.Take(6), page => Assert.Equal(5, page.Count));
        Assert.DoesNotContain(cleaned.SelectMany(p => p), l => l.Text.Contains("Lighthouse") || l.Text.Contains("Dunmore") || l.Size < 10);

        var paragraphs = PdfText.Paragraphs(cleaned);
        Assert.Equal("on to the top of this page and ends.", paragraphs[0].Text);
        // A word cut by a hyphen at the end of the line is closed; a line ended early ends the paragraph
        Assert.Equal("This is page 1 and its text goes on to the next line with a hyphenated word, and then it ends here.", paragraphs[1].Text);
        // Cut by the end of the page: it goes on at the top of the next
        Assert.Equal(("Another paragraph on page 1 that runs on to the top of this page and ends.", 1), (paragraphs[2].Text, paragraphs[2].Page));
        var title = Assert.Single(paragraphs, p => p.Heading);
        Assert.Equal(("Chapter 2", 7), (title.Text, title.Page));
        Assert.Equal("Another paragraph on page 6 that runs", paragraphs[paragraphs.IndexOf(title) - 1].Text);
    }

    [Fact]
    public void A_drop_cap_is_a_word_or_the_first_letter_of_one()
    {
        // "I" + "n my younger years", "A" + "bout half way", "T" + "here was music"
        Assert.False(PdfText.IsWord("I", "n"));
        Assert.False(PdfText.IsWord("A", "bout"));
        Assert.False(PdfText.IsWord("T", "here"));
        Assert.False(PdfText.IsWord("A", "lice"));
        // "I couldn't sleep", "A week later", "“I was there”"
        Assert.True(PdfText.IsWord("I", "couldn’t"));
        Assert.True(PdfText.IsWord("A", "week"));
        Assert.True(PdfText.IsWord("“I", "was"));
    }

    [Fact]
    public void A_long_chapter_is_cut_into_parts_at_a_paragraph()
    {
        var text = "Chapter 1\n\n" + string.Join("\n\n", Enumerable.Range(0, 300).Select(i => string.Join(' ', Enumerable.Repeat("word", 100)) + "."));
        var path = Path.Combine(Path.GetTempPath(), "aBookPlayer.Tests", Guid.NewGuid().ToString("N") + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        var book = TextBookReader.Read(path);
        Assert.Equal(4, book.Chapters.Count); // 30 000 words, about 8000 each
        Assert.Equal("Chapter 1 (1)", book.Chapters[0].Title);
        Assert.Equal(30_002, book.Words);
        Assert.All(book.Chapters, c => Assert.InRange(c.Words, 6000, 10_000));
    }

    [Fact]
    public void Kokoro_gets_its_own_phonemes_from_espeak_ones()
    {
        // Diphthongs and affricates as one letter, the American "r", no length marks
        Assert.Equal("ðə kˈæt sˈɛd hˈA, ʧˈɜɹʧ ʤˈOk", KokoroVoice.ToKokoro("ðə kˈæt sˈɛd hˈe^ɪ, t^ʃˈɜːt^ʃ d^ʒˈo^ʊk", british: false));
    }

    [Fact]
    public void A_chapter_file_gets_an_ID3_tag_and_a_safe_name()
    {
        var tag = Narrator.Id3(("TIT2", "Snowfall"), ("TPE1", ""), ("TALB", "Winter Lights"));
        Assert.Equal("ID3"u8.ToArray(), tag[..3]);
        Assert.Equal(3, tag[3]);
        int size = (tag[6] << 21) | (tag[7] << 14) | (tag[8] << 7) | tag[9];
        Assert.Equal(tag.Length - 10, size);
        Assert.Equal("TIT2", Encoding.ASCII.GetString(tag, 10, 4));
        // The title in UTF-16, after the frame's header, the encoding byte and the byte order mark
        Assert.Equal("Snowfall", Encoding.Unicode.GetString(tag, 10 + 10 + 3, 16));
        // An empty frame is left out
        Assert.DoesNotContain("TPE1", Encoding.ASCII.GetString(tag));

        Assert.Equal("Chapter 1 What Is It", Narrator.SafeName("Chapter 1: What? Is/It..."));
        Assert.Equal("Untitled", Narrator.SafeName("???"));
    }

    [Fact]
    public void A_header_with_the_chapters_title_and_the_pages_number_goes_and_a_chapters_number_stays()
    {
        // Forty pages, chapters of ten: the book's title on the even pages and the chapter's on the odd ones, each
        // with the page's number; a chapter's first page has "Chapter N" instead, in the size of the text
        var pages = Enumerable.Range(1, 40).Select(n => new List<PdfLine>
        {
            n % 10 == 1 ? Line($"Chapter {n / 10 + 1}", 50, 110, 420)
                : n % 2 == 0 ? Line($"{n} Winter Lights", 50, 130, 420, 8) : Line($"The Letter {n / 10 + 1} {n}", 220, 300, 420, 8),
            Line($"Text of page {n} to the right margin and", 50, 300, 392),
            Line($"more text of page {n} to the margin too", 50, 300, 380),
        }).ToList();
        var cleaned = PdfText.Clean(pages);
        Assert.DoesNotContain(cleaned.SelectMany(p => p), l => l.Size < 10);
        Assert.Equal(["Chapter 1", "Chapter 2", "Chapter 3", "Chapter 4"], cleaned.Select(p => p[0].Text).Where(t => t.StartsWith("Chapter")));
        Assert.All(cleaned, page => Assert.InRange(page.Count, 2, 3));
    }

    [Fact]
    public void A_text_file_in_UTF16_is_read_with_or_without_its_mark()
    {
        var folder = Path.Combine(Path.GetTempPath(), "aBookPlayer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string text = "Chapter 1\n\nSnow had fallen during the night — “at last”.\n";
        File.WriteAllText(Path.Combine(folder, "marked.txt"), text, Encoding.Unicode);
        File.WriteAllBytes(Path.Combine(folder, "bare.txt"), Encoding.Unicode.GetBytes(text));
        File.WriteAllText(Path.Combine(folder, "big.txt"), text, Encoding.BigEndianUnicode);
        File.WriteAllText(Path.Combine(folder, "western.txt"), "Chapter 1\n\nA café, naïve.\n", Encoding.Latin1);
        foreach (var name in new[] { "marked.txt", "bare.txt", "big.txt" })
            Assert.Equal(["Chapter 1", "Snow had fallen during the night — “at last”."], TextBookReader.Read(Path.Combine(folder, name)).Chapters.Single().Paragraphs);
        Assert.Equal(["Chapter 1", "A café, naïve."], TextBookReader.Read(Path.Combine(folder, "western.txt")).Chapters.Single().Paragraphs);
    }

    static string Epub(string? encryption)
    {
        var path = Path.Combine(Path.GetTempPath(), "aBookPlayer.Tests", Guid.NewGuid().ToString("N") + ".epub");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='OEBPS/content.opf'/></rootfiles></container>");
        Add("OEBPS/content.opf", "<package xmlns='http://www.idpf.org/2007/opf' xmlns:dc='http://purl.org/dc/elements/1.1/'><metadata><dc:title>Winter Lights</dc:title></metadata>" +
                                 "<manifest><item id='c1' href='text/one.xhtml' media-type='application/xhtml+xml'/></manifest><spine><itemref idref='c1'/></spine></package>");
        Add("OEBPS/text/one.xhtml", "<html><body><h1>Snowfall</h1><p>" + Filler + "</p></body></html>");
        if (encryption != null)
            Add("META-INF/encryption.xml", "<encryption xmlns='urn:oasis:names:tc:opendocument:xmlns:container' xmlns:enc='http://www.w3.org/2001/04/xmlenc#'>" + encryption + "</encryption>");
        return path;
    }

    [Fact]
    public void A_protected_EPUB_is_told_so_and_one_with_only_its_fonts_obfuscated_is_read()
    {
        static string Encrypted(string algorithm, string file) =>
            $"<enc:EncryptedData><enc:EncryptionMethod Algorithm='{algorithm}'/><enc:CipherData><enc:CipherReference URI='{file}'/></enc:CipherData></enc:EncryptedData>";
        var error = Assert.Throws<InvalidDataException>(() => TextBookReader.Read(Epub(Encrypted("http://www.w3.org/2001/04/xmlenc#aes128-cbc", "OEBPS/text/one.xhtml"))));
        Assert.Contains("protected", error.Message);
        Assert.Equal("Snowfall", TextBookReader.Read(Epub(Encrypted("http://www.idpf.org/2008/embedding", "OEBPS/fonts/a.otf"))).Chapters.Single().Title);
        Assert.Equal("Snowfall", TextBookReader.Read(Epub(null)).Chapters.Single().Title);
    }

    [Fact]
    public void Espeak_is_given_its_folder_in_plain_letters()
    {
        Assert.Equal("C:\\speech\\espeak\0", Encoding.ASCII.GetString(Espeak.DataPath("C:\\speech\\espeak")));
        // A folder with other letters goes by its short name, where the disk gives one
        var folder = Path.Combine(Path.GetTempPath(), "aBookPlayer.Tests", "Niccolò " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Espeak.DataPath(folder);
            Assert.All(path, b => Assert.InRange(b, 0, 127));
            Assert.True(Directory.Exists(Encoding.ASCII.GetString(path).TrimEnd('\0')));
        }
        catch (InvalidOperationException e) { Assert.Contains("plain letters", e.Message); }
    }
}
