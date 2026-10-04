using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Outline;

namespace aBookPlayer;

/// <summary>
/// A line of text on a PDF page: where it is and how big its letters are, in points (the baseline is measured from
/// the bottom of the page). <paramref name="FirstWord"/> is the width of its first word;
/// <paramref name="Starts"/> says it begins a paragraph whatever its place (it had a drop cap).
/// </summary>
sealed record PdfLine(string Text, double Left, double Right, double Baseline, double Size, double FirstWord = 0, bool Starts = false);

/// <summary>A paragraph put back together from a PDF's lines: the page it starts on, and whether it is set as a title.</summary>
sealed record PdfParagraph(string Text, int Page, bool Heading);

/// <summary>
/// The text of a PDF as a book. A PDF holds no paragraphs nor chapters, only letters at places on pages: the lines
/// are found by their baselines, the running headers and page numbers by their being the same on many pages, the
/// paragraphs by indents, gaps and lines ended early, the chapters by the bookmarks or by the titles in the text.
/// </summary>
static partial class PdfText
{
    public static TextBook Read(string path)
    {
        using var document = PdfDocument.Open(path);
        var pages = new List<List<PdfLine>>();
        byte[]? cover = null;
        for (int n = 1; n <= document.NumberOfPages; n++)
        {
            var page = document.GetPage(n);
            pages.Add(Lines(page));
            if (n == 1) cover = Cover(page);
        }
        var paragraphs = Paragraphs(Clean(pages));

        // (The title a word processor leaves is the file's name: "Microsoft Word - book.doc")
        string? Info(string? value) => value?.Trim() is { Length: > 0 } v && !FileNameRegex().IsMatch(v) ? v : null;
        string title = Info(document.Information.Title) ?? Path.GetFileNameWithoutExtension(path);
        var outline = Outline(document, paragraphs.Sum(p => TextBookReader.CountWords(p.Text)));
        var chapters = outline.Count > 0
            ? ByOutline(paragraphs, outline)
            : TextBookReader.Chapters(title, paragraphs.Select(p => (p.Text, p.Heading || TextBookReader.IsHeading(p.Text))));
        return new TextBook { Title = title, Author = Info(document.Information.Author), Cover = cover, Chapters = chapters };
    }

