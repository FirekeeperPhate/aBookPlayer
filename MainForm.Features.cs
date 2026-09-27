namespace aBookPlayer;

// Listening aids: smart rewind, bookmarks, search in the subtitles, cover, voice boost
public sealed partial class MainForm
{
    // ───────────────────────────── Smart rewind ─────────────────────────────

    /// <summary>When playback was paused (or the book last listened to), for the smart rewind on the next Play.</summary>
    DateTime? _pausedSince;

    void MarkPaused() => _pausedSince ??= DateTime.Now;

    /// <summary>
    /// How far to go back after a pause: nothing for a short break, then 5 s, 15 s and 30 s after a minute,
    /// ten minutes and an hour, so the thread of the story is easy to pick up again.
    /// </summary>
    internal static int SmartRewindSeconds(TimeSpan pause) =>
        pause >= TimeSpan.FromHours(1) ? 30 : pause >= TimeSpan.FromMinutes(10) ? 15 : pause >= TimeSpan.FromMinutes(1) ? 5 : 0;

    /// <summary>Starts playback, first going back a little if the book has been paused for a while.</summary>
    void PlayResuming()
    {
        if (_settings.SmartRewind && _pausedSince is { } since)
        {
            int seconds = SmartRewindSeconds(DateTime.Now - since);
            var position = _player.Position;
            if (seconds > 0 && position > TimeSpan.FromSeconds(1))
            {
                var target = position - TimeSpan.FromSeconds(seconds);
                _player.Seek(target > TimeSpan.Zero ? target : TimeSpan.Zero);
                ShowOsd($"Back {seconds} s after the pause");
            }
        }
        _pausedSince = null;
        _keepSavedPosition = false;
        MarkListening();
        _player.Play();
    }

    /// <summary>A finished book played again is in progress once more (library, sync).</summary>
    void MarkListening()
    {
        if (CurrentBook is { Finished: true } book) book.Finished = false;
    }

    // ───────────────────────────── Bookmarks ─────────────────────────────

    BookState? CurrentBook => _audioPath != null ? _settings.GetBook(_audioPath) : null;

