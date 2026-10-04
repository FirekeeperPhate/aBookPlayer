using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace aBookPlayer;

/// <summary>How far the narration of a book is.</summary>
/// <param name="Fraction">0–1 of the words of the chosen chapters.</param>
/// <param name="Sentence">The sentence just spoken (null while a chapter's file is written).</param>
/// <param name="SpeedFactor">Seconds of audio made per second of work, so far.</param>
sealed record NarrationProgress(int Chapter, int Chapters, string ChapterTitle, double Fraction, string? Sentence, double SpeedFactor);

/// <summary>
/// Turns a book in text into an audiobook the player opens as any other: a folder with one MP3 per chapter (tagged
/// with the book's and the chapter's titles), the cover, and one subtitle file for the whole book whose lines are
/// the sentences as they were spoken, timed exactly. A book left halfway is taken up again at the first chapter
/// not yet done.
/// </summary>
static class Narrator
{
    const int Bitrate = 64_000;
    const double SentencePause = 0.35, ParagraphPause = 0.4, HeadingPause = 0.6; // the last two on top of the first
    const string WorkFolder = ".narration";

    /// <summary>Windows has an MP3 encoder (it lacks one only on the "N" editions without the Media Feature Pack).</summary>
    public static bool CanEncode
    {
        get
        {
            try
            {
                MediaFoundationApi.Startup();
                return MediaFoundationEncoder.SelectMediaType(AudioSubtypes.MFAudioFormat_MP3, new WaveFormat(44100, 16, 1), Bitrate) != null;
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { return false; }
        }
    }

    /// <summary>The folder the audiobook is made in: "Author - Title" under <paramref name="root"/>.</summary>
    public static string FolderFor(TextBook book, string root) =>
        Path.Combine(root, SafeName(string.IsNullOrWhiteSpace(book.Author) ? book.Title : $"{book.Author} - {book.Title}"));

    /// <summary>
    /// Narrates the chapters chosen (indexes into the book's), blocking: run it off the UI thread. Returns the
    /// audiobook's folder. <paramref name="signature"/> tells one way of narrating from another (source, voice,
    /// speed): chapters made with the same one are kept, others are made again.
    /// </summary>
    public static string Run(TextBook book, IReadOnlyList<int> chosen, SpeechTeam voice, double speed, string folder, string signature,
        IProgress<NarrationProgress> progress, CancellationToken ct)
    {
        var work = Path.Combine(folder, WorkFolder);
        Directory.CreateDirectory(work);
        File.SetAttributes(work, File.GetAttributes(work) | FileAttributes.Hidden);
        var signatureFile = Path.Combine(work, "job.txt");
        if (!File.Exists(signatureFile) || File.ReadAllText(signatureFile) != signature)
        {
            // Another book, voice or speed was narrated here before: nothing of it is kept
            foreach (var old in Directory.GetFiles(work)) File.Delete(old);
            File.WriteAllText(signatureFile, signature);
        }
        if (book.Cover is { Length: > 0 } cover)
            File.WriteAllBytes(Path.Combine(folder, cover is [0x89, 0x50, ..] ? "cover.png" : "cover.jpg"), cover);

        var chapters = chosen.Select(i => book.Chapters[i]).ToList();
        long totalWords = Math.Max(1, chapters.Sum(c => (long)c.Words)), doneWords = 0;
        string digits = new('0', Math.Max(2, chapters.Count.ToString(CultureInfo.InvariantCulture).Length));
        var files = new List<string>();
        var clock = Stopwatch.StartNew();
        double spoken = 0;

        for (int n = 0; n < chapters.Count; n++)
        {
            var chapter = chapters[n];
            string file = Path.Combine(folder, $"{(n + 1).ToString(digits, CultureInfo.InvariantCulture)} - {SafeName(chapter.Title)}.mp3");
            string cuesFile = Path.Combine(work, $"{n + 1}.json");
            files.Add(file);
            if (File.Exists(file) && File.Exists(cuesFile))
            {
                doneWords += chapter.Words;
                progress.Report(new NarrationProgress(n + 1, chapters.Count, chapter.Title, doneWords / (double)totalWords, null, 0));
                continue;
            }

            var cues = new List<SubtitleCue>();
            var pcm = Path.Combine(work, "chapter.pcm");
            long samples = 0;
            using (var writer = new WaveFileWriter(pcm, new WaveFormat(voice.SampleRate, 16, 1)))
            {
                void Silence(double seconds)
                {
                    int count = (int)(seconds * voice.SampleRate);
                    writer.Write(new byte[count * 2], 0, count * 2);
                    samples += count;
                }
                Silence(0.4);
                // The chapter's sentences are spoken several at a time, and written here in their order
                var sentences = chapter.Paragraphs.Select(p => SpeechText.Sentences(p)).ToList();
                using var voiced = voice.Speak(sentences.SelectMany(s => s).Select(SpeechText.Spell).ToList(), speed, ct).GetEnumerator();
                for (int p = 0; p < chapter.Paragraphs.Count; p++)
                {
                    foreach (var sentence in sentences[p])
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!voiced.MoveNext()) throw new InvalidOperationException("A sentence was not spoken.");
                        var audio = Trim(voiced.Current, voice.SampleRate);
                        if (audio.Length > 0)
                        {
                            var start = TimeSpan.FromSeconds(samples / (double)voice.SampleRate);
                            var bytes = new byte[audio.Length * 2];
                            for (int i = 0; i < audio.Length; i++)
                                BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), (short)Math.Clamp(audio[i] * 32767f, short.MinValue, short.MaxValue));
                            writer.Write(bytes, 0, bytes.Length);
                            samples += audio.Length;
                            spoken += audio.Length / (double)voice.SampleRate;
                            cues.Add(new SubtitleCue(start, TimeSpan.FromSeconds(samples / (double)voice.SampleRate), sentence));
                            Silence(SentencePause);
                        }
                        doneWords += TextBookReader.CountWords(sentence);
                        progress.Report(new NarrationProgress(n + 1, chapters.Count, chapter.Title, Math.Min(1, doneWords / (double)totalWords), sentence,
                            spoken / Math.Max(0.001, clock.Elapsed.TotalSeconds)));
                    }
                    // A longer breath after a paragraph, longer still after the chapter's title
                    Silence(p == 0 && chapter.Paragraphs[0].Length <= 80 ? HeadingPause : ParagraphPause);
                }
            }
            progress.Report(new NarrationProgress(n + 1, chapters.Count, chapter.Title, Math.Min(1, doneWords / (double)totalWords), null,
                spoken / Math.Max(0.001, clock.Elapsed.TotalSeconds)));
            Encode(pcm, file, chapter.Title, book, n + 1, chapters.Count);
            File.Delete(pcm);
            // Last, so a chapter with its cues is a chapter finished
            File.WriteAllText(cuesFile, JsonSerializer.Serialize(cues));
        }

        // The subtitles of the whole book: each chapter's lines moved to where its file starts, as the player
        // counts the files' lengths
        var starts = PartStarts(folder, files);
        var all = new List<SubtitleCue>();
        for (int n = 0; n < chapters.Count; n++)
        {
            var cues = JsonSerializer.Deserialize<List<SubtitleCue>>(File.ReadAllText(Path.Combine(work, $"{n + 1}.json"))) ?? [];
            all.AddRange(cues.Select(c => new SubtitleCue(c.Start + starts[n], c.End + starts[n], c.Text)));
        }
        SubtitleTrack.WriteSrt(BookSource.SubtitlePath(folder), all);
        Directory.Delete(work, recursive: true);
        return folder;
    }

    /// <summary>Where each chapter's file starts in the book, from the lengths the player itself reads.</summary>
    static TimeSpan[] PartStarts(string folder, List<string> files)
    {
        var starts = new TimeSpan[files.Count];
        var (_, reader) = BookAudio.OpenWithInfo(folder);
        using (reader)
        {
            if (reader is not ConcatenatedWaveStream joined) return starts;
            var parts = BookSource.PartsOf(folder);
            for (int n = 0; n < files.Count; n++)
            {
                int index = Array.FindIndex(parts, p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(files[n]), StringComparison.OrdinalIgnoreCase));
                if (index >= 0 && index < joined.PartCount) starts[n] = joined.PartStart(index);
            }
        }
        return starts;
    }

    /// <summary>Without the silence a model puts before and after a sentence (the pauses are added here, all alike).</summary>
    static float[] Trim(float[] audio, int rate)
    {
        const float quiet = 0.004f;
        int margin = rate / 50, first = 0, last = audio.Length - 1;
        while (first < audio.Length && Math.Abs(audio[first]) < quiet) first++;
        while (last > first && Math.Abs(audio[last]) < quiet) last--;
        if (first >= audio.Length) return [];
        first = Math.Max(0, first - margin);
        last = Math.Min(audio.Length - 1, last + margin);
        return first == 0 && last == audio.Length - 1 ? audio : audio[first..(last + 1)];
    }

    /// <summary>The chapter's samples to an MP3 (at twice the voice's rate: the encoder knows 44.1 and 48 kHz), with its ID3 tag in front.</summary>
    static void Encode(string pcm, string file, string chapter, TextBook book, int number, int count)
    {
        MediaFoundationApi.Startup();
        // (The encoder tells the format from the extension)
        var encoded = Path.Combine(Path.GetDirectoryName(pcm)!, "chapter.mp3");
        try
        {
            using (var reader = new WaveFileReader(pcm))
            {
                int rate = reader.WaveFormat.SampleRate <= 22050 ? 44100 : 48000;
                var resampled = new WdlResamplingSampleProvider(reader.ToSampleProvider(), rate).ToWaveProvider16();
                MediaFoundationEncoder.EncodeToMp3(resampled, encoded, Bitrate);
            }
            using var output = File.Create(file + ".tagged");
            var tag = Id3(("TIT2", chapter), ("TALB", book.Title), ("TPE1", book.Author ?? ""), ("TRCK", $"{number}/{count}"), ("TCON", "Audiobook"));
            output.Write(tag);
            using (var audio = File.OpenRead(encoded)) audio.CopyTo(output);
            output.Close();
            File.Move(file + ".tagged", file, overwrite: true);
        }
        finally
        {
            if (File.Exists(encoded)) File.Delete(encoded);
            if (File.Exists(file + ".tagged")) File.Delete(file + ".tagged");
        }
    }

    /// <summary>An ID3v2.3 tag of text frames (UTF-16, as that version wants for anything but Latin letters).</summary>
    internal static byte[] Id3(params (string Id, string Text)[] frames)
    {
        using var body = new MemoryStream();
        foreach (var (id, text) in frames)
        {
            if (text.Length == 0) continue;
            var value = Encoding.Unicode.GetBytes(text);
            int size = 1 + 2 + value.Length;
            body.Write(Encoding.ASCII.GetBytes(id));
            body.Write([(byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size, 0, 0]);
            body.Write([1, 0xFF, 0xFE]); // UTF-16 with its byte order mark
            body.Write(value);
        }
        int length = (int)body.Length;
        using var tag = new MemoryStream();
        tag.Write("ID3"u8);
        // Version 2.3, no flags, the size in four 7-bit bytes
        tag.Write([3, 0, 0, (byte)((length >> 21) & 0x7F), (byte)((length >> 14) & 0x7F), (byte)((length >> 7) & 0x7F), (byte)(length & 0x7F)]);
        body.WriteTo(tag);
        return tag.ToArray();
    }

    /// <summary>A title as a file or folder name: without the characters Windows forbids, not too long.</summary>
    internal static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray());
        clean = string.Join(' ', clean.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        if (clean.Length > 80) clean = clean[..80].Trim('.', ' ');
        return clean.Length > 0 ? clean : "Untitled";
    }
}