    [GeneratedRegex(@"\.(docx?|rtf|odt|indd|qxd|pdf|tex|pages)$|^untitled|^microsoft word", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameRegex();

    /// <summary>The picture filling the first page, if it is one: the cover.</summary>
    static byte[]? Cover(Page page)
    {
        try
        {
            var image = page.GetImages().MaxBy(i => i.BoundingBox.Area);
            if (image == null || image.BoundingBox.Area < page.Width * page.Height * 0.3) return null;
            // A JPEG is kept in the PDF as it is; anything else is decoded
            var raw = image.RawBytes.ToArray();
            if (raw is [0xFF, 0xD8, ..]) return raw;
            return image.TryGetPng(out var png) ? png : null;
        }
        // The cover is an ornament: a picture that cannot be read is no reason not to read the book
        catch (Exception) { return null; }
    }

    // ───────────────────────────── Lines ─────────────────────────────

    sealed class Piece
    {
        public required string Text;
        public double Left, Right, Baseline, Top, Size;
        /// <summary>The end of a word cut in two by the big letter drawn before it: one with the piece that touches it.</summary>
        public bool Glue;

        public static Piece Of(IReadOnlyList<Letter> letters, int from, int to) => new()
        {
            Text = string.Concat(Enumerable.Range(from, to - from).Select(i => letters[i].Value)),
            Left = letters[from].StartBaseLine.X,
            Right = letters[to - 1].EndBaseLine.X,
            Baseline = letters[from].StartBaseLine.Y,
            Top = Enumerable.Range(from, to - from).Max(i => letters[i].BoundingBox.Top),
            Size = letters[from].PointSize,
        };
    }

    sealed class Row
    {
        public readonly List<Piece> Pieces = [];
        public double Baseline, Left, Size;
        public string Prefix = "";
        public bool Starts;
    }

    static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[values.Count / 2];
    }

    static List<PdfLine> Lines(Page page)
    {
        var words = page.GetWords().Where(w => w.Letters.Count > 0 && !string.IsNullOrWhiteSpace(w.Text)).ToList();
        // (Text up the margin, a watermark across the page, is not the book's)
        var upright = words.Where(w => w.TextOrientation == TextOrientation.Horizontal).ToList();
        if (upright.Count >= words.Count / 2) words = upright;
        if (words.Count == 0) return [];
        double body = Median(words.SelectMany(w => w.Letters).Select(l => l.PointSize).ToList());

        // A drop cap is a big letter set against the first lines of a paragraph: it belongs before the top one,
        // though its baseline is a lower one's (and the word it is drawn next to may have been joined to it)
        var pieces = new List<Piece>();
        var drops = new List<Piece>();
        foreach (var word in words)
        {
            var letters = word.Letters;
            int big = 0;
            while (big < letters.Count && big < 4 && letters[big].PointSize >= body * 1.6) big++;
            if (big is > 0 and < 4 && (big == letters.Count || letters[big].PointSize < body * 1.3))
            {
                drops.Add(Piece.Of(letters, 0, big));
                if (big < letters.Count)
                {
                    var rest = Piece.Of(letters, big, letters.Count);
                    rest.Glue = true;
                    pieces.Add(rest);
                }
                continue;
            }
            var piece = Piece.Of(letters, 0, letters.Count);
            // A note's number, raised and small, is not read
            if (piece.Size < body * 0.72 && piece.Text.All(c => char.IsDigit(c) || c is '*' or '†' or '‡')) continue;
            pieces.Add(piece);
        }
        var rows = Group(pieces, body);
        // (A big letter with no smaller lines beside it is just a short word of a title)
        var loose = drops.Where(d => Beside(rows, d, body) == null).ToList();
        if (loose.Count > 0)
        {
            pieces.AddRange(loose);
            rows = Group(pieces, body);
        }
        foreach (var drop in drops.Except(loose))
        {
            if (Beside(rows, drop, body) is not { } top) continue;
            top.Prefix = drop.Text + (IsWord(drop.Text, top.Pieces[0].Text) ? " " : "");
            top.Starts = true;
            // The lines beside the letter are pushed right by it, not indented
            foreach (var row in rows)
                if (row.Baseline <= top.Baseline && row.Baseline > drop.Baseline - body * 0.3 && row.Left >= drop.Right - body * 0.5) row.Left = drop.Left;
        }
        return rows.Select(row => new PdfLine(Normalize(row.Prefix + Text(row, body)), row.Left, row.Pieces.Max(p => p.Right),
            row.Baseline, row.Size, row.Pieces[0].Right - row.Pieces[0].Left, row.Starts)).Where(l => l.Text.Length > 0).ToList();
    }

    static string Text(Row row, double body)
    {
        var text = new StringBuilder();
        for (int i = 0; i < row.Pieces.Count; i++)
        {
            // ("c" left of "cessantly" by the big letter drawn before it, then "essantly" touching it)
            bool glued = i > 0 && row.Pieces[i - 1].Glue && row.Pieces[i].Left - row.Pieces[i - 1].Right < body * 0.08;
            if (i > 0 && !glued) text.Append(' ');
            text.Append(row.Pieces[i].Text);
        }
        return text.ToString();
    }

    /// <summary>Words that start a chapter with an "A" or an "I" that is their first letter, not a word.</summary>
    static readonly HashSet<string> Starting = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "above", "according", "across", "actually", "after", "afterwards", "again", "against", "ah", "ahead", "alas", "all", "almost", "alone",
        "along", "already", "also", "although", "always", "am", "among", "an", "and", "another", "any", "anybody", "anyone", "anything", "anyway", "apart",
        "apparently", "april", "are", "around", "as", "at", "away",
        "abigail", "adam", "agatha", "agnes", "alan", "albert", "alexander", "alfred", "alice", "amanda", "amy", "andrew", "anna", "anne", "anthony", "arthur",
        "if", "immediately", "in", "indeed", "inside", "instead", "into", "is", "isn't", "it", "it's", "its",
        "ian", "isaac", "isabel", "ivan",
    };

