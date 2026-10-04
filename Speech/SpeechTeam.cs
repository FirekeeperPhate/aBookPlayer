using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.ML.OnnxRuntime;

namespace aBookPlayer;

/// <summary>
/// A voice at work on a book, speaking several sentences at a time. On the processor one model does it (its runs
/// share the cores: about twice the speed of one sentence after the other); on the graphics card every sentence
/// at once needs a model of its own there, since a card's model runs one thing at a time, and most of what a
/// sentence costs it is getting ready for its length, which the processor does for each of them.
/// </summary>
sealed class SpeechTeam : IDisposable
{
    readonly ISpeechVoice[] _voices;
    /// <summary>A voice for each sentence that can be spoken now (on the processor, the same one many times).</summary>
    readonly BlockingCollection<ISpeechVoice> _free = [];

    public int SampleRate => _voices[0].SampleRate;
    public bool OnGpu => _voices[0].OnGpu;
    /// <summary>How many sentences are spoken at the same time.</summary>
    public int AtOnce { get; }

    internal SpeechTeam(ISpeechVoice[] voices, int atOnce)
    {
        _voices = voices;
        AtOnce = atOnce;
        for (int i = 0; i < atOnce; i++) _free.Add(voices[i % voices.Length]);
    }

    /// <summary>How many sentences a processor speaks at once to good use: more than four gain nothing.</summary>
    static int ProcessorAtOnce => Math.Clamp(Environment.ProcessorCount / 4, 1, 4);

    /// <summary>
    /// The voice as it speaks fastest on this PC, and a line saying how. On the processor, or, if asked for
    /// (<paramref name="gpu"/>) and <paramref name="worthTiming"/> (a short text is done before the timing would
    /// be), on the graphics card when that is faster: one model there, then one more as long as each makes it
    /// clearly faster.
    /// </summary>
    public static (SpeechTeam Team, string Note) Open(SpeechVoiceInfo info, bool gpu, bool worthTiming, CancellationToken ct)
    {
        static string Times(double speed) => speed.ToString(speed < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + "×";
        static string AtOnceText(int n) => n == 1 ? "one sentence at a time" : $"{n} sentences at a time";

        var processor = new SpeechTeam([SpeechVoices.Open(info, gpu: false)], ProcessorAtOnce);
        string card = SpeechRuntime.UsesCuda ? "graphics card (CUDA)" : "graphics card";
        if (!gpu || !worthTiming) return (processor, $"On the processor, {AtOnceText(processor.AtOnce)}.");

        var cards = new List<ISpeechVoice>();
        try
        {
            double onProcessor = processor.Measure(ct), onCard = 0;
            string? refused = null;
            while (cards.Count < ProcessorAtOnce)
            {
                ct.ThrowIfCancellationRequested();
                int before = cards.Count;
                double speed = 0;
                try
                {
                    var voice = SpeechVoices.Open(info, gpu: true);
                    cards.Add(voice);
                    if (voice.OnGpu) speed = new SpeechTeam(cards.ToArray(), cards.Count).Measure(ct);
                }
                // The card cannot run the voice, or has no room for another model of it: without this one, then
                catch (OnnxRuntimeException) { }
                // One more is kept when it makes the card clearly faster
                if (speed <= 0 || (before > 0 && speed < onCard * 1.25))
                {
                    if (cards.Count > before)
                    {
                        cards[^1].Dispose();
                        cards.RemoveAt(before);
                    }
                    if (before == 0)
                        refused = SpeechRuntime.UsesCuda && SpeechRuntime.GpuError?.Contains("driver version is insufficient") == true
                            ? "The NVIDIA driver is too old for CUDA: update it to use the card. " : $"The {card} cannot run this voice. ";
                    break;
                }
                onCard = speed;
                // (A card far behind the processor with one model is not tried with two)
                if (cards.Count == 1 && onCard < onProcessor * 0.6) break;
            }
            if (onCard > onProcessor)
            {
                processor.Dispose();
                return (new SpeechTeam(cards.ToArray(), cards.Count),
                    $"On the {card}, {AtOnceText(cards.Count)}: {Times(onCard)} real time (the processor: {Times(onProcessor)}).");
            }
            foreach (var voice in cards) voice.Dispose();
            return (processor, refused != null
                ? $"{refused}On the processor, {AtOnceText(processor.AtOnce)}: {Times(onProcessor)} real time."
                : $"On the processor, {AtOnceText(processor.AtOnce)}: {Times(onProcessor)} real time (the {card}: {Times(onCard)}).");
        }
        catch
        {
            processor.Dispose();
            foreach (var voice in cards) voice.Dispose();
            throw;
        }
    }

    static readonly string[] Trial =
    [
        "The harbor bells rang twice, and the fishing boats turned slowly toward home.",
        "Nobody noticed the stranger at the corner table.",
        "It was the kind of story that people tell only once, and never quite the same way again.",
        "She opened the letter slowly, afraid of what it would say.",
        "By the time the stars came out, the village had gathered around the great fire in the square.",
        "\"Was it worth it?\" he asked.",
        "Snow had fallen during the night, and the whole valley lay silent under a pale winter sky.",
        "He lit the lantern and watched its warm light spill across the frozen window.",
    ];

    /// <summary>Seconds of speech made in a second, on sentences of several lengths (a card gets ready again for each).</summary>
    double Measure(CancellationToken ct)
    {
        foreach (var voice in _voices) voice.Speak("Ready.", 1);
        var clock = Stopwatch.StartNew();
        long samples = Speak(Trial, 1, ct).Sum(audio => (long)audio.Length);
        return samples / (double)SampleRate / Math.Max(0.001, clock.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// The sentences (written out as they are read: see <see cref="SpeechText.Spell"/>) as samples, in their order,
    /// each given as soon as it and the ones before it are spoken.
    /// </summary>
    public IEnumerable<float[]> Speak(IReadOnlyList<string> sentences, double speed, CancellationToken ct)
    {
        // A few more than can be spoken at once are asked for, so no voice waits while the first is a long one
        int window = AtOnce == 1 ? 1 : AtOnce * 2, next = 0;
        var ahead = new Queue<Task<float[]>>();
        try
        {
            while (next < sentences.Count || ahead.Count > 0)
            {
                while (next < sentences.Count && ahead.Count < window)
                {
                    string sentence = sentences[next++];
                    ahead.Enqueue(Task.Run(() =>
                    {
                        var voice = _free.Take(ct);
                        try { return voice.Speak(sentence, speed); }
                        finally { _free.Add(voice); }
                    }, ct));
                }
                yield return ahead.Dequeue().GetAwaiter().GetResult();
            }
        }
        finally
        {
            // (Stopped halfway: nothing is left speaking when the voices are closed)
            try { Task.WaitAll(ahead.ToArray()); }
            catch (AggregateException) { }
        }
    }

    public void Dispose()
    {
        foreach (var voice in _voices) voice.Dispose();
        _free.Dispose();
    }
}
