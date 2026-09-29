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
        // From the very start (after Stop, or a finished book played again): skip the intro
        if (_player.Position < TimeSpan.FromSeconds(1) && IntroOf(CurrentBook) is var intro && intro > TimeSpan.Zero
            && intro < _player.Duration - OutroOf(CurrentBook))
            _player.Seek(intro);
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
        menu.Opening += (_, _) => copy.Enabled = _subView.SubtitleText != null || _subView.SecondText != null;
        _subView.ContextMenuStrip = menu;
    }

    void CopySubtitle()
    {
        // Both lines when a translation is shown under the subtitles
        var text = string.Join("\n", new[] { _subView.SubtitleText, _subView.SecondText }.Where(t => t != null));
        if (text.Length == 0) return;
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
        _cover.Image = CoverImages.ToImage(bytes);
        _coverHost.Visible = _cover.Image != null;
        old?.Dispose();
    }

    // ───────────────────────────── Updates ─────────────────────────────

    /// <summary>
    /// Automatic check at most once a day (silent unless there is a new version the user has not declined), or on
    /// request from the Help menu. Only the app's version is sent to GitHub (in the User-Agent).
    /// </summary>
    async Task CheckForUpdatesAsync(bool interactive)
    {
        if (!interactive && (!_settings.CheckForUpdates || DateTime.UtcNow - _settings.LastUpdateCheck < TimeSpan.FromDays(1))) return;
        ReleaseInfo? release = null;
        try { release = await UpdateCheck.LatestAsync(CancellationToken.None); }
        catch { /* offline */ }
        if (IsDisposed) return;

        var current = UpdateCheck.CurrentVersion;
        bool newer = release != null && release.Version > current;
        var installer = release != null ? UpdateCheck.InstallerFor(release, AppPaths.Install, AppPaths.SelfContained) : null;
        if (!interactive)
        {
            // Not while another window (a dialog) is in front: try again at the next start
            if (release == null || Application.OpenForms.Cast<Form>().Any(f => f.Modal)) return;
            // Just published: the installers are attached a few minutes later (by GitHub Actions); ask then. Still
            // missing hours later (the build failed), the download page is offered instead
            if (newer && installer == null && AppPaths.Install is InstallKind.AllUsers or InstallKind.CurrentUser
                && UpdateCheck.WaitForInstaller(release, DateTime.UtcNow)) return;
            _settings.LastUpdateCheck = DateTime.UtcNow;
            SaveSettings();
            if (!newer || release.Version.ToString() == _settings.SkippedVersion) return;
        }

        if (newer) OfferUpdate(release!, installer);
        else
        {
            MessageBox.Show(this, release != null
                    ? $"You have the latest version ({current})."
                    : "Could not check for updates (no internet connection?).\n\n" + UpdateCheck.ReleasesPage,
                AppName, MessageBoxButtons.OK, release != null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }

    /// <summary>What is new, and a one-click update: the installer is downloaded, run quietly, and reopens the app.</summary>
    async void OfferUpdate(ReleaseInfo release, ReleaseAsset? installer)
    {
        UpdateForm.Choice choice;
        string? path;
        using (var dlg = new UpdateForm(release, installer))
        {
            dlg.ShowDialog(this);
            (choice, path) = (dlg.Result, dlg.InstallerPath);
        }
        if (choice == UpdateForm.Choice.Skip)
        {
            _settings.SkippedVersion = release.Version.ToString(); // not offered again by the daily check
            SaveSettings();
        }
        else if (choice == UpdateForm.Choice.Install && path != null)
        {
            SaveSettings();
            try
            {
                // The installer closes the app when it installs and opens it again afterwards; while it waits for the
                // administrator prompt the app stays open (and responsive, so it can be closed)
                using var setup = UpdateCheck.StartInstaller(path, AppPaths.Install);
                ShowOsd("Installing the update…");
                await setup.WaitForExitAsync();
                if (IsDisposed) return;
                // Still here: nothing was installed (prompt declined, or the installer failed)
                MessageBox.Show(this, setup.ExitCode == 0
                        ? "The update was installed: restart aBookPlayer to use it."
                        : $"The update was not installed (the administrator prompt was declined, or the installer stopped; code {setup.ExitCode}).\n\n" +
                          $"You can run it yourself: \"{path}\".",
                    AppName, MessageBoxButtons.OK, setup.ExitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not start the installer:\n{ex.Message}\n\nIt was saved as \"{path}\".",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        OpenDeferredFile();
    }

    ToolStripMenuItem MakeAutoUpdateItem()
    {
        var item = new ToolStripMenuItem("Check for updates automatically") { Checked = _settings.CheckForUpdates };
        item.Click += (_, _) =>
        {
            _settings.CheckForUpdates = item.Checked = !item.Checked;
            SaveSettings();
        };
        return item;
    }

    // ───────────────────────────── Library ─────────────────────────────

    // Side panels: widths in 96-DPI pixels; the subtitles in between always keep MinCenterWidth
    const int MinPanelWidth = 220, MaxPanelWidth = 900, MinCenterWidth = 380;

    readonly LibraryPanel _library;
    readonly PanelSplitter _librarySplitter = new() { Dock = DockStyle.Left };
    readonly PanelSplitter _chaptersSplitter = new() { Dock = DockStyle.Right };
    Panel _chaptersPanel = null!;
    ToolStripMenuItem? _libraryItem;

    static int ClampPanelWidth(int width) => Math.Clamp(width, MinPanelWidth, MaxPanelWidth);

    int ToLogicalUnits(int pixels) => (int)Math.Round(pixels * 96.0 / DeviceDpi);

    /// <summary>The dividers can be dragged until a panel or the subtitles reach their minimum width.</summary>
    void ApplySplitterLimits()
    {
        foreach (var splitter in new[] { _librarySplitter, _chaptersSplitter })
        {
            splitter.MinSize = LogicalToDeviceUnits(MinPanelWidth);
            splitter.MinExtra = LogicalToDeviceUnits(MinCenterWidth);
        }
    }

    /// <summary>A divider was dragged: remember the panel's width for the next start.</summary>
    void OnPanelResized(object? sender, SplitterEventArgs e)
    {
        if (sender == _librarySplitter) _settings.LibraryPanelWidth = ClampPanelWidth(ToLogicalUnits(_library.Width));
        else _settings.ChaptersPanelWidth = ClampPanelWidth(ToLogicalUnits(_chaptersPanel.Width));
        SaveSettings();
    }

    /// <summary>
    /// The window can't get narrower than the panels shown at their minimum plus the subtitles' minimum (nor wider
    /// than the screen: see <see cref="FitToScreen"/>); in between, <see cref="FitPanels"/> narrows the panels.
    /// </summary>
    void UpdateMinimumSize()
    {
        int panel = LogicalToDeviceUnits(MinPanelWidth);
        int panels = panel + _chaptersSplitter.Width + (_settings.ShowLibrary ? panel + _librarySplitter.Width : 0);
        int width = LogicalToDeviceUnits(MinCenterWidth) + panels + (Width - ClientSize.Width);
        MinimumSize = new Size(Math.Min(width, Screen.FromControl(this).WorkingArea.Width), MinimumSize.Height);
    }

    /// <summary>
    /// Gives the panels their saved widths when the window has room for them, and narrows them (the wider one
    /// first) when it has not: a smaller screen, higher scaling, the library shown in a maximized window. The saved
    /// widths are kept, so the panels grow back when there is room again.
    /// </summary>
    void FitPanels()
    {
        if (!IsHandleCreated || _chaptersPanel == null || WindowState == FormWindowState.Minimized) return;
        int splitters = _chaptersSplitter.Width + (_settings.ShowLibrary ? _librarySplitter.Width : 0);
        var (library, chapters) = FitPanelWidths(
            LogicalToDeviceUnits(ClampPanelWidth(_settings.LibraryPanelWidth)), LogicalToDeviceUnits(ClampPanelWidth(_settings.ChaptersPanelWidth)),
            ClientSize.Width - splitters - LogicalToDeviceUnits(MinCenterWidth), LogicalToDeviceUnits(MinPanelWidth), _settings.ShowLibrary);
        if (_library.Width != library) _library.Width = library;
        if (_chaptersPanel.Width != chapters) _chaptersPanel.Width = chapters;
    }

    /// <summary>
    /// Panel widths that fit in <paramref name="room"/> pixels: unchanged if they fit, else the wider panel gives up
    /// room first and then both shrink together, never below <paramref name="min"/>. A hidden library keeps its width.
    /// </summary>
    internal static (int Library, int Chapters) FitPanelWidths(int library, int chapters, int room, int min, bool showLibrary)
    {
        if (!showLibrary) return (library, Math.Max(min, Math.Min(chapters, room)));
        if (library + chapters <= room) return (library, chapters);
        int total = Math.Max(room, 2 * min);
        int narrower = Math.Min(library, chapters);
        int cap = narrower * 2 >= total ? total / 2 : total - narrower;
        return (Math.Max(min, Math.Min(library, cap)), Math.Max(min, Math.Min(chapters, cap)));
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        FitPanels();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        // The panels were rescaled with the window; their limits are in pixels
        ApplySplitterLimits();
        BeginInvoke(() =>
        {
            UpdateMinimumSize();
            FitPanels();
        });
    }

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
        // Shown: the window grows by the library's own width (not one narrowed to fit); hidden: it shrinks by the
        // room the library really took
        int delta = (show ? LogicalToDeviceUnits(ClampPanelWidth(_settings.LibraryPanelWidth)) : _library.Width) + _librarySplitter.Width;
        SuspendLayout();
        if (!show)
        {
            _library.Visible = _librarySplitter.Visible = false;
            UpdateMinimumSize();
        }
        if (WindowState == FormWindowState.Normal)
        {
            var area = Screen.FromControl(this).WorkingArea;
            int width = Math.Min(area.Width, Width + (show ? delta : -delta));
            // Growing past the screen's right edge: move the window left instead
            Bounds = new Rectangle(Math.Max(area.Left, Math.Min(Left, area.Right - width)), Top, width, Height);
        }
        if (show)
        {
            _library.Visible = _librarySplitter.Visible = true;
            UpdateMinimumSize(); // limited to the screen: a larger minimum would push the window past its edge
        }
        FitToScreen();
        FitPanels(); // a maximized window does not grow: the panels may have to narrow
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

    /// <summary>
    /// The open book was marked finished in the library: stop listening and go back to the start, so the save
    /// keeps position 0 (not the player's position) and other PCs get "finished" with it. Unmarked: just save.
    /// </summary>
    void MarkOpenBookFinished(bool finished)
    {
        if (finished && _player.IsLoaded)
        {
            if (_player.IsPlaying) _player.Pause();
            _player.Seek(TimeSpan.Zero);
            _keepSavedPosition = false;
            _pausedSince = null; // no smart rewind from 0:00
            CancelSleepAtChapterEnd();
        }
        if (CurrentBook is { } book) book.Finished = finished; // RememberCurrentBook keeps the flag
        SaveSettings();
        UpdateUi();
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
