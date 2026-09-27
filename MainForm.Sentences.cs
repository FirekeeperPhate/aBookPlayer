namespace aBookPlayer;

// Sentence by sentence (from the subtitles' timing), and chapters found in the subtitles
public sealed partial class MainForm
{
    // ───────────────────────────── Chapters ─────────────────────────────

    /// <summary>The chapters the file itself has (tags or one per file), kept when others are found in the subtitles.</summary>
    List<Chapter> _fileChapters = [];
    bool _chaptersFromSubtitles;

    void SetChapters(List<Chapter> chapters)
    {
        _chapters = chapters;
        _currentChapter = -1;
        _lstChapters.BeginUpdate();
        _lstChapters.Items.Clear();
        foreach (var c in _chapters) _lstChapters.Items.Add(c.Title);
        _lstChapters.EndUpdate();
        _seek.Marks = _chapters.Select(c => c.Start.TotalSeconds).ToArray();
    }

    /// <summary>
    /// A book without chapters of its own gets the ones read out by the narrator ("Chapter 12"…), found in the
    /// subtitles; removing the subtitles gives the file's chapters back.
    /// </summary>
    void ApplySubtitleChapters()
    {
        if (_fileChapters.Count > 1) return;
        var found = _subs != null ? ChapterDetector.Detect(_subs.Cues) : [];
        if (found.Count == 0)
        {
            if (_chaptersFromSubtitles) SetChapters(_fileChapters);
            _chaptersFromSubtitles = false;
            return;
        }
        // Subtitle times are file time plus the sync offset; before the first heading there is usually an opening
        var chapters = found.Select(c => c with { Start = c.Start + _subOffset }).ToList();
        if (chapters[0].Start > TimeSpan.FromSeconds(30)) chapters.Insert(0, new Chapter("Opening", TimeSpan.Zero, TimeSpan.Zero));
        SetChapters(BuildChapters(chapters, _player.Duration));
        _chaptersFromSubtitles = true;
        ShowOsd($"{found.Count} chapters found in the subtitles");
    }

    // ───────────────────────────── Sentences ─────────────────────────────

    /// <summary>The sentence being looped (file time), or null.</summary>
    (TimeSpan Start, TimeSpan End)? _loop;

    /// <summary>Index of the subtitle line heard at <paramref name="position"/> (the last one started), or -1.</summary>
    int CueIndexAt(TimeSpan position)
    {
        if (_subs == null) return -1;
        var cues = _subs.Cues;
        int lo = 0, hi = cues.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (cues[mid].Start + _subOffset <= position) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    bool NeedsSubtitles()
    {
        if (_subs != null) return false;
        ShowOsd("Sentences come from the subtitles: load an .srt (Ctrl+T) or transcribe the book (Ctrl+R)");
        return true;
    }

    /// <summary>Back to the start of the sentence being heard (R), to catch it again.</summary>
    void RepeatSentence()
    {
        if (!_player.IsLoaded || NeedsSubtitles()) return;
        int i = CueIndexAt(_player.Position);
        if (i < 0) return;
        GoToCue(i);
    }

    /// <summary>Previous sentence (Shift+←); within the first 1.5 s of a sentence, the one before it.</summary>
    void PreviousSentence()
    {
        if (!_player.IsLoaded || NeedsSubtitles()) return;
        var position = _player.Position;
        int i = CueIndexAt(position);
        if (i < 0) return;
        if (position - (_subs!.Cues[i].Start + _subOffset) < TimeSpan.FromSeconds(1.5) && i > 0) i--;
        GoToCue(i);
    }

    /// <summary>Next sentence (Shift+→).</summary>
    void NextSentence()
    {
        if (!_player.IsLoaded || NeedsSubtitles()) return;
        int i = CueIndexAt(_player.Position) + 1;
        if (i < _subs!.Cues.Count) GoToCue(i);
    }

    void GoToCue(int index)
    {
        var cue = _subs!.Cues[index];
        var start = cue.Start + _subOffset;
        // Moving elsewhere ends a loop; staying on its sentence keeps it
        if (_loop is { } loop && (start < loop.Start || start >= loop.End)) StopLoop(quiet: true);
        SeekTo(start > TimeSpan.Zero ? start : TimeSpan.Zero);
    }

    /// <summary>Plays the current sentence over and over (L), e.g. to catch a hard passage; L again stops.</summary>
    void ToggleSentenceLoop()
    {
        if (_loop != null)
        {
            StopLoop(quiet: false);
            return;
        }
        if (!_player.IsLoaded || NeedsSubtitles()) return;
        int i = CueIndexAt(_player.Position);
        if (i < 0) return;
        var cue = _subs!.Cues[i];
        _loop = (cue.Start + _subOffset, cue.End + _subOffset);
        ShowOsd("Looping this sentence: L to stop");
        GoToCue(i);
        if (!_player.IsPlaying) PlayResuming();
    }

    void StopLoop(bool quiet)
    {
        if (_loop == null) return;
        _loop = null;
        if (!quiet) ShowOsd("Loop off");
    }

    /// <summary>Called on every UI tick: back to the start of the looped sentence once it has been heard.</summary>
    void UpdateLoop(TimeSpan position)
    {
        if (_loop is not { } loop || !_player.IsPlaying) return;
        // A seek far from the sentence (a chapter, the seek bar) ends the loop
        if (position < loop.Start - TimeSpan.FromSeconds(1) || position > loop.End + TimeSpan.FromSeconds(2))
        {
            StopLoop(quiet: false);
            return;
        }
        if (position >= loop.End + TimeSpan.FromMilliseconds(150)) _player.Seek(loop.Start);
    }

    string LoopStatus() => _loop != null ? "  ·  Looping a sentence" : "";
}
