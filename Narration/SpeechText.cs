using System.Text.RegularExpressions;

namespace aBookPlayer;

/// <summary>
/// English text made ready to be spoken: cut into sentences (each one is synthesized and shown as a subtitle on its
/// own), and written out the way it is read (abbreviations, money, symbols), since the speech models read letters,
/// not meanings.
/// </summary>
static partial class SpeechText
{
    /// <summary>Words that end in a period without ending the sentence.</summary>
    static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "st", "sr", "jr", "vs", "etc", "e.g", "i.e", "no", "mt", "capt", "col", "gen", "lt", "sgt",
        "rev", "hon", "inc", "ltd", "co", "fig", "vol", "pp", "p", "ch", "ed", "approx", "dept", "est", "gov", "sen", "rep", "messrs", "mme", "mlle",
    };

    /// <summary>
    /// The sentences of a paragraph. One longer than <paramref name="maxLength"/> is cut at a semicolon, a colon, a
    /// dash or a comma, so every piece fits the speech models (and the subtitle area).
    /// </summary>
    public static List<string> Sentences(string paragraph, int maxLength = 300)
    {
        var sentences = new List<string>();
        int start = 0;
        for (int i = 0; i < paragraph.Length; i++)
        {
            if (!".!?…".Contains(paragraph[i])) continue;
            // Past the closing quotes and brackets ("…he said.” She…")
            int end = i + 1;
            while (end < paragraph.Length && ".!?…\"'”’)]".Contains(paragraph[end])) end++;
            if (end < paragraph.Length && !char.IsWhiteSpace(paragraph[end])) continue; // "3.5", "e.g.", "www.site.com"
            int next = end;
            while (next < paragraph.Length && char.IsWhiteSpace(paragraph[next])) next++;
            // The sentence goes on in small letters: "…etc. and so on", "“Was it worth it?” he asked."
            if (next < paragraph.Length && char.IsLower(paragraph[next])) continue;
            if (next < paragraph.Length && paragraph[i] == '.')
            {
                // "Mr. Smith", "J. K. Rowling"
                int wordStart = i;
                while (wordStart > start && (char.IsLetter(paragraph[wordStart - 1]) || paragraph[wordStart - 1] == '.')) wordStart--;
                string word = paragraph[wordStart..i];
                if (Abbreviations.Contains(word) || (word.Length == 1 && char.IsUpper(word[0]))) continue;
            }
            Add(paragraph[start..end]);
            start = next;
            i = next - 1;
        }
        if (start < paragraph.Length) Add(paragraph[start..]);
        return sentences;

        void Add(string sentence)
        {
            sentence = sentence.Trim();
            if (sentence.Length == 0) return;
            // Nothing to say in it ("* * *", "---"): a pause, not a sentence
            if (!sentence.Any(char.IsLetterOrDigit)) return;
            if (sentence.Length <= maxLength) sentences.Add(sentence);
            else sentences.AddRange(Cut(sentence, maxLength));
        }
    }

    /// <summary>A long sentence in pieces: at the strongest break (; : — ,) nearest to its middle, again while too long.</summary>
    static IEnumerable<string> Cut(string sentence, int maxLength)
    {
        if (sentence.Length <= maxLength) return [sentence];
        int middle = sentence.Length / 2, best = -1;
        foreach (var breaks in new[] { ";:", "—–", "," })
        {
            for (int i = 20; i < sentence.Length - 20; i++)
                if (breaks.Contains(sentence[i]) && i + 1 < sentence.Length && (char.IsWhiteSpace(sentence[i + 1]) || breaks != ",")
                    && (best < 0 || Math.Abs(i - middle) < Math.Abs(best - middle))) best = i;
            if (best >= 0) break;
        }
        if (best < 0)
        {
            // No punctuation at all: at the space nearest to the middle
            best = sentence.LastIndexOf(' ', middle);
            if (best <= 0) return [sentence];
            best--;
        }
        return Cut(sentence[..(best + 1)].Trim(), maxLength).Concat(Cut(sentence[(best + 1)..].Trim(), maxLength));
    }

    static readonly (Regex Pattern, string Replacement)[] Spoken =
    [
        (new(@"\bMr\.(?=\s)"), "Mister"), (new(@"\bMrs\.(?=\s)"), "Missus"), (new(@"\bMs\.(?=\s)"), "Miz"), (new(@"\bDr\.(?=\s+[A-Z])"), "Doctor"),
        (new(@"\bProf\.(?=\s)"), "Professor"), (new(@"\bSt\.(?=\s+[A-Z])"), "Saint"), (new(@"\bMt\.(?=\s+[A-Z])"), "Mount"),
        (new(@"\bCapt\.(?=\s)"), "Captain"), (new(@"\bCol\.(?=\s+[A-Z])"), "Colonel"), (new(@"\bGen\.(?=\s+[A-Z])"), "General"),
        (new(@"\bLt\.(?=\s)"), "Lieutenant"), (new(@"\bSgt\.(?=\s)"), "Sergeant"), (new(@"\bRev\.(?=\s+[A-Z])"), "Reverend"),
        (new(@"\bJr\."), "Junior"), (new(@"\bSr\.(?=\s|$)"), "Senior"), (new(@"\bvs\.?(?=\s)"), "versus"), (new(@"\betc\."), "et cetera"),
        (new(@"\be\.g\.,?"), "for example,"), (new(@"\bi\.e\.,?"), "that is,"), (new(@"\bNo\.(?=\s*\d)"), "Number"),
    ];

    [GeneratedRegex(@"([$£€])\s?(\d[\d,]*)(?:\.(\d\d))?(?:\s+(hundred|thousand|million|billion|trillion))?")]
    private static partial Regex MoneyRegex();

    [GeneratedRegex(@"\[\d{1,3}\]|[*_]{1,3}(?=\S)|(?<=\S)[*_]{1,3}")]
    private static partial Regex MarkupRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();

    // Four digits from 1100 to 2099 standing alone: not part of a longer number, a price, a time or a fraction
    [GeneratedRegex(@"(?<![\d$£€,.:/\-])\b(1[1-9]|20)(\d\d)\b(?![,.:/\-]?\d)")]
    private static partial Regex YearRegex();

    /// <summary>
    /// The sentence as it is read aloud: "Dr. Smith paid $3.50" → "Doctor Smith paid 3 dollars and 50 cents". The
    /// phonemizer reads numbers, dates and most symbols by itself.
    /// </summary>
    public static string Spell(string sentence)
    {
        var s = sentence.Replace('“', '"').Replace('”', '"').Replace('‘', '\'').Replace('’', '\'').Replace("…", "...")
            // A dash is a pause, as a comma is
            .Replace(" — ", ", ").Replace("—", ", ").Replace(" – ", ", ").Replace(" -- ", ", ").Replace("--", ", ");
        s = MarkupRegex().Replace(s, "");
        foreach (var (pattern, replacement) in Spoken) s = pattern.Replace(s, replacement);
        s = MoneyRegex().Replace(s, m =>
        {
            var (unit, cent) = m.Groups[1].Value switch { "$" => ("dollar", "cent"), "£" => ("pound", "pence"), _ => ("euro", "cent") };
            string amount = m.Groups[2].Value, scale = m.Groups[4].Value;
            bool one = amount == "1" && scale.Length == 0;
            var words = $"{amount}{(scale.Length > 0 ? " " + scale : "")} {unit}{(one ? "" : "s")}";
            if (m.Groups[3].Success && m.Groups[3].Value != "00")
                words += $" and {m.Groups[3].Value.TrimStart('0')} {(cent == "pence" || m.Groups[3].Value == "01" ? cent : cent + "s")}";
            return words;
        });
        // Years, as they are said: "1999" is "nineteen ninety-nine", "1905" "nineteen oh five", "1800" "eighteen hundred"
        // (2000–2009 are read as numbers: "two thousand five")
        s = YearRegex().Replace(s, m =>
        {
            string century = m.Groups[1].Value, rest = m.Groups[2].Value;
            if (century == "20" && rest[0] == '0') return m.Value;
            return rest == "00" ? $"{century} hundred" : rest[0] == '0' ? $"{century} oh {rest[1]}" : $"{century} {rest}";
        });
        s = s.Replace("&", " and ").Replace("%", " percent").Replace("+", " plus ").Replace("=", " equals ").Replace("@", " at ");
        return SpacesRegex().Replace(s, " ").Trim();
    }
}
