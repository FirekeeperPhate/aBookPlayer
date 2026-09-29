namespace aBookPlayer;

// Second subtitles shown under the first: a translation, for listening to a book in a language being learned
public sealed partial class MainForm
{
    SubtitleTrack? _subs2;
    string? _srt2Path;
    bool _secondRemoved;   // removed on purpose for this book: the translation next to it is not loaded again

    void OpenSecondSrtDialog()
    {
        if (_subs == null)
        {
            MessageBox.Show(this, "Load the book's subtitles first: the second ones (a translation) are shown under them.",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new OpenFileDialog { Title = "Load second subtitles (a translation)", Filter = "SubRip subtitles (*.srt)|*.srt|All files (*.*)|*.*" };
        if (_srtPath != null) dlg.InitialDirectory = Path.GetDirectoryName(_srtPath);
        if (dlg.ShowDialog(this) == DialogResult.OK) LoadSecondSrt(dlg.FileName);
    }

    void LoadSecondSrt(string path, bool quiet = false)
    {
        try
        {
            var track = SubtitleTrack.Load(path);
            if (track.Cues.Count == 0)
            {
                if (!quiet) MessageBox.Show(this, "The file does not contain valid SRT subtitles.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _subs2 = track;
            _srt2Path = Path.GetFullPath(path);
            _secondRemoved = false;
            if (!quiet) ShowOsd($"Second subtitles: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            if (!quiet) MessageBox.Show(this, $"Could not read the subtitles:\n{ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        UpdateUi();
    }

    void RemoveSecondSubtitles()
    {
        if (_subs2 == null) return;
        _subs2 = null;
        _srt2Path = null;
        _secondRemoved = true;
        UpdateUi();
    }

    /// <summary>A new book (or none): its second subtitles are decided when it is restored.</summary>
    void ResetSecondSubtitles()
    {
        _subs2 = null;
        _srt2Path = null;
        _secondRemoved = false;
    }

    /// <summary>
    /// The book's second subtitles: the ones chosen last time, or else a translation made by the transcription
    /// ("Book.en.srt") next to it, unless they were removed on purpose.
    /// </summary>
    void RestoreSecondSubtitles(string path, BookState? book)
    {
        if (_subs == null) return;
        var saved = book?.SecondSubtitleFile;
        if (saved == "") _secondRemoved = true;
        else if (saved != null && File.Exists(saved)) LoadSecondSrt(saved, quiet: true);
        else if (BookSource.TranslationPath(path) is var translation && File.Exists(translation) && !SamePath(translation, _srtPath))
            LoadSecondSrt(translation, quiet: true);
    }

    /// <summary>Kept per book with its other state (see <see cref="RememberCurrentBook"/>).</summary>
    void RememberSecondSubtitles(BookState book)
    {
        if (_srt2Path != null) book.SecondSubtitleFile = _srt2Path;
        else if (_secondRemoved) book.SecondSubtitleFile = "";
    }

    string? SecondSubtitleAt(TimeSpan position) => _subs2?.TextAt(position - _subOffset);
}
