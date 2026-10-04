using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace aBookPlayer;

/// <summary>A chapter of a book in text: its title and its paragraphs, in reading order.</summary>
sealed record TextChapter(string Title, List<string> Paragraphs)
{
    public int Words => Paragraphs.Sum(TextBookReader.CountWords);

    /// <summary>
    /// Not part of the story (cover, title page, copyright, contents, a publisher's license): not read aloud unless
    /// asked for.
    /// </summary>
    public bool FrontMatter => TextBookReader.FrontMatterRegex().IsMatch(Title);
}

/// <summary>A book as text (an EPUB, a PDF, a text file), read to be narrated: see <see cref="Narrator"/>.</summary>
sealed class TextBook
{
    public required string Title { get; init; }
    public string? Author { get; init; }
    public byte[]? Cover { get; init; }
    public required List<TextChapter> Chapters { get; init; }
    public int Words => Chapters.Sum(c => c.Words);
}

/// <summary>
/// Reads EPUB, PDF and plain text into chapters and paragraphs. An EPUB says what its chapters are (its table of
/// contents); a PDF may (its bookmarks), and its pages have to be put back into paragraphs (see
/// <see cref="PdfText"/>); a text file is split at its "Chapter …" lines.
/// </summary>
static partial class TextBookReader
{
    public static readonly string[] Extensions = [".epub", ".pdf", ".txt", ".md"];