    /// <summary>What follows "I" when it is the word: "I was", "I couldn't".</summary>
    static readonly HashSet<string> AfterI = new(StringComparer.OrdinalIgnoreCase)
    {
        "am", "was", "had", "have", "did", "do", "didn't", "don't", "can", "can't", "cannot", "could", "couldn't", "would", "wouldn't", "should", "shall",
        "will", "won't", "must", "might", "may", "never", "always", "still", "only", "once", "first", "also", "too", "remember", "think", "thought", "know",
        "knew", "saw", "see", "went", "came", "come", "felt", "feel", "found", "heard", "believe", "suppose", "wish", "want", "wanted", "said", "say", "told",
        "met", "woke", "lived", "live", "stood", "sat", "took", "looked", "turned", "walked", "spent", "got", "made", "left", "began", "tried", "hate",
        "love", "like", "need", "used", "wonder", "write", "wrote", "wasn't", "hadn't", "haven't",
    };

    /// <summary>
    /// Whether a drop cap is a word of its own ("I couldn't sleep", "A week later") or the first letter of the word
    /// next to it ("I" and "n my younger years"): their places are the same on the page, only the words tell.
    /// </summary>
    internal static bool IsWord(string drop, string next)
    {
        if (drop.Count(char.IsLetter) != 1) return false;
        char letter = char.ToUpperInvariant(drop.First(char.IsLetter));
        int from = 0, to = next.Length;
        while (from < to && !char.IsLetter(next[from])) from++;
        while (to > from && !char.IsLetter(next[to - 1])) to--;
        next = next[from..to].Replace('’', '\'').ToLowerInvariant();
        if (next.Length == 0 || Starting.Contains(char.ToLowerInvariant(letter) + next)) return false;
        return letter switch
        {
            'I' => AfterI.Contains(next),
            // ("A" is before a consonant: "A man"; before a vowel it would be "An")
            'A' => !"aeiou".Contains(next[0]),
            _ => false,
        };
    }

    /// <summary>The words on the same baseline are a line; the lines go from the top of the page down.</summary>
    static List<Row> Group(List<Piece> pieces, double body)
    {
        var rows = new List<Row>();
        foreach (var piece in pieces.OrderByDescending(p => p.Baseline))
        {
            if (rows.Count == 0 || rows[^1].Baseline - piece.Baseline > Math.Max(body, piece.Size) * 0.3) rows.Add(new Row { Baseline = piece.Baseline });
            rows[^1].Pieces.Add(piece);
        }
        foreach (var row in rows)
        {
            row.Pieces.Sort((a, b) => a.Left.CompareTo(b.Left));
            row.Left = row.Pieces[0].Left;
            row.Size = Median(row.Pieces.Select(p => p.Size).ToList());
        }
        return rows;
    }

    /// <summary>The top line of those set beside a big letter: the one the letter starts.</summary>
    static Row? Beside(List<Row> rows, Piece drop, double body) =>
        rows.Where(r => r.Baseline > drop.Baseline + body * 0.3 && r.Baseline < drop.Top + body * 0.5 && r.Size < drop.Size * 0.75
                        && r.Left >= drop.Right - body * 0.5 && r.Left - drop.Right < body * 3)
            .MaxBy(r => r.Baseline);

    static string Normalize(string text)
    {
        // Ligatures ("ﬁ" as one letter) are their letters, for the reader
        if (text.Any(c => c is >= (char)0xFB00 and <= (char)0xFB06)) text = text.Normalize(NormalizationForm.FormKC);
        return text.Trim();
    }

    // ───────────────────────────── Headers and page numbers ─────────────────────────────

    [GeneratedRegex(@"^\W*(page\s+)?(\d{1,4}|(?-i:[ivxlc]{1,7}))(\s*(of|/)\s*\d{1,4})?\W*$", RegexOptions.IgnoreCase)]
    private static partial Regex PageNumberRegex();

    [GeneratedRegex(@"^\d{1,4}\s*[|·•–—-]?\s+(?=\S)|(?<=\S)\s+[|·•–—-]?\s*\d{1,4}$")]
    private static partial Regex NumberAtEndRegex();

    /// <summary>The size most of the text is set in.</summary>
    static double BodySize(IReadOnlyList<List<PdfLine>> pages) =>
        pages.SelectMany(p => p).GroupBy(l => Math.Round(l.Size * 2) / 2).OrderByDescending(g => g.Sum(l => l.Text.Length)).FirstOrDefault()?.Key ?? 10;

