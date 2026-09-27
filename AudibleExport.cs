using System.Globalization;
using System.Text.RegularExpressions;

namespace aBookPlayer;

/// <summary>
/// One audio file of an Audible library exported by Libation (or OpenAudible), as its naming template writes
/// it: "&lt;Title&gt;_ &lt;Series&gt;, Book &lt;n&gt; [&lt;ASIN&gt;] - 07 - Chapter 6.mp3", or just
/// "&lt;Title&gt; [&lt;ASIN&gt;]" for the folder and for the whole book saved as one file.
/// <see cref="Book"/> is what the chapter files and the whole-book file of one export have in common: it
/// groups the files of the same book.
/// </summary>
sealed record AudiblePart(string Book, string Title, string? Series, int? SeriesNumber, string? Asin, int? Part, string? Chapter);

/// <summary>
/// The naming and the layout of an Audible export, so the chapter list, the library and the sync between PCs
/// can use the real title, series and ASIN instead of showing (or hashing) the whole long name.
/// </summary>
static partial class AudibleExport
{
    /// <summary>
    /// "[B08V8766SV]" wherever it appears. An Audible ASIN is "B0" + 8 letters/digits, or an ISBN-10 for older
    /// titles; any 10-letter word would also match a looser pattern, and "[Unabridged]" or "[Dramatized]" would
    /// then give different books the same "ASIN" (and the same sync key).
    /// </summary>
    [GeneratedRegex(@"\s*\[(?:[Bb]0[A-Za-z0-9]{8}|\d{9}[\dXx])\]")]
    private static partial Regex AsinTag();

    /// <summary>A chapter file of a split book: "&lt;base&gt; - 07 - Chapter 6".</summary>
    [GeneratedRegex(@"^(?<base>.+?)\s+-\s+(?<part>\d{1,3})\s+-\s+(?<chapter>.+)$")]
    private static partial Regex ChapterFile();

    /// <summary>Libation's default template: "&lt;Title&gt;_ &lt;Series&gt;, Book &lt;n&gt;".</summary>
    [GeneratedRegex(@"^(?<title>.+?)_\s+(?<series>.+?),\s*[Bb]ook\s+(?<number>\d+(?:\.\d+)?)$")]
    private static partial Regex TitleSeriesBook();

    /// <summary>A template that only carries the series number: "&lt;Title&gt;, Book &lt;n&gt;".</summary>
    [GeneratedRegex(@"^(?<title>.+?),\s*[Bb]ook\s+(?<number>\d+(?:\.\d+)?)$")]
    private static partial Regex TitleBook();

    /// <summary>The series line alone: "&lt;Series&gt;, Book &lt;n&gt;".</summary>
    [GeneratedRegex(@"^(?<series>.+?),\s*[Bb]ook\s+(?<number>\d+(?:\.\d+)?)$")]
    private static partial Regex SeriesBook();

    /// <summary>What the name says about the book; every field is null when the name is not an export's.</summary>
    public static AudiblePart Parse(string name)
    {
        var book = name.Trim();
        string? asin = null;
        if (AsinTag().Match(book) is { Success: true } tag)
        {
            asin = tag.Value.Trim('[', ']', ' ').ToUpperInvariant();
            book = string.Concat(book.AsSpan(0, tag.Index), book.AsSpan(tag.Index + tag.Length)).Trim();
        }

        int? part = null;
        string? chapter = null;
        if (ChapterFile().Match(book) is { Success: true } split)
        {
            part = int.Parse(split.Groups["part"].Value, CultureInfo.InvariantCulture);
            chapter = split.Groups["chapter"].Value.Trim();
            book = split.Groups["base"].Value.Trim();
        }

        string title = book;
        string? series = null;
        int? number = null;
        if (TitleSeriesBook().Match(book) is { Success: true } titled)
        {
            series = titled.Groups["series"].Value.Trim();
            number = SeriesNumber(titled.Groups["number"].Value);
            title = titled.Groups["title"].Value.Trim();
        }
        else if (TitleBook().Match(book) is { Success: true } numbered)
        {
            number = SeriesNumber(numbered.Groups["number"].Value);
            title = numbered.Groups["title"].Value.Trim();
        }

        return new AudiblePart(book, title, series, number, asin, part, chapter);
    }

    static int? SeriesNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? (int)Math.Round(value) : null;

    /// <summary>The book's name as the export wrote it ("Dungeon Crawler Carl [B08V8766SV]") is too long for lists.</summary>
    public static string DisplayName(string name)
    {
        var parsed = Parse(name);
        return parsed.Asin != null || parsed.Series != null ? parsed.Title! : name;
    }

    /// <summary>"Dungeon Crawler Carl, Book 1", for the lists; null when the book is not part of a series.</summary>
    public static string? SeriesLabel(string? series, int? number) =>
        string.IsNullOrWhiteSpace(series) ? null
        : number is { } n ? $"{series}, Book {n}"
        : series;

    /// <summary>
    /// The values AAXClean/Libation write as tags of the files they produce (TXXX in ID3, freeform tags in
    /// MP4); unknown keys are ignored.
    /// </summary>
    public static void ApplyTag(MediaInfo info, string key, string value)
    {
        value = value.Trim();
        if (value.Length == 0) return;
        if (key.Equals("AUDIBLE_ASIN", StringComparison.OrdinalIgnoreCase)) info.Asin ??= value;
        else if (key.Equals("SERIES", StringComparison.OrdinalIgnoreCase)) info.Series ??= value;
        else if (key.Equals("PART", StringComparison.OrdinalIgnoreCase)
                 && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var part)) info.SeriesNumber ??= part;
    }

    /// <summary>AAXClean's "TIT3" subtitle is the series line, e.g. "Dungeon Crawler Carl, Book 1".</summary>
    public static void ApplySeriesLine(MediaInfo info, string line)
    {
        if (SeriesBook().Match(line.Trim()) is not { Success: true } match) return;
        info.Series ??= match.Groups["series"].Value.Trim();
        info.SeriesNumber ??= SeriesNumber(match.Groups["number"].Value);
    }

    /// <summary>
    /// The audio files that make up one book. A Libation export keeps, next to the chapter files, the whole
    /// book as a single file (and often the .mp4 it came from): playing both would play the book twice, so the
    /// chapter files win and the duplicates are left out. Folders without chapter files are untouched.
    /// </summary>
    public static List<string> SelectParts(IEnumerable<string> files)
    {
        var all = files.ToList();
        // Files of one book share the name without its " - 07 - Chapter 6" part
        var books = new Dictionary<string, List<(string Path, AudiblePart Name, string Extension)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in all)
        {
            var name = Parse(Path.GetFileNameWithoutExtension(path));
            if (!books.TryGetValue(name.Book, out var parts)) books[name.Book] = parts = [];
            parts.Add((path, name, Path.GetExtension(path).ToLowerInvariant()));
        }

        var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parts in books.Values)
        {
            if (parts.Count(f => f.Name.Part != null) >= 2)
            {
                foreach (var whole in parts.Where(f => f.Name.Part == null)) dropped.Add(whole.Path);
            }
            else if (parts.Any(f => f.Extension != ".mp4"))
            {
                // The same book as .mp3/.m4b and as .mp4: two copies of the same audio
                foreach (var mp4 in parts.Where(f => f.Extension == ".mp4")) dropped.Add(mp4.Path);
            }
        }
        return all.Where(p => !dropped.Contains(p)).ToList();
    }
}
