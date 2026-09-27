using NAudio.Wave;

namespace aBookPlayer.Tests;

/// <summary>Chapters found in the subtitles, skipping silences, statistics and the bookmark export.</summary>
public class ListeningTests
{
    static SubtitleCue Cue(double start, string text) => new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(start + 3), text);

    // ───────── Chapters from the subtitles ─────────

    [Fact]
    public void Headings_read_by_the_narrator_become_chapters()
    {
        var cues = new[]
        {
            Cue(2, "Prologue."),
            Cue(20, "It was a dark night and nobody had seen the chapter two of his life coming."), // in a sentence: not a heading
            Cue(300, "Chapter One. The Road."),
            Cue(302, "Chapter One."),                         // repeated right after: the same chapter
            Cue(900, "CHAPTER TWENTY-ONE"),
            Cue(1500, "Capitolo 3"),
            Cue(2100, "Epilogue"),
        };
        var chapters = ChapterDetector.Detect(cues);
        Assert.Equal(["Prologue", "Chapter One: The Road", "Chapter TWENTY-ONE", "Capitolo 3", "Epilogue"],
            chapters.Select(c => c.Title).ToArray());
        Assert.Equal([2.0, 300, 900, 1500, 2100], chapters.Select(c => c.Start.TotalSeconds).ToArray());
    }

    [Fact]
    public void Part_and_Book_count_only_as_headings_standing_alone()
    {
        var cues = new[]
        {
            Cue(10, "Part One."),
            Cue(200, "Part one of the problem is that nobody listened."),   // an ordinary sentence
            Cue(400, "Book two was better, everybody said."),              // another one
            Cue(600, "Part Two: The Return"),
            Cue(900, "Book 3"),
        };
        Assert.Equal(["Part One", "Part Two: The Return", "Book 3"], ChapterDetector.Detect(cues).Select(c => c.Title).ToArray());
    }

    [Fact]
    public void Voice_boost_raises_the_silence_level_by_the_gain_it_gives_quiet_sound()
    {
        // Room tone at -45 dBFS: silence as it is, but not once Voice boost has lifted it by 10 dB
        float roomTone = (float)Math.Pow(10, -45 / 20.0);
        Assert.True(roomTone < AudioPlayer.TrackingSampleProvider.SilenceLevel);
        Assert.False(roomTone * VoiceBoost.QuietGain < AudioPlayer.TrackingSampleProvider.SilenceLevel);
        Assert.True(roomTone * VoiceBoost.QuietGain < AudioPlayer.TrackingSampleProvider.SilenceLevel * VoiceBoost.QuietGain);
    }

    [Fact]
    public void A_couple_of_mentions_are_not_a_chapter_structure()
    {
        Assert.Empty(ChapterDetector.Detect([Cue(10, "Chapter 1"), Cue(500, "Chapter 2")]));
        Assert.Empty(ChapterDetector.Detect([]));
    }

    // ───────── Skipping silences ─────────

    /// <summary>1 s of sound, <paramref name="silence"/> s of silence, 1 s of sound (44.1 kHz stereo float).</summary>
    static RawSourceWaveStream SoundSilenceSound(double silence)
    {
        int rate = 44100, frames = (int)((2 + silence) * rate);
        var data = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            double t = f / (double)rate;
            float v = t < 1 || t >= 1 + silence ? 0.3f * (float)Math.Sin(f * 0.05) + 0.2f : 0f;
            data[2 * f] = data[2 * f + 1] = v;
        }
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        return new RawSourceWaveStream(new MemoryStream(bytes), WaveFormat.CreateIeeeFloatWaveFormat(rate, 2));
    }

    [Fact]
    public void Long_pauses_are_shortened_and_the_position_stays_exact()
    {
        using var stream = SoundSilenceSound(silence: 2.0);
        var stretch = new TimeStretchSampleProvider(stream.ToSampleProvider());
        stretch.Reset(0, 1.0);
        var tracker = new AudioPlayer.TrackingSampleProvider(stream, stretch) { SkipSilences = true };

        var buffer = new float[4410 * 2];
        long total = 0;
        int n;
        while ((n = tracker.Read(buffer, 0, buffer.Length)) > 0) total += n;

        // 1 s + 0.25 s of the pause + 1 s, instead of 4 s
        double seconds = total / 2 / 44100.0;
        Assert.InRange(seconds, 2.2, 2.3);
        Assert.InRange(tracker.TakeSkippedTime().TotalSeconds, 1.7, 1.8);
        // The first sample after the cut is heard at 3 s of the book, not at 1.25 s
        long afterCut = (long)(1.26 * 44100) * 2;
        Assert.InRange(tracker.TimeAt(afterCut).TotalSeconds, 3.0, 3.02);
    }

    [Fact]
    public void Short_pauses_and_quiet_speech_are_untouched()
    {
        using var stream = SoundSilenceSound(silence: 0.2); // shorter than what is kept
        var stretch = new TimeStretchSampleProvider(stream.ToSampleProvider());
        stretch.Reset(0, 1.0);
        var tracker = new AudioPlayer.TrackingSampleProvider(stream, stretch) { SkipSilences = true };
        var buffer = new float[4410 * 2];
        long total = 0;
        int n;
        while ((n = tracker.Read(buffer, 0, buffer.Length)) > 0) total += n;
        Assert.InRange(total / 2 / 44100.0, 2.19, 2.21);
    }

    // ───────── Statistics ─────────

    [Fact]
    public void Listening_time_adds_up_per_day_and_gives_a_finish_date()
    {
        var settings = new AppSettings();
        var today = new DateTime(2026, 9, 28, 21, 0, 0);
        for (int d = 0; d < 14; d++) ListeningStats.Add(settings, today.AddDays(-d), 3600); // an hour a day

        Assert.Equal(TimeSpan.FromHours(1), ListeningStats.Day(settings, today));
        Assert.Equal(TimeSpan.FromHours(7), ListeningStats.LastDays(settings, today, 7));
        Assert.Equal(TimeSpan.FromHours(14), ListeningStats.Total(settings));
        // 3 hours left at an hour a day: finished the day after tomorrow
        Assert.Equal(today.Date.AddDays(2), ListeningStats.EstimatedFinish(settings, today, TimeSpan.FromHours(3)));
        Assert.Null(ListeningStats.EstimatedFinish(new AppSettings(), today, TimeSpan.FromHours(3)));
    }

    // ───────── Bookmark export ─────────

    [Fact]
    public void Bookmarks_are_exported_by_chapter_with_the_words_spoken()
    {
        var md = BookmarkExport.ToMarkdown("The Test Journey", "Ann Author",
            [new Bookmark { Seconds = 75, Note = "Great line" }, new Bookmark { Seconds = 5, Note = "" }],
            s => s < 60 ? "Chapter 1" : "Chapter 2",
            s => s < 60 ? "Once upon a time" : "A storm broke");

        Assert.Equal(
            "# The Test Journey\n\n*Ann Author*\n\n## Chapter 1\n\n- **00:05**\n  > Once upon a time\n\n## Chapter 2\n\n- **01:15**: Great line\n  > A storm broke\n",
            md.Replace("\r\n", "\n"));
    }
}