    void AddBookmark()
    {
        if (!_player.IsLoaded || _audioPath == null) return;
        var position = _player.Position;
        // Suggest the words being spoken (or the chapter) as the note
        var suggestion = _subs?.TextAt(position - _subOffset)?.Replace('\n', ' ')
                         ?? (ChapterIndexAt(position) is var i and >= 0 ? _chapters[i].Title : "");
        var note = InputDialog.Ask(this, "Add bookmark", $"Bookmark at {FormatTime(position)}. Note:", suggestion);
        if (note == null) return;
        RememberCurrentBook();
        var book = CurrentBook!;
        book.Bookmarks.Add(new Bookmark { Seconds = position.TotalSeconds, Note = note, Created = DateTime.UtcNow });
        book.Bookmarks.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));
        SaveSettings();
        RefreshBookmarkMarks();
        ShowOsd($"Bookmark added at {FormatTime(position)}");
    }

    void ShowBookmarks()
    {
        if (!_player.IsLoaded || _audioPath == null) return;
        RememberCurrentBook();
        var book = CurrentBook!;
        using (var dlg = new BookmarksForm(book.Bookmarks,
                   s =>
                   {
                       int i = ChapterIndexAt(TimeSpan.FromSeconds(s));
                       return i >= 0 ? _chapters[i].Title : "";
                   },
                   s => _subs?.TextAt(TimeSpan.FromSeconds(s) - _subOffset),
                   book.Title ?? BookSource.NameFromPath(_audioPath),
                   book.Author))
        {
            var result = dlg.ShowDialog(this);
            SaveSettings();
            RefreshBookmarkMarks();
            if (result == DialogResult.OK && dlg.Selected != null) SeekTo(TimeSpan.FromSeconds(dlg.Selected.Seconds));
        }
        OpenDeferredFile();
    }

    void RefreshBookmarkMarks() =>
        _seek.Bookmarks = CurrentBook?.Bookmarks.Select(b => b.Seconds).ToArray() ?? [];

    // ───────────────────────────── Search ─────────────────────────────

    string _lastSearch = "";

    void ShowSearch()
    {
        if (_subs == null)
        {
            MessageBox.Show(this, "This book has no subtitles to search yet.\n\nLoad an .srt file (Ctrl+T) or create one with Whisper (Ctrl+R).",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using (var dlg = new SearchForm(_subs.Cues, _lastSearch))
        {
            var result = dlg.ShowDialog(this);
            _lastSearch = dlg.Query;
            // Subtitle times are in the file's time: undo the sync offset to land where the words are heard
            if (result == DialogResult.OK && dlg.Selected is { } time) SeekTo(time + _subOffset);
        }
        OpenDeferredFile();
    }

    // ───────────────────────────── Subtitle area: click, copy ─────────────────────────────

    /// <summary>A click on the subtitles plays/pauses; right-click (or Ctrl+C) copies the line on screen.</summary>
    void SetUpSubtitleArea()
    {
        _subView.Clicked += (_, _) => TogglePlay();
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false };
        var copy = new ToolStripMenuItem("Copy") { ShortcutKeyDisplayString = "Ctrl+C", ShowShortcutKeys = true };
        copy.Click += (_, _) => CopySubtitle();
        menu.Items.Add(copy);
        menu.Opening += (_, _) => copy.Enabled = _subView.SubtitleText != null;
        _subView.ContextMenuStrip = menu;
    }

    void CopySubtitle()
    {
        if (_subView.SubtitleText is not { } text) return;
        try
        {
            Clipboard.SetText(text.Replace("\n", Environment.NewLine));
            ShowOsd("Subtitle copied");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ShowOsd("The clipboard is busy: try again"); // another app holds it open
        }
    }

    // ───────────────────────────── Cover ─────────────────────────────

    readonly PictureBox _cover = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom };
    // PictureBox ignores its own padding in Zoom mode: the margin comes from this host
    readonly Panel _coverHost = new() { Dock = DockStyle.Left, Width = 90, Padding = new Padding(20, 14, 4, 6), Visible = false };
    byte[]? _coverBytes;

    void SetCover(byte[]? bytes)
    {
        var old = _cover.Image;
        _coverBytes = bytes;
        _cover.Image = CoverArt.ToImage(bytes);
        _coverHost.Visible = _cover.Image != null;
        old?.Dispose();
    }

    // ───────────────────────────── Library ─────────────────────────────

    const int LibraryWidth = 330;
    int MinimumWidth => _settings.ShowLibrary ? 780 + LibraryWidth : 780;

    readonly LibraryPanel _library;
    ToolStripMenuItem? _libraryItem;

    ToolStripMenuItem MakeLibraryItem()
    {
        _libraryItem = new ToolStripMenuItem("Library") { Checked = _settings.ShowLibrary, ShortcutKeyDisplayString = "Ctrl+L", ShowShortcutKeys = true };
        _libraryItem.Click += (_, _) => ToggleLibrary();
        return _libraryItem;
    }

    /// <summary>
    /// Shows or hides the library panel. The window grows or shrinks by the panel's width, so the subtitles keep
    /// their room (unless the window is maximized).
    /// </summary>
    void ToggleLibrary()
    {
        bool show = _settings.ShowLibrary = !_settings.ShowLibrary;
        if (_libraryItem != null) _libraryItem.Checked = show;
        int delta = LogicalToDeviceUnits(LibraryWidth);
        SuspendLayout();
        if (!show) MinimumSize = new Size(LogicalToDeviceUnits(MinimumWidth), MinimumSize.Height);
        if (WindowState == FormWindowState.Normal)
        {
            var area = Screen.FromControl(this).WorkingArea;
            int width = Math.Min(area.Width, Width + (show ? delta : -delta));
            // Growing past the screen's right edge: move the window left instead
            Bounds = new Rectangle(Math.Max(area.Left, Math.Min(Left, area.Right - width)), Top, width, Height);
        }
        _library.Visible = show;
        // Limited to the screen first: a minimum wider than the screen would push the window past its edge
        if (show) MinimumSize = new Size(Math.Min(LogicalToDeviceUnits(MinimumWidth), Screen.FromControl(this).WorkingArea.Width), MinimumSize.Height);
        FitToScreen();
        ResumeLayout(true);
        if (show) _library.FocusList();
        SaveSettings();
    }

    /// <summary>
    /// A click outside the library while its search box (or a filter) has the focus gives the focus back to the
    /// window, so Space plays again: the subtitles and the panels cannot take the focus themselves.
    /// </summary>
    sealed class LibraryFocusFilter(MainForm form) : IMessageFilter
    {
        const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN && form._library.TextInputFocused
                && Control.FromHandle(m.HWnd) is { } target && target.FindForm() == form && !form._library.Contains(target))
                form.ActiveControl = null;
            return false; // the click itself goes on as usual
        }
    }

    /// <summary>Saves the small cover picture the library shows, then shows it there.</summary>
    async Task SaveLibraryCoverAsync(string path, byte[]? cover)
    {
        await Task.Run(() => LibraryCovers.Save(path, cover));
        _library.ReloadCover(path);
    }

    // ───────────────────────────── Voice boost ─────────────────────────────

    ToolStripMenuItem MakeVoiceBoostItem()
    {
        var item = new ToolStripMenuItem("Voice boost (even out the volume)") { Checked = _settings.VoiceBoost, ShortcutKeyDisplayString = "V", ShowShortcutKeys = true };
        item.Click += (_, _) => ToggleVoiceBoost();
        _voiceBoostItem = item;
        return item;
    }

    ToolStripMenuItem? _voiceBoostItem;

    void ToggleVoiceBoost()
    {
        _settings.VoiceBoost = _player.VoiceBoost = !_player.VoiceBoost;
        if (_voiceBoostItem != null) _voiceBoostItem.Checked = _settings.VoiceBoost;
        ShowOsd(_settings.VoiceBoost ? "Voice boost on" : "Voice boost off");
        SaveSettings();
    }

    ToolStripMenuItem MakeSmartRewindItem()
    {
        var item = new ToolStripMenuItem("Smart rewind after a pause") { Checked = _settings.SmartRewind };
        item.Click += (_, _) =>
        {
            _settings.SmartRewind = item.Checked = !item.Checked;
            SaveSettings();
        };
        return item;
    }
}