    /// <summary>A chapter longer than this is cut into parts at a paragraph: about 50 minutes of audio each.</summary>
    internal const int MaxChapterWords = 8000;
    /// <summary>A "chapter" shorter than this is a title on a page of its own ("PART ONE"): it goes with what follows.</summary>
    const int MinChapterWords = 40;

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static TextBook Read(string path)
    {
        var book = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".epub" => ReadEpub(path),
            ".pdf" => PdfText.Read(path),
            _ => ReadText(Path.GetFileNameWithoutExtension(path), ReadAllText(path)),
        };
        var chapters = MergeShort(book.Chapters.Where(c => c.Paragraphs.Count > 0).ToList()).SelectMany(SplitLong).ToList();
        if (chapters.Count == 0) throw new InvalidDataException("No text was found in this file.");
        return new TextBook { Title = book.Title, Author = book.Author, Cover = book.Cover, Chapters = chapters };
    }

    public static int CountWords(string text) => WordRegex().Matches(text).Count;

    [GeneratedRegex(@"\w+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"^\W*(cover|front matter|title page|half title|illustrations|list of (illustrations|figures|tables|plates)|copyright|imprint|colophon|contents|table of contents|uncopyright|also by\b.*|.*project gutenberg.*|.*\blicen[sc]e\b.*)\W*$", RegexOptions.IgnoreCase)]
    internal static partial Regex FrontMatterRegex();

    /// <summary>"PART ONE" on a page of its own, then "Chapter 1": one chapter, under the second title.</summary>
    static List<TextChapter> MergeShort(List<TextChapter> chapters)
    {
        var merged = new List<TextChapter>();
        TextChapter? carried = null;
        foreach (var chapter in chapters)
        {
            // (Front matter is kept apart, however short, so it can be left out; nothing of the book goes into it)
            if (chapter.FrontMatter)
            {
                if (carried != null) merged.Add(carried);
                carried = null;
                merged.Add(chapter);
                continue;
            }
            var whole = carried == null ? chapter : chapter with { Paragraphs = [.. carried.Paragraphs, .. chapter.Paragraphs] };
            carried = whole.Words < MinChapterWords ? whole : null;
            if (carried == null) merged.Add(whole);
        }
        // The last one has nothing after it: it goes with the one before
        if (carried != null)
        {
            if (merged.Count > 0 && !merged[^1].FrontMatter) merged[^1].Paragraphs.AddRange(carried.Paragraphs);
            else merged.Add(carried);
        }
        return merged;
    }

    /// <summary>"Chapter 3" over 8000 words becomes "Chapter 3 (1)", "Chapter 3 (2)"…, each a file of its own.</summary>
    static IEnumerable<TextChapter> SplitLong(TextChapter chapter)
    {
        int words = chapter.Words;
        if (words <= MaxChapterWords * 5 / 4)
        {
            yield return chapter;
            yield break;
        }
        int parts = (int)Math.Ceiling(words / (double)MaxChapterWords), target = words / parts, part = 1, count = 0;
        var current = new List<string>();
        foreach (var paragraph in chapter.Paragraphs)
        {
            current.Add(paragraph);
            count += CountWords(paragraph);
            if (count >= target && part < parts)
            {
                yield return new TextChapter($"{chapter.Title} ({part++})", current);
                current = [];
                count = 0;
            }
        }
        if (current.Count > 0) yield return new TextChapter($"{chapter.Title} ({part})", current);
    }

    // ───────────────────────────── Plain text ─────────────────────────────

    static string ReadAllText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // UTF-16 (what Notepad calls "Unicode"), told by its mark or by every other byte being zero; else UTF-8, or
        // the old Western encoding when it is not valid UTF-8
        if (bytes is [0xFF, 0xFE, ..]) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes is [0xFE, 0xFF, ..]) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        int sample = Math.Min(bytes.Length, 2000) / 2;
        if (sample >= 8 && Enumerable.Range(0, sample).Count(i => bytes[2 * i + 1] == 0) > sample * 9 / 10) return Encoding.Unicode.GetString(bytes);
        if (sample >= 8 && Enumerable.Range(0, sample).Count(i => bytes[2 * i] == 0) > sample * 9 / 10) return Encoding.BigEndianUnicode.GetString(bytes);
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart((char)0xFEFF); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    const string Numbers = @"\d+|[ivxlcdm]+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|first|second|third|last|the\s+\w+";

    // "Chapter 3", "Part Two: The Sea", "STAVE I", "Book the First"; a keyword alone ("Prologue", "Epilogue.")
    [GeneratedRegex(@"^(chapter|part|book|stave|canto|act|scene|section|letter|volume)\s+(" + Numbers + @")\b.{0,60}$|^(prologue|epilogue|introduction|preface|foreword|afterword|appendix|conclusion|contents|dedication)\W*$", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^[IVXLC]{1,7}\.?$")]
    private static partial Regex RomanRegex();

    /// <summary>
    /// A paragraph that is a chapter's title: "Chapter 3", "PART TWO", "Prologue", "# Title" (Markdown), "IV", or a
    /// short line all in capitals.
    /// </summary>
    internal static bool IsHeading(string paragraph)
    {
        if (paragraph.Length is 0 or > 80) return false;
        if (paragraph.StartsWith('#')) return paragraph.TrimStart('#').StartsWith(' ');
        if (HeadingRegex().IsMatch(paragraph) || RomanRegex().IsMatch(paragraph)) return true;
        // All capitals, a few words, not a sentence shouted ("NO!") nor a line of dialogue
        var letters = paragraph.Where(char.IsLetter).ToList();
        return paragraph.Length <= 60 && letters.Count >= 4 && letters.All(char.IsUpper) && !".!?,;\"”’".Contains(paragraph[^1]) && !paragraph.StartsWith('"');
    }

    [GeneratedRegex(@"^(Title|Author):[ \t]*(.+)$", RegexOptions.Multiline)]
    private static partial Regex GutenbergRegex();

    [GeneratedRegex(@"^\*\*\* ?(START|END) OF (THE|THIS) PROJECT GUTENBERG EBOOK.*$", RegexOptions.Multiline)]
    private static partial Regex GutenbergMarkRegex();

    internal static TextBook ReadText(string name, string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        // Project Gutenberg's header says the title and the author; the book is between its START and END lines
        string title = name;
        string? author = null;
        var head = GutenbergRegex().Matches(text.Length > 4000 ? text[..4000] : text);
        foreach (Match m in head)
        {
            if (m.Groups[1].Value == "Title") title = m.Groups[2].Value.Trim();
            else author = m.Groups[2].Value.Trim();
        }
        var marks = GutenbergMarkRegex().Matches(text);
        if (marks.FirstOrDefault(m => m.Groups[1].Value == "START") is { } start)
        {
            int end = marks.FirstOrDefault(m => m.Groups[1].Value == "END" && m.Index > start.Index)?.Index ?? text.Length;
            text = text[(start.Index + start.Length)..end];
        }
        // (A header of one's own, "Title: …" and "Author: …" on the first lines, is not read aloud either)
        else foreach (var m in head.Reverse()) text = text.Remove(m.Index, m.Length);
        // Paragraphs are set apart by empty lines (their lines are then one sentence going on); without any,
        // every line is a paragraph
        var paragraphs = text.Contains("\n\n")
            ? BlankLineRegex().Split(text).Select(p => WhitespaceRegex().Replace(p, " ").Trim())
            : text.Split('\n').Select(l => l.Trim());
        return new TextBook { Title = title, Author = author, Chapters = Chapters(title, paragraphs.Where(p => p.Length > 0).Select(p => (p, IsHeading(p)))) };
    }

    /// <summary>Paragraphs into chapters, at the ones that are titles (each read aloud as its chapter's first words).</summary>
    internal static List<TextChapter> Chapters(string bookTitle, IEnumerable<(string Text, bool Heading)> paragraphs)
    {
        var chapters = new List<TextChapter>();
        var current = new TextChapter(bookTitle, []);
        foreach (var (paragraph, isHeading) in paragraphs)
        {
            if (isHeading)
            {
                if (current.Paragraphs.Count > 0) chapters.Add(current);
                var heading = paragraph.TrimStart('#', ' ');
                current = new TextChapter(heading, [heading]);
            }
            else current.Paragraphs.Add(paragraph);
        }
        if (current.Paragraphs.Count > 0) chapters.Add(current);

        // Titles one after the other with no text between them are the book's contents, not its chapters
        for (int i = 0; i < chapters.Count; i++)
        {
            int run = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // (A title coming a second time is the book starting, after its contents: "PART ONE" on its own page)
            while (i + run < chapters.Count && chapters[i + run].Paragraphs.Count == 1 && seen.Add(chapters[i + run].Title)) run++;
            if (run < 4)
            {
                i += run;
                continue;
            }
            var listed = chapters.GetRange(i, run).SelectMany(c => c.Paragraphs).ToList();
            chapters.RemoveRange(i, run);
            chapters.Insert(i, new TextChapter(new TextChapter(listed[0], []).FrontMatter ? listed[0] : "Contents", listed));
        }
        return chapters;
    }

    [GeneratedRegex(@"\n[ \t]*\n\s*")]
    private static partial Regex BlankLineRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // ───────────────────────────── EPUB ─────────────────────────────

    static TextBook ReadEpub(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        string Text(string entry)
        {
            var e = zip.GetEntry(entry) ?? zip.Entries.FirstOrDefault(x => string.Equals(x.FullName, entry, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"The EPUB lacks \"{entry}\".");
            using var reader = new StreamReader(e.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }
        byte[]? Bytes(string entry)
        {
            var e = zip.GetEntry(entry);
            if (e == null) return null;
            using var s = e.Open();
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        }
        static string FolderOf(string file) => file.Contains('/') ? file[..(file.LastIndexOf('/') + 1)] : "";
        static (string Path, string Fragment) Target(string baseFolder, string href)
        {
            var parts = href.Split('#', 2);
            return (NormalizePath(baseFolder + Uri.UnescapeDataString(parts[0])), parts.Length > 1 ? parts[1] : "");
        }

        var container = XDocument.Parse(Text("META-INF/container.xml"));
        string opfPath = container.Descendants().First(e => e.Name.LocalName == "rootfile").Attribute("full-path")!.Value;
        string folder = FolderOf(opfPath);

        var opf = XDocument.Parse(Text(opfPath));
        string? Meta(string name) => opf.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;
        var manifest = new Dictionary<string, (string Href, string Type, string Properties)>();
        foreach (var e in opf.Descendants().Where(e => e.Name.LocalName == "item"))
            manifest[e.Attribute("id")?.Value ?? ""] = (e.Attribute("href")?.Value ?? "", e.Attribute("media-type")?.Value ?? "", e.Attribute("properties")?.Value ?? "");
        var spine = opf.Descendants().Where(e => e.Name.LocalName == "itemref" && e.Attribute("linear")?.Value != "no")
            .Select(e => e.Attribute("idref")?.Value ?? "").Where(manifest.ContainsKey).ToList();

        // A protected book (DRM): its documents are encrypted, and would be read as nonsense. (Fonts alone are often
        // "obfuscated" in books that are not protected: those two ways of doing it do not count)
        if (zip.GetEntry("META-INF/encryption.xml") != null)
        {
            var spineFiles = spine.Select(id => Target(folder, manifest[id].Href).Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var data in XDocument.Parse(Text("META-INF/encryption.xml")).Descendants().Where(e => e.Name.LocalName == "EncryptedData"))
            {
                string algorithm = data.Descendants().FirstOrDefault(e => e.Name.LocalName == "EncryptionMethod")?.Attribute("Algorithm")?.Value ?? "";
                string uri = data.Descendants().FirstOrDefault(e => e.Name.LocalName == "CipherReference")?.Attribute("URI")?.Value ?? "";
                if (algorithm is "http://www.idpf.org/2008/embedding" or "http://ns.adobe.com/pdf/enc#RC4") continue;
                if (spineFiles.Contains(NormalizePath(Uri.UnescapeDataString(uri))))
                    throw new InvalidDataException("This EPUB is protected (DRM): its text is encrypted and cannot be read. Only books without protection can be made into audiobooks.");
            }
        }

        // The cover: marked in the manifest (EPUB 3), or named by a <meta name="cover"> (EPUB 2)
        var coverId = manifest.FirstOrDefault(m => m.Value.Properties.Split(' ').Contains("cover-image")).Key
                      ?? opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "meta" && e.Attribute("name")?.Value == "cover")?.Attribute("content")?.Value;
        byte[]? cover = coverId != null && manifest.TryGetValue(coverId, out var coverItem) && coverItem.Type.StartsWith("image/")
            ? Bytes(Target(folder, coverItem.Href).Path) : null;

        // The table of contents: where each chapter starts (a document, or a place inside one), its title, and how
        // deep in the contents it is (the parts, their chapters, the chapters' sections)
        var contents = new List<(string Path, string Fragment, string Title, int Level)>();
        try
        {
            var nav = manifest.Values.FirstOrDefault(m => m.Properties.Split(' ').Contains("nav"));
            var ncx = manifest.Values.FirstOrDefault(m => m.Type == "application/x-dtbncx+xml");
            void Add(string tocPath, string href, string label, int level)
            {
                var (target, fragment) = Target(FolderOf(tocPath), href);
                label = Clean(label);
                if (label.Length > 0) contents.Add((target, fragment, label.Length > 90 ? label[..90].TrimEnd() + "…" : label, level));
            }
            if (nav.Href is { Length: > 0 })
            {
                string navPath = Target(folder, nav.Href).Path;
                var doc = XDocument.Parse(Text(navPath));
                var toc = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "nav" && e.Attributes().Any(a => a.Name.LocalName == "type" && a.Value == "toc"))
                          ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "nav");
                foreach (var a in toc?.Descendants().Where(e => e.Name.LocalName == "a") ?? [])
                    if (a.Attribute("href")?.Value is { Length: > 0 } href)
                        Add(navPath, href, a.Value, a.Ancestors().TakeWhile(e => e != toc).Count(e => e.Name.LocalName == "ol"));
            }
            if (contents.Count == 0 && ncx.Href is { Length: > 0 })
            {
                string ncxPath = Target(folder, ncx.Href).Path;
                foreach (var point in XDocument.Parse(Text(ncxPath)).Descendants().Where(e => e.Name.LocalName == "navPoint"))
                {
                    var label = point.Elements().FirstOrDefault(e => e.Name.LocalName == "navLabel")?.Value;
                    var src = point.Elements().FirstOrDefault(e => e.Name.LocalName == "content")?.Attribute("src")?.Value;
                    if (label != null && src != null) Add(ncxPath, src, label, point.Ancestors().Count(e => e.Name.LocalName == "navPoint") + 1);
                }
            }
        }
        catch (Exception e) when (e is System.Xml.XmlException or InvalidDataException) { contents.Clear(); /* every document is a chapter then */ }

        // The documents, in reading order, with a mark at every place the contents point to
        var documents = new List<(string Path, string? Heading, List<string> Paragraphs)>();
        foreach (var id in spine)
        {
            var item = manifest[id];
            if (item.Properties.Split(' ').Contains("nav")) continue;
            string docPath = Target(folder, item.Href).Path;
            var anchors = contents.Where(c => c.Fragment.Length > 0 && string.Equals(c.Path, docPath, StringComparison.OrdinalIgnoreCase)).Select(c => c.Fragment).ToList();
            var paragraphs = HtmlParagraphs(Text(docPath), out var heading, anchors);
            if (paragraphs.Count > 0) documents.Add((docPath, heading, paragraphs));
        }

        // The chapters are the first level of the contents, or the ones under it too when that is the book's parts
        // (a chapter is not hours long); contents that only say "Start" are no contents
        int words = documents.Sum(d => d.Paragraphs.Where(p => p[0] != Mark).Sum(CountWords));
        int depth = 1, deepest = contents.Count > 0 ? contents.Max(c => c.Level) : 0;
        while (depth < deepest && contents.Count(c => c.Level <= depth) is var count && (count < 3 || words / count > MaxChapterWords)) depth++;
        contents.RemoveAll(c => c.Level > depth);
        if (contents.Count < 3 && documents.Count > contents.Count * 3) contents.Clear();

        string bookTitle = Meta("title") ?? Path.GetFileNameWithoutExtension(path);
        var chapters = new List<TextChapter>();
        foreach (var (docPath, heading, paragraphs) in documents)
        {
            // Without a table of contents, every document is a chapter
            if (contents.Count == 0)
            {
                chapters.Add(new TextChapter(heading ?? $"Chapter {chapters.Count + 1}", paragraphs.Where(p => p[0] != Mark).ToList()));
                continue;
            }
            var starts = contents.Where(c => string.Equals(c.Path, docPath, StringComparison.OrdinalIgnoreCase)).ToList();
            // A chapter starting at the document's start…
            if (starts.FirstOrDefault(s => s.Fragment.Length == 0) is { Title: not null } whole) chapters.Add(new TextChapter(whole.Title, []));
            foreach (var paragraph in paragraphs)
            {
                // …and those starting at a place inside it
                if (paragraph[0] == Mark)
                {
                    if (starts.FirstOrDefault(s => s.Fragment == paragraph.Trim(Mark)) is { Title: not null } inner) chapters.Add(new TextChapter(inner.Title, []));
                    continue;
                }
                // Project Gutenberg's license comes after the book, under no title of the contents
                if (GutenbergMarkRegex().Match(paragraph) is { Success: true } mark && mark.Groups[1].Value == "END") chapters.Add(new TextChapter("Project Gutenberg license", []));
                // What comes before the first chapter listed (a cover, a title page) is a chapter too
                if (chapters.Count == 0) chapters.Add(new TextChapter(heading ?? bookTitle, []));
                chapters[^1].Paragraphs.Add(paragraph);
            }
        }
        return new TextBook { Title = bookTitle, Author = Meta("creator"), Cover = cover, Chapters = chapters };
    }

    static string Clean(string text) => WhitespaceRegex().Replace(text, " ").Trim();

    /// <summary>"OEBPS/text/../images/a.jpg" → "OEBPS/images/a.jpg".</summary>
    static string NormalizePath(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (part != "." && part.Length > 0) parts.Add(part);
        }
        return string.Join('/', parts);
    }

    /// <summary>Put around an id in the text where a chapter starts inside a document (see <see cref="HtmlParagraphs"/>).</summary>
    const char Mark = (char)1;

    [GeneratedRegex(@"<head\b.*?</head>|<script\b.*?</script>|<style\b.*?</style>|<!--.*?-->|<sup\b.*?</sup>|<rt\b.*?</rt>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HiddenRegex();

    [GeneratedRegex(@"<h[1-3]\b[^>]*>(.*?)</h[1-3]>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HeadingTagRegex();

    [GeneratedRegex(@"</?(p|div|h[1-6]|li|br|tr|blockquote|section|article|hr|pre|table|ul|ol|dt|dd|figcaption|title|body)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    /// <summary>
    /// The text of an (X)HTML document, paragraph by paragraph, and its first heading. Where an element has one of
    /// the ids in <paramref name="anchors"/> (a chapter starts there), a paragraph holding just the id between two
    /// <see cref="Mark"/>s is put in.
    /// </summary>
    internal static List<string> HtmlParagraphs(string html, out string? heading, IReadOnlyList<string>? anchors = null)
    {
        // Line breaks in the source mean nothing: only the tags do
        html = WhitespaceRegex().Replace(HiddenRegex().Replace(html, ""), " ");
        heading = HeadingTagRegex().Match(html) is { Success: true } m ? Clean(WebUtility.HtmlDecode(TagRegex().Replace(m.Groups[1].Value, " "))) : null;
        if (heading is { Length: 0 }) heading = null;
        foreach (var anchor in anchors ?? [])
        {
            var at = Regex.Match(html, $@"<[^>]*\b(id|name)\s*=\s*[""']{Regex.Escape(anchor)}[""'][^>]*>");
            // (A place that is not there is the document's start)
            html = html.Insert(at.Success ? at.Index : 0, $"\n{Mark}{anchor}{Mark}\n");
        }
        var text = TagRegex().Replace(BlockTagRegex().Replace(html, "\n"), "");
        // (A non-breaking space is a space, for the reader)
        return WebUtility.HtmlDecode(text).Split('\n').Select(p => Clean(p.Replace((char)0xA0, ' '))).Where(p => p.Length > 0).ToList();
    }
}
