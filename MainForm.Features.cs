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
        using (var dlg = new BookmarksForm(book.Bookmarks, s =>
               {
                   int i = ChapterIndexAt(TimeSpan.FromSeconds(s));
                   return i >= 0 ? _chapters[i].Title : "";
               }))
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

    async void ShowLibrary()
    {
        SaveSettings(); // the library shows the current book's latest position
        string? chosen = null;
        using (var dlg = new LibraryForm(_settings, _audioPath))
        {
            if (dlg.ShowDialog(this) == DialogResult.OK) chosen = dlg.Selected;
        }
        SaveSettings();
        OpenDeferredFile();
        if (chosen != null && !SamePath(chosen, _audioPath)) await LoadAudioAsync(chosen);
    }

    // ───────────────────────────── Updates ─────────────────────────────

    /// <summary>Automatic check at most once a day (silent), or on request from the Help menu.</summary>
    async Task CheckForUpdatesAsync(bool interactive)
    {
        if (!interactive && (!_settings.CheckForUpdates || DateTime.UtcNow - _settings.LastUpdateCheck < TimeSpan.FromDays(1))) return;
        Version? latest = null;
        try { latest = await UpdateCheck.LatestAsync(CancellationToken.None); }
        catch { /* offline */ }
        if (IsDisposed) return;
        if (latest != null) _settings.LastUpdateCheck = DateTime.UtcNow;

        var current = UpdateCheck.CurrentVersion;
        if (latest != null && latest > current)
        {
            if (MessageBox.Show(this, $"aBookPlayer {latest} is available (you have {current}).\n\nOpen the download page?",
                    AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                UpdateCheck.OpenReleasesPage();
        }
        else if (interactive)
        {
            MessageBox.Show(this, latest != null
                    ? $"You have the latest version ({current})."
                    : "Could not check for updates (no internet connection, or the releases page is not public).\n\n" + UpdateCheck.ReleasesPage,
                AppName, MessageBoxButtons.OK, latest != null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
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