    /// <summary>
    /// The pages without their running headers, footers and page numbers: the lines at the top or at the bottom
    /// that are the same on several pages, but for a number.
    /// </summary>
    internal static List<List<PdfLine>> Clean(IReadOnlyList<List<PdfLine>> pages)
    {
        const int Edge = 2;
        double body = BodySize(pages);
        Dictionary<string, int> tops = [], bottoms = [];
        foreach (var page in pages)
        {
            foreach (var key in page.Take(Edge).SelectMany(Keys).Distinct()) tops[key] = tops.GetValueOrDefault(key) + 1;
            foreach (var key in page.TakeLast(Edge).SelectMany(Keys).Distinct()) bottoms[key] = bottoms.GetValueOrDefault(key) + 1;
        }
        // A title running over a chapter's pages is the same on a few; "Chapter 1", "Chapter 2"… at the top of
        // their pages are the same too but for the number, and are not to be lost: a header with the page's
        // number is on a good part of the book's pages
        int often = Math.Max(3, pages.Count(p => p.Count > 0) / 4);
        bool Running(PdfLine line, Dictionary<string, int> seen)
        {
            // (Nothing bigger than the text: that is a title, "2" over a chapter)
            if (line.Size >= body * 1.15) return false;
            if (PageNumberRegex().IsMatch(line.Text)) return true;
            return Keys(line).Any(key => seen.GetValueOrDefault(key) >= (key[0] == '#' ? often : 3));
        }

        var cleaned = new List<List<PdfLine>>();
        foreach (var page in pages)
        {
            int from = 0, to = page.Count;
            while (from < to && from < Edge && Running(page[from], tops)) from++;
            while (to > from && page.Count - to < Edge && Running(page[to - 1], bottoms)) to--;
            cleaned.Add(page.GetRange(from, to - from));
        }
        return cleaned;
    }

    /// <summary>What a line is told by among the headers: its text, and its text without the page's number.</summary>
    static IEnumerable<string> Keys(PdfLine line)
    {
        string text = line.Text.ToLowerInvariant();
        if (text.Length > 80) yield break;
        var numberless = NumberAtEndRegex().Replace(text, "", 1);
        if (numberless.Length < text.Length) yield return "#" + numberless;
        // (A short line of the story may come back at the top of a few pages: "“No.”" – a header has no full stop)
        else if (!".!?\"”’".Contains(text[^1])) yield return text;
    }

    // ───────────────────────────── Paragraphs ─────────────────────────────

