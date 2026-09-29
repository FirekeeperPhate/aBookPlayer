using static aBookPlayer.DialogControls;

namespace aBookPlayer;

// The end of a book (skipping its ending, going on with the series) and the intro skipped at its start
public sealed partial class MainForm
{
    static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    // ───────────────────────────── End of the book ─────────────────────────────

    /// <summary>The book is over: at the real end of the audio, or where its ending (credits) starts being skipped.</summary>
    void OnBookEnded()
    {
        // Nothing left for a sleep timer to stop
        _pausedSince = null;
        var path = _audioPath;
        var finished = CurrentBook;
        if (finished != null)
        {
            finished.Finished = true;
            SaveSettings();
        }
        _sleepAt = null;
        CancelSleepAtChapterEnd();
        _player.Fade = 1;
        UpdateUi();
        if (path != null && finished != null) _ = OfferNextInSeriesAsync(path, finished);
    }

    // ───────────────────────────── Intro and ending ─────────────────────────────

    TimeSpan IntroOf(BookState? book) => TimeSpan.FromSeconds(Math.Max(0, book?.SkipIntroSeconds ?? _settings.DefaultSkipIntroSeconds));
    TimeSpan OutroOf(BookState? book) => TimeSpan.FromSeconds(Math.Max(0, book?.SkipOutroSeconds ?? _settings.DefaultSkipOutroSeconds));

    TimeSpan _lastTickPosition;

    /// <summary>Called on every UI tick: playing into the ending to skip ends the book there.</summary>
    void SkipOutro(TimeSpan pos, TimeSpan duration)
    {
        var last = _lastTickPosition;
        _lastTickPosition = pos;
        var outro = OutroOf(CurrentBook);
        if (!_player.IsPlaying || outro <= TimeSpan.Zero || outro >= duration) return;
        var start = duration - outro;
        // Only when playback crosses into it: a jump into the ending (to hear it after all) plays on
        if (last < start && pos >= start && pos - last < TimeSpan.FromSeconds(2))
        {
            _player.Pause();
            OnBookEnded();
        }
    }

