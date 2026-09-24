using System.Text;
using System.Text.RegularExpressions;

namespace aBookPlayer;

sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

sealed partial class SubtitleTrack
{
    readonly TimeSpan _longestCue;

    SubtitleTrack(List<SubtitleCue> cues)
    {
        Cues = cues;
        _longestCue = cues.Count > 0 ? cues.Max(c => c.End - c.Start) : TimeSpan.Zero;
    }

    public IReadOnlyList<SubtitleCue> Cues { get; }

    [GeneratedRegex(@"(\d+):(\d{1,2}):(\d{1,2})[,.](\d{1,3})\s*-->\s*(\d+):(\d{1,2}):(\d{1,2})[,.](\d{1,3})")]
    private static partial Regex TimingRegex();

    [GeneratedRegex(@"<[^>]*>|\{\\[^}]*\}")]
    private static partial Regex TagRegex();

    public static SubtitleTrack Load(string path)
    {
        var lines = ReadText(path).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var cues = new List<SubtitleCue>();

        for (int i = 0; i < lines.Length; i++)
        {
            var m = TimingRegex().Match(lines[i]);
            if (!m.Success) continue;

            var start = ParseTime(m, 1);
            var end = ParseTime(m, 5);
            var text = new List<string>();
            while (++i < lines.Length && lines[i].Trim().Length > 0)
            {
                // block without a trailing blank line: the timing line (or its numeric index) starts the next cue
                if (TimingRegex().IsMatch(lines[i])) { i--; break; }
                if (lines[i].Trim().All(char.IsAsciiDigit) && i + 1 < lines.Length && TimingRegex().IsMatch(lines[i + 1])) { i--; break; }
                text.Add(TagRegex().Replace(lines[i], "").Trim());
            }
            if (end > start) cues.Add(new SubtitleCue(start, end, string.Join("\n", text)));
        }

        cues.Sort((a, b) => a.Start.CompareTo(b.Start));
        return new SubtitleTrack(cues);
    }

    /// <summary>Text active at the given time (overlapping cues are joined), or null.</summary>
    public string? TextAt(TimeSpan time)
    {
        int lo = 0, hi = Cues.Count - 1, idx = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Cues[mid].Start <= time) { idx = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        // Any cue still active must have started within the longest cue duration before 'time'
        List<string>? parts = null;
        var earliestStart = time - _longestCue;
        for (int i = idx; i >= 0 && Cues[i].Start >= earliestStart; i--)
            if (time < Cues[i].End) (parts ??= []).Insert(0, Cues[i].Text);

        return parts == null ? null : string.Join("\n", parts);
    }

    public static void WriteSrt(string path, IReadOnlyList<SubtitleCue> cues)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < cues.Count; i++)
        {
            sb.Append(i + 1).Append("\r\n");
            sb.Append(FormatSrtTime(cues[i].Start)).Append(" --> ").Append(FormatSrtTime(cues[i].End)).Append("\r\n");
            sb.Append(cues[i].Text.Replace("\n", "\r\n")).Append("\r\n\r\n");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>Plain-text transcript with chapter headings and a paragraph break wherever speech pauses.</summary>
    public static void WriteTranscript(string path, IReadOnlyList<SubtitleCue> cues, IReadOnlyList<Chapter> chapters)
    {
        var sb = new StringBuilder();
        int nextChapter = 0;
        SubtitleCue? previous = null;
        foreach (var cue in cues)
        {
            bool heading = false;
            while (nextChapter < chapters.Count && chapters[nextChapter].Start <= cue.Start + TimeSpan.FromSeconds(0.5))
            {
                if (sb.Length > 0) sb.Append("\r\n\r\n");
                sb.Append("## ").Append(chapters[nextChapter++].Title).Append("\r\n\r\n");
                heading = true;
            }
            if (previous != null && !heading)
                sb.Append(cue.Start - previous.End > TimeSpan.FromSeconds(1.5) ? "\r\n\r\n" : " ");
            sb.Append(cue.Text.Replace('\n', ' '));
            previous = cue;
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    static string FormatSrtTime(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";

    static TimeSpan ParseTime(Match m, int g) =>
        new TimeSpan(0,
            int.Parse(m.Groups[g].Value),
            int.Parse(m.Groups[g + 1].Value),
            int.Parse(m.Groups[g + 2].Value),
            int.Parse(m.Groups[g + 3].Value.PadRight(3, '0')));

    static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes is [0xEF, 0xBB, 0xBF, ..]) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes is [0xFF, 0xFE, ..]) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes is [0xFE, 0xFF, ..]) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }
}