    /// <summary>Words that keep their hyphen when a line ends after it: "self-" / "made" is "self-made".</summary>
    static readonly HashSet<string> Prefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "self", "well", "half", "ex", "all", "ill", "old", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety",
    };

    /// <summary>
    /// The lines of the pages as paragraphs. A line starts one when it is indented, after a gap, in another size, or
    /// when the line before ended though this one's first word had room on it; a paragraph cut by the end of a page
    /// goes on at the top of the next.
    /// </summary>
    internal static List<PdfParagraph> Paragraphs(IReadOnlyList<List<PdfLine>> pages)
    {
        double body = BodySize(pages);
        var paragraphs = new List<PdfParagraph>();
        var text = new StringBuilder();
        int startPage = 0;
        double size = 0;
        List<PdfLine>? previousPage = null;
        int previousIndex = 0;

        void Flush()
        {
            if (text.Length > 0) paragraphs.Add(new PdfParagraph(text.ToString(), startPage, size >= body * 1.2 && text.Length <= 80));
            text.Clear();
        }

        for (int p = 0; p < pages.Count; p++)
        {
            var lines = pages[p];
            if (lines.Count == 0) continue;
            // The distance between the lines of this page, and where its lines start
            var steps = lines.Zip(lines.Skip(1), (a, b) => Math.Round((a.Baseline - b.Baseline) * 2) / 2).Where(s => s > 0).ToList();
            double pitch = steps.Count > 0 ? steps.GroupBy(s => s).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key : body * 1.2;
            var lefts = lines.GroupBy(l => Math.Round(l.Left)).OrderBy(g => g.Key).ToList();
            double margin = (lefts.FirstOrDefault(g => g.Count() >= 2) ?? lefts[0]).Key;

            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                bool start = true;
                if (text.Length > 0 && previousPage != null)
                {
                    var previous = previousPage[previousIndex];
                    // The widest of the lines around the one before: what a full line is there (a quotation set
                    // narrower than the text has its own)
                    double full = Enumerable.Range(previousIndex - 2, 4).Where(k => k >= 0 && k < previousPage.Count).Max(k => previousPage[k].Right);
                    if (i > 0) full = Math.Max(full, line.Right);
                    bool ended = full - previous.Right > line.FirstWord + previous.Size * 0.3;
                    start = line.Starts || ended || Math.Abs(line.Size - previous.Size) > previous.Size * 0.12
                            || (i == 0 ? line.Left > margin + line.Size * 0.5
                                : line.Left > previous.Left + line.Size * 0.5 || previous.Baseline - line.Baseline > pitch * 1.45);
                }
                if (start)
                {
                    Flush();
                    startPage = p + 1;
                    size = line.Size;
                    text.Append(line.Text);
                }
                else Append(text, line.Text);
                previousPage = lines;
                previousIndex = i;
            }
        }
        Flush();
        return paragraphs;
    }

    /// <summary>The next line of a paragraph: after a space, or closing the word a hyphen cut at the end of the line.</summary>
    static void Append(StringBuilder text, string line)
    {
        const char SoftHyphen = (char)0xAD, Hyphen = (char)0x2010;
        if (text[^1] == SoftHyphen) text.Length--;
        else if (text.Length > 1 && text[^1] is '-' or Hyphen && char.IsLetter(text[^2]) && char.IsLower(line[0]))
        {
            int wordStart = text.Length - 1;
            while (wordStart > 0 && char.IsLetter(text[wordStart - 1])) wordStart--;
            if (!Prefixes.Contains(text.ToString(wordStart, text.Length - 1 - wordStart))) text.Length--;
        }
        else text.Append(' ');
        text.Append(line);
    }

    // ───────────────────────────── Chapters ─────────────────────────────

    /// <summary>
    /// The bookmarks that are the book's chapters, with the page each starts on: the first level, or the ones under
    /// it too when that is the book's parts (a chapter is not hours long). None when there are too few to be it.
    /// </summary>
    static List<(int Page, string Title)> Outline(PdfDocument document, int words)
    {
        if (!document.TryGetBookmarks(out var bookmarks)) return [];
        List<(int Page, string Title)> best = [];
        for (int depth = 1; depth <= 3; depth++)
        {
            var entries = new List<(int Page, string Title)>();
            void Walk(IReadOnlyList<BookmarkNode> nodes, int level)
            {
                foreach (var node in nodes)
                {
                    if (node is DocumentBookmarkNode at && Spaces().Replace(at.Title ?? "", " ").Trim() is { Length: > 0 } title) entries.Add((at.PageNumber, title));
                    if (level < depth) Walk(node.Children, level + 1);
                }
            }
            Walk(bookmarks.Roots, 1);
            if (entries.Count < 3) continue;
            best = entries.OrderBy(e => e.Page).ToList();
            if (words / entries.Count <= TextBookReader.MaxChapterWords) break;
        }
        return best;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    static List<TextChapter> ByOutline(List<PdfParagraph> paragraphs, List<(int Page, string Title)> outline)
    {
        static string Key(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        // Where each chapter starts: at the first paragraph of its page, or further down the page at its title
        // (the chapter before may end on the same page)
        var starts = new List<int>();
        int from = 0;
        foreach (var (page, title) in outline)
        {
            int first = paragraphs.FindIndex(from, p => p.Page >= page);
            if (first < 0) first = paragraphs.Count;
            string key = Key(title);
            for (int i = first; i < paragraphs.Count && paragraphs[i].Page == page; i++)
            {
                string text = Key(paragraphs[i].Text);
                if (text.Length < 3 || paragraphs[i].Text.Length > 100 || !(key.StartsWith(text) || text.StartsWith(key))) continue;
                first = i;
                break;
            }
            starts.Add(from = first);
        }

        var chapters = new List<TextChapter>();
        void Add(string title, int start, int end)
        {
            if (end > start) chapters.Add(new TextChapter(title, paragraphs.GetRange(start, end - start).Select(p => p.Text).ToList()));
        }
        // (What comes before the first one: the cover's text, the title page, the copyright)
        Add("Front matter", 0, starts[0]);
        for (int k = 0; k < outline.Count; k++) Add(outline[k].Title, starts[k], k + 1 < starts.Count ? starts[k + 1] : paragraphs.Count);
        return chapters;
    }
}