    void ShowSkipIntroOutro()
    {
        var book = _player.IsLoaded ? CurrentBook : null;
        using (var dlg = new SkipForm(book, _settings, _player.IsLoaded ? _player.Position : null, _player.IsLoaded ? _player.Duration : null))
        {
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                SaveSettings();
                ShowOsd(dlg.Summary);
            }
        }
        OpenDeferredFile();
    }

    // ───────────────────────────── Details edited in the library ─────────────────────────────

    /// <summary>What the open book's files say about it (restored when the edits are dropped).</summary>
    sealed record FileDetails(string Title, string? Author, string? Series, int? Number, string Header, byte[]? Cover);

    FileDetails? _fileDetails;

    /// <summary>Title line and cover of the open book: the ones edited in the library, else the files' own.</summary>
    void ShowBookDetails()
    {
        if (_audioPath == null || CurrentBook is not { } book || _fileDetails is not { } file) return;
        _lblTitle.Text = book.DetailsEdited
            ? (string.IsNullOrWhiteSpace(book.Author) ? book.Title ?? file.Title : $"{book.Title}  —  {book.Author}")
            : file.Header;
        SetCover(LibraryCovers.LoadCustom(_audioPath) ?? file.Cover);
        UpdateMediaControls();
    }

    /// <summary>A book's details were edited in the library.</summary>
    void OnDetailsEdited(string path)
    {
        if (SamePath(path, _audioPath) && CurrentBook is { } book && _fileDetails is { } file && !book.DetailsEdited)
        {
            // Back to the file's own details, at once for the open book
            (book.Title, book.Author, book.Series, book.SeriesNumber) = (file.Title, file.Author, file.Series, file.Number);
            _library.SyncWithSettings(_audioPath);
        }
        if (SamePath(path, _audioPath)) ShowBookDetails();
        SaveSettings();
    }

    // ───────────────────────────── Next in the series ─────────────────────────────

    /// <summary>
    /// A book of a series was finished: offers the next one found among the books listened to and, when never
    /// opened, the ones beside it in the library folders (their tags or names are read in the background).
    /// </summary>
    async Task OfferNextInSeriesAsync(string path, BookState book)
    {
        if (string.IsNullOrWhiteSpace(book.Series) || book.SeriesNumber is not int number) return;
        var known = _settings.Books
            .Where(b => !SamePath(b.Key, path))
            .Select(b => new SeriesCandidate(b.Key, b.Value.Title ?? BookSource.NameFromPath(b.Key), b.Value.Series, b.Value.SeriesNumber, b.Value.Finished))
            .ToList();
        // Books never opened: the library has usually read their details already (after scanning its folders)
        var unopened = _library.UnopenedBooks();
        known.AddRange(unopened.Where(u => u.Details != null)
            .Select(u => new SeriesCandidate(u.Path, u.Details!.Title, u.Details.Series, u.Details.Number, false)));
        var next = SeriesOrder.Next(book.Series, number, known);
        if (next == null)
        {
            // Not read yet: the ones stored beside this book, where the rest of a series usually is
            var nearby = SeriesOrder.Nearby(path, unopened.Where(u => u.Details == null).Select(u => u.Path)).Take(200).ToList();
            var read = await Task.Run(() => nearby.Select(p =>
            {
                try
                {
                    var (details, _) = BookSource.ReadDetails(p);
                    return new SeriesCandidate(p, details.Title, details.Series, details.Number, false);
                }
                catch { return null; }
            }).OfType<SeriesCandidate>().ToList());
            next = SeriesOrder.Next(book.Series, number, read);
        }
        // Only if nothing happened meanwhile (another book opened, playback started again)
        if (next == null || IsDisposed || !SamePath(path, _audioPath) || _player.IsPlaying) return;
        if (Application.OpenForms.Cast<Form>().Any(f => f.Modal)) return;
        var label = AudibleExport.SeriesLabel(next.Series, next.Number);
        if (MessageBox.Show(this, $"You finished \"{book.Title}\".\n\nContinue with \"{next.Title}\" ({label})?", AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            await LoadAudioAsync(next.Path);
    }
}

/// <summary>Seconds to skip at the start and at the end of the open book, or of every book without values of its own.</summary>
sealed class SkipForm : DarkDialog
{
    readonly NumericUpDown _intro = MakeSeconds(), _outro = MakeSeconds();
    readonly CheckBox _default = new() { Text = "Use these for every book without values of its own", AutoSize = true, FlatStyle = FlatStyle.Flat };
    readonly BookState? _book;
    readonly AppSettings _settings;

    public string Summary { get; private set; } = "";

    public SkipForm(BookState? book, AppSettings settings, TimeSpan? position, TimeSpan? duration) : base("Skip intro and ending", new Size(520, 250))
    {
        _book = book;
        _settings = settings;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(18, 16, 18, 4) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var info = new Label
        {
            AutoSize = true, ForeColor = Theme.TextDim, MaximumSize = new Size(470, 0), Margin = new Padding(0, 0, 0, 10),
            Text = book != null
                ? $"For \"{book.Title}\": the start is skipped when the book is played from the beginning, and the book ends where the ending to skip begins."
                : "No book is open: these become the values for every book without its own.",
        };
        grid.Controls.Add(info, 0, 0);
        grid.SetColumnSpan(info, 3);
        AddSecondsRow(grid, 1, "Skip at the start", _intro, position is { } p ? () => _intro.Value = Clamp(p.TotalSeconds) : null);
        AddSecondsRow(grid, 2, "Skip at the end", _outro, position is { } q && duration is { } d ? () => _outro.Value = Clamp((d - q).TotalSeconds) : null);
        grid.Controls.Add(_default, 0, 3);
        grid.SetColumnSpan(_default, 3);
        _default.Margin = new Padding(0, 10, 0, 0);
        Controls.Add(grid);
        grid.BringToFront();

        _intro.Value = Clamp(book?.SkipIntroSeconds ?? settings.DefaultSkipIntroSeconds);
        _outro.Value = Clamp(book?.SkipOutroSeconds ?? settings.DefaultSkipOutroSeconds);
        _default.Checked = book == null;
        _default.Enabled = book != null;

        var ok = AddButton("OK", DialogResult.OK);
        CancelButton = AddButton("Cancel", DialogResult.Cancel);
        AcceptButton = ok;
        ok.Click += (_, _) => Apply();
    }

    static decimal Clamp(double seconds) => (decimal)Math.Clamp(Math.Round(seconds), 0, 3600);

    static NumericUpDown MakeSeconds() => new()
    {
        Minimum = 0, Maximum = 3600, Width = 80, BackColor = Theme.Surface, ForeColor = Theme.Text,
        BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Center,
    };

    /// <summary>"Skip at the start  [ 12 ] seconds  [Up to here]": the button takes the current position.</summary>
    static void AddSecondsRow(TableLayoutPanel grid, int row, string caption, NumericUpDown box, Action? fromPosition)
    {
        grid.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = Theme.TextDim, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 16, 8) }, 0, row);
        var cell = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 3, 0, 3) };
        cell.Controls.Add(box);
        cell.Controls.Add(new Label { Text = "seconds", AutoSize = true, ForeColor = Theme.TextDim, Margin = new Padding(8, 6, 16, 0) });
        grid.Controls.Add(cell, 1, row);
        if (fromPosition != null)
        {
            var button = MakeButton(row == 1 ? "Up to here" : "From here", 110, 30);
            button.Margin = new Padding(0, 1, 0, 1);
            button.Click += (_, _) => fromPosition();
            grid.Controls.Add(button, 2, row);
        }
    }

    void Apply()
    {
        double intro = (double)_intro.Value, outro = (double)_outro.Value;
        if (_book != null)
        {
            _book.SkipIntroSeconds = intro;
            _book.SkipOutroSeconds = outro;
        }
        if (_default.Checked)
        {
            _settings.DefaultSkipIntroSeconds = intro;
            _settings.DefaultSkipOutroSeconds = outro;
        }
        Summary = intro == 0 && outro == 0 ? "Nothing skipped at the start or end"
            : $"Skipping {intro:0} s at the start, {outro:0} s at the end";
    }
}
