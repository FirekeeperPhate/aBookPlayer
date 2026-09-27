using System.Text.RegularExpressions;

namespace aBookPlayer;

/// <summary>
/// Chapters for a book that has none (a plain MP3, a single long file): the narrator reads the headings, so the
/// subtitles (e.g. Whisper's transcription) contain "Chapter 12", "Chapter Twelve", "Prologue", "Part Two"…
/// A heading counts only at the start of a short subtitle line, so a "chapter" mentioned in a sentence is not one.
/// </summary>
static partial class ChapterDetector
{
    const string Numbers =
        @"\d{1,3}|[ivxlc]{1,7}|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|" +
        @"sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundred|first|second|" +
        @"third|fourth|fifth|sixth|seventh|eighth|ninth|tenth|last|final|" +
        @"uno|due|tre|quattro|cinque|sei|sette|otto|nove|dieci|primo|secondo|terzo";

    /// <summary>"Chapter 12", "CHAPTER TWENTY-ONE", "Capitolo 3", "Part Two": a heading word followed by a number.</summary>
    [GeneratedRegex(@"^\W*(?<word>chapter|capitolo|chapitre|kapitel|cap[ií]tulo|part|parte|book|libro)\s+(?<number>(?:" + Numbers + @")(?:[\s-]+(?:" + Numbers + @"))*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberedHeading();

    /// <summary>Headings without a number.</summary>
    [GeneratedRegex(@"^\W*(?<word>prologue|prologo|epilogue|epilogo|interlude|introduction|introduzione|foreword|prefazione|afterword|preface|epilog|prolog)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedHeading();

    /// <summary>A heading is said on its own: its line is short.</summary>
    const int MaxLineLength = 90;

    /// <summary>At least this many headings, or it is not a chapter structure worth showing.</summary>
    const int MinChapters = 3;

    static readonly TimeSpan MinGap = TimeSpan.FromSeconds(60);

    /// <summary>The chapters found in the subtitles (start and title), or an empty list.</summary>
    public static List<Chapter> Detect(IReadOnlyList<SubtitleCue> cues)
    {
        var found = new List<Chapter>();
        foreach (var cue in cues)
        {
            var text = cue.Text.Replace('\n', ' ').Trim();
            if (text.Length == 0 || text.Length > MaxLineLength) continue;
            string title;
            Match match;
            if ((match = NumberedHeading().Match(text)).Success)
            {
                // "Part"/"Book" start ordinary sentences too ("Part one of the problem is…"): as headings they stand
                // alone, the number followed by the end of the line or by punctuation ("Part Two.", "Book 3: …")
                if (IsCommonWord(match.Groups["word"].Value) && !EndsHeading(text, match.Index + match.Length)) continue;
                title = Capitalize(match.Groups["word"].Value) + " " + match.Groups["number"].Value.Trim();
            }
            else if ((match = NamedHeading().Match(text)).Success)
                title = Capitalize(match.Groups["word"].Value);
            else
                continue;

            // "Chapter 3. The Storm." → keep the chapter's own name too
            var rest = text[(match.Index + match.Length)..].Trim(' ', '.', ',', ':', ';', '-', '—');
            if (rest.Length is > 0 and <= 60) title += ": " + rest;

            // The same heading repeated right after (a narrator's pause mid-line) is one chapter
            if (found.Count > 0 && cue.Start - found[^1].Start < MinGap) continue;
            found.Add(new Chapter(title, cue.Start, TimeSpan.Zero));
        }
        return found.Count >= MinChapters ? found : [];
    }

    static bool IsCommonWord(string word) =>
        word.Equals("part", StringComparison.OrdinalIgnoreCase) || word.Equals("parte", StringComparison.OrdinalIgnoreCase)
        || word.Equals("book", StringComparison.OrdinalIgnoreCase) || word.Equals("libro", StringComparison.OrdinalIgnoreCase);

    static bool EndsHeading(string text, int index)
    {
        var after = text.AsSpan(index).TrimStart(' ');
        return after.IsEmpty || after[0] is '.' or ':' or ',' or ';' or '-' or '—' or '!' or '?';
    }

    static string Capitalize(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
}
