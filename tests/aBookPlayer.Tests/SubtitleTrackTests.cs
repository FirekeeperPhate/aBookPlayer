using System.Text;

namespace aBookPlayer.Tests;

public class SubtitleTrackTests
{
    static SubtitleTrack Load(string srt, Encoding encoding)
    {
        var path = TestFiles.TempPath(".srt");
        File.WriteAllText(path, srt, encoding);
        return SubtitleTrack.Load(path);
    }

    [Fact]
    public void Parses_windows1252_file_with_tags_and_missing_blank_line()
    {
        var track = Load("""
            1
            00:00:01,000 --> 00:00:04,000
            <i>Primo</i> sottotitolo

            2
            00:00:05,500 --> 00:00:08,000
            Seconda riga
            con due righe, perché sì
            3
            00:00:13.000 --> 00:00:15,5
            Terza
            """, CodePagesEncodingProvider.Instance.GetEncoding(1252)!);

        Assert.Equal(3, track.Cues.Count);
        Assert.Equal("Primo sottotitolo", track.Cues[0].Text);
        Assert.Equal("Seconda riga\ncon due righe, perché sì", track.Cues[1].Text); // the next cue's index is not text
        Assert.Equal(TimeSpan.FromMilliseconds(15_500), track.Cues[2].End);          // ",5" = 500 ms
    }

    [Theory]
    [InlineData(0.5, null)]
    [InlineData(2.0, "Primo")]
    [InlineData(4.0, null)]                 // end is exclusive
    [InlineData(6.0, "Secondo")]
    public void TextAt_returns_the_active_cue(double seconds, string? expected)
    {
        var track = Load("""
            1
            00:00:01,000 --> 00:00:04,000
            Primo

            2
            00:00:05,000 --> 00:00:08,000
            Secondo
            """, Encoding.UTF8);
        Assert.Equal(expected, track.TextAt(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Long_cue_stays_visible_under_many_short_ones()
    {
        var sb = new StringBuilder("1\n00:00:00,000 --> 00:00:30,000\nLONG\n\n");
        for (int i = 0; i < 8; i++)
            sb.Append($"{i + 2}\n00:00:{2 + i * 3:00},000 --> 00:00:{4 + i * 3:00},000\nshort {i + 1}\n\n");
        var track = Load(sb.ToString(), Encoding.UTF8);

        Assert.Equal("LONG\nshort 7", track.TextAt(TimeSpan.FromSeconds(20)));
        Assert.Equal("LONG", track.TextAt(TimeSpan.FromSeconds(29)));
        Assert.Null(track.TextAt(TimeSpan.FromSeconds(31)));
    }

    [Fact]
    public void WriteSrt_round_trips()
    {
        var cues = new List<SubtitleCue>
        {
            new(TimeSpan.FromMilliseconds(1_840), TimeSpan.FromMilliseconds(5_360), "The morning was quiet."),
            new(TimeSpan.FromHours(1) + TimeSpan.FromMilliseconds(5), TimeSpan.FromHours(1) + TimeSpan.FromSeconds(3), "Line one\nline two àèì"),
        };
        var path = TestFiles.TempPath(".srt");
        SubtitleTrack.WriteSrt(path, cues);

        Assert.Equal(cues, SubtitleTrack.Load(path).Cues);
    }

    [Fact]
    public void Transcript_has_chapter_headings_and_paragraphs()
    {
        var cues = new List<SubtitleCue>
        {
            new(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(2), "One."),
            new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), "Two."),
            new(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(9), "After a pause."),
            new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12), "Chapter two text."),
        };
        var chapters = new List<Chapter> { new("First", TimeSpan.Zero, TimeSpan.Zero), new("Second", TimeSpan.FromSeconds(10), TimeSpan.Zero) };
        var path = TestFiles.TempPath(".txt");
        SubtitleTrack.WriteTranscript(path, cues, chapters);

        Assert.Equal("## First\r\n\r\nOne. Two.\r\n\r\nAfter a pause.\r\n\r\n## Second\r\n\r\nChapter two text.", File.ReadAllText(path));
    }
}
