using System.Runtime.InteropServices;
using Microsoft.Win32;
using NAudio.Wave;

namespace aBookPlayer;

public sealed partial class MainForm : Form
{
    const string AppName = AppSettings.AppName;
    static readonly TimeSpan SkipStep = TimeSpan.FromSeconds(10);
    static readonly TimeSpan LongSkipStep = TimeSpan.FromSeconds(60);
    static readonly TimeSpan OffsetStep = TimeSpan.FromMilliseconds(100);

    readonly AudioPlayer _player = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 40 };
    readonly ToolTip _tip = DarkToolTip.Create();
    readonly string? _startupFile;
    readonly AppSettings _settings = AppSettings.Load();

    List<Chapter> _chapters = [];
    SubtitleTrack? _subs;
    string? _audioPath, _srtPath;
    TimeSpan _subOffset;
    int _currentChapter = -1;
    float _volumeBeforeMute = 0.8f;
    DateTime _osdUntil;
    DateTime _lastSave = DateTime.Now;
    int _loadGeneration;
    string? _deferredOpen;   // file forwarded by another instance while a dialog was open

    // Sleep timer: either a wall-clock deadline or "at the end of chapter _sleepChapter"
    DateTime? _sleepAt;
    int _sleepMinutes;
    bool _sleepAtChapterEnd;
    int _sleepChapter = -1;
    const double SleepFadeSeconds = 10;

    // After Stop the player sits at 0:00, but the book's saved position is kept until playback starts again
    bool _keepSavedPosition;
    bool _chapterScrollPending;   // the current chapter changed while the list had no height (minimized)

    readonly MenuStrip _menu = MenuFonts.Track(new MenuStrip());
    readonly Label _lblTitle = new()
    {
        Dock = DockStyle.Top, Height = 48, Padding = new Padding(22, 16, 22, 0),
        Font = new Font("Segoe UI Semibold", 15f), UseMnemonic = false, AutoEllipsis = true,
    };
    readonly Label _lblChapter = new()
    {
        Dock = DockStyle.Top, Height = 30, Padding = new Padding(23, 4, 22, 0),
        Font = new Font("Segoe UI", 10.5f), ForeColor = Theme.TextDim, UseMnemonic = false, AutoEllipsis = true,
    };
    readonly SubtitleView _subView = new() { Dock = DockStyle.Fill };
    readonly Label _lblOsd = new()
    {
        AutoSize = true, Visible = false, BackColor = Theme.Surface, ForeColor = Theme.Text,
        Padding = new Padding(10, 6, 10, 6), UseMnemonic = false,
    };
    readonly Label _lblChaptersHeader = new()
    {
        Dock = DockStyle.Top, Height = 42, Padding = new Padding(14, 16, 0, 0), Text = "CHAPTERS",
        Font = new Font("Segoe UI Semibold", 9f), ForeColor = Theme.TextDim,
    };
    readonly ListBox _lstChapters = new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Panel, ForeColor = Theme.Text,
        DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 34, IntegralHeight = false,
    };
    readonly SeekBar _seek = new() { Dock = DockStyle.Top, Height = 30 };
    readonly SeekBar _volume = new() { Width = 110, Height = 30, Maximum = 1, LiveUpdate = true, Margin = new Padding(0, 5, 0, 5) };
    readonly Label _lblTime = new() { AutoSize = true, ForeColor = Theme.TextDim, Font = new Font("Segoe UI", 10f), Text = "00:00 / 00:00" };

    readonly IconButton _btnStop = new(Glyphs.Stop);
    readonly IconButton _btnPrev = new(Glyphs.Previous);
    readonly IconButton _btnBack = new(Glyphs.Rewind);
    readonly IconButton _btnPlay = new(Glyphs.Play, 20f, 56, 48);
    readonly IconButton _btnFwd = new(Glyphs.FastForward);
    readonly IconButton _btnNext = new(Glyphs.Next);
    readonly IconButton _btnMute = new(Glyphs.Volume, 13f, 40, 40);
    readonly Button _btnSpeed = new()
    {
        Text = "1×", FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel, ForeColor = Theme.Text,
        Font = new Font("Segoe UI Semibold", 10f), Size = new Size(58, 34), Margin = new Padding(0, 3, 10, 3),
        TabStop = false, UseMnemonic = false, Cursor = Cursors.Hand,
    };
    readonly ContextMenuStrip _speedMenu = MenuFonts.Track(new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true });
    readonly List<(double Speed, ToolStripMenuItem Item)> _speedItems = [];

    static readonly double[] SpeedPresets = [0.5, 0.75, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0];

    public MainForm(string? startupFile = null)
    {
        _startupFile = startupFile;
        _library = new LibraryPanel(_settings)
        {
            Dock = DockStyle.Left, Width = ClampPanelWidth(_settings.LibraryPanelWidth), Visible = _settings.ShowLibrary,
        };
        _librarySplitter.Visible = _settings.ShowLibrary;
        SuspendLayout();
        Text = AppName;
        Icon = Theme.AppIcon;
        ClientSize = new Size(_settings.ShowLibrary ? 1300 : 1040, 640);
        MinimumSize = new Size(640, 480); // the real minimum depends on the panels: see UpdateMinimumSize (OnLoad)
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.75f);
        AllowDrop = true;

        BuildMenu();
        BuildLayout();
        WireEvents();
        SetUpSubtitleArea();
        EnableFileDrop(this);
        EnableWindowDrag(this);

        // Same as designer-generated code: sizes are 96-DPI pixels and get scaled here
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);
        PerformLayout();

        _player.Volume = _settings.Volume;
        _player.Speed = _settings.PlaybackSpeed;
        _player.VoiceBoost = _settings.VoiceBoost;
        _player.SkipSilences = _settings.SkipSilences;
        UpdateSpeedUi();
        _volume.Value = _player.Volume;
        _subView.SubtitleStyle = _settings.Subtitles;
        _timer.Tick += (_, _) => UpdateUi();
        _timer.Start();
        UpdateUi();
    }

    // ───────────────────────────── UI construction ─────────────────────────────

    void BuildMenu()
    {
        _menu.Renderer = new DarkMenuRenderer();
        _menu.BackColor = Theme.Panel;
        _menu.ForeColor = Theme.Text;
        _menu.Padding = new Padding(6, 3, 0, 3);

        _menu.Items.AddRange(
        [
            MakeMenu("&File",
                MakeItem("Open audio file…", "Ctrl+O", OpenAudioDialog),
                MakeItem("Open folder as a book…", "Ctrl+Shift+O", OpenFolderDialog),
                MakeLibraryItem(),
                MakeRecentMenu(),
                new ToolStripSeparator(),
                MakeItem("Load SRT subtitles…", "Ctrl+T", OpenSrtDialog),
                MakeItem("Remove subtitles", null, RemoveSubtitles),
                MakeItem("Load second subtitles (a translation)…", null, OpenSecondSrtDialog),
                MakeItem("Remove second subtitles", null, RemoveSecondSubtitles),
                new ToolStripSeparator(),
                MakeItem("Sync between PCs…", null, ShowSyncOptions),
                MakeItem("Share with your phone…", null, ShowShareOptions),
                MakeItem("Options…", "Ctrl+P", ShowOptions),
                new ToolStripSeparator(),
                MakeItem("Exit", "Alt+F4", Close)),
            MakeMenu("&Playback",
                MakeItem("Play / Pause", "Space", TogglePlay),
                MakeItem("Stop", "S", Stop),
                new ToolStripSeparator(),
                MakeItem("Back 10 s", "←", () => SkipBy(-SkipStep)),
                MakeItem("Forward 10 s", "→", () => SkipBy(SkipStep)),
                MakeItem("Back 1 min", "Ctrl+←", () => SkipBy(-LongSkipStep)),
                MakeItem("Forward 1 min", "Ctrl+→", () => SkipBy(LongSkipStep)),
                new ToolStripSeparator(),
                MakeItem("Previous chapter", "PgUp", PreviousChapter),
                MakeItem("Next chapter", "PgDn", NextChapter),
                new ToolStripSeparator(),
                MakeItem("Volume up", "↑", () => ChangeVolume(0.05f)),
                MakeItem("Volume down", "↓", () => ChangeVolume(-0.05f)),
                MakeItem("Mute", "M", ToggleMute),
                new ToolStripSeparator(),
                MakeSpeedMenu(),
                MakeSleepMenu(),
                MakeItem("Skip intro and ending…", null, ShowSkipIntroOutro),
                new ToolStripSeparator(),
                MakeVoiceBoostItem(),
                MakeSkipSilencesItem(),
                MakeSmartRewindItem(),
                MakeKeepScreenOnItem()),
            MakeMenu("&Bookmarks",
                MakeItem("Add bookmark…", "B", AddBookmark),
                MakeItem("Show bookmarks…", "Ctrl+B", ShowBookmarks)),
            MakeMenu("&Subtitles",
                MakeItem("Search in subtitles…", "Ctrl+F", ShowSearch),
                new ToolStripSeparator(),
                MakeItem("Repeat this sentence", "R", RepeatSentence),
                MakeItem("Previous sentence", "Shift+←", PreviousSentence),
                MakeItem("Next sentence", "Shift+→", NextSentence),
                MakeItem("Loop this sentence", "L", ToggleSentenceLoop),
                new ToolStripSeparator(),
                MakeItem("Show 100 ms earlier", "G", () => ChangeOffset(-OffsetStep)),
                MakeItem("Show 100 ms later", "H", () => ChangeOffset(OffsetStep)),
                MakeItem("Reset sync", "J", () => ChangeOffset(-_subOffset)),
                new ToolStripSeparator(),
                MakeItem("Transcribe with Whisper…", "Ctrl+R", ShowTranscribe),
                new ToolStripSeparator(),
                MakeItem("Subtitle appearance…", "Ctrl+P", ShowOptions)),
            MakeMenu("&View",
                MakeItem("Mini player", "Ctrl+M", ToggleMiniPlayer),
                MakeTrayItem(),
                new ToolStripSeparator(),
                MakeItem("Listening statistics…", null, ShowStatistics)),
            MakeMenu("&Help",
                MakeItem("Keyboard shortcuts", "F1", ShowShortcuts),
                new ToolStripSeparator(),
                MakeItem("Check for updates…", null, () => _ = CheckForUpdatesAsync(interactive: true)),
                MakeAutoUpdateItem()),
        ]);
        MainMenuStrip = _menu;
    }

    /// <summary>While playing, the PC never goes to standby; this option also keeps the screen on (no screensaver).</summary>
    ToolStripMenuItem MakeKeepScreenOnItem()
    {
        var item = new ToolStripMenuItem("Keep screen on while playing") { Checked = _settings.KeepScreenOn };
        item.Click += (_, _) =>
        {
            _settings.KeepScreenOn = item.Checked = !item.Checked;
            SaveSettings();
            UpdateUi();
        };
        return item;
    }

    /// <summary>"Speed" submenu for the main menu; the same presets also fill the speed button's pop-up menu.</summary>
    ToolStripMenuItem MakeSpeedMenu()
    {
        var submenu = new ToolStripMenuItem("Speed");
        foreach (var speed in SpeedPresets)
        {
            submenu.DropDownItems.Add(MakeSpeedItem(speed));
            _speedMenu.Items.Add(MakeSpeedItem(speed));
        }
        submenu.DropDownItems.Add(new ToolStripSeparator());
        submenu.DropDownItems.Add(MakeItem("Slower", "−", () => StepSpeed(-1)));
        submenu.DropDownItems.Add(MakeItem("Faster", "+", () => StepSpeed(+1)));
        return submenu;

        ToolStripMenuItem MakeSpeedItem(double speed)
        {
            var item = new ToolStripMenuItem(speed == 1.0 ? "1× (normal)" : FormatSpeed(speed));
            item.Click += (_, _) => SetSpeed(speed);
            _speedItems.Add((speed, item));
            return item;
        }
    }

    /// <summary>"Recent books": rebuilt each time it opens, from the per-book history (missing files are skipped).</summary>
    ToolStripMenuItem MakeRecentMenu()
    {
        var menu = new ToolStripMenuItem("Recent books");
        menu.DropDownItems.Add(new ToolStripMenuItem("(empty)") { Enabled = false }); // lets the arrow show before first opening
        menu.DropDownOpening += (_, _) =>
        {
            menu.DropDownItems.Clear();
            int n = 0;
            // No File.Exists here: on an offline network drive each check can block for seconds (see OpenRecentAsync)
            foreach (var path in _settings.RecentBooks(10))
            {
                var book = _settings.GetBook(path);
                var position = book != null && book.PositionSeconds > 1 ? $"  ({FormatTime(TimeSpan.FromSeconds(book.PositionSeconds))})" : "";
                var name = book?.Title ?? BookSource.NameFromPath(path);
                var item = new ToolStripMenuItem($"&{(++n) % 10}  {name.Replace("&", "&&")}{position}") { ToolTipText = path };
                item.Click += async (_, _) => await OpenRecentAsync(path);
                menu.DropDownItems.Add(item);
            }
            if (n == 0) menu.DropDownItems.Add(new ToolStripMenuItem("(empty)") { Enabled = false });
            else
            {
                menu.DropDownItems.Add(new ToolStripSeparator());
                menu.DropDownItems.Add(MakeItem("Clear list", null, () =>
                {
                    if (MessageBox.Show(this, "Forget all the other books, with their positions and bookmarks?\n\n(To remove single books, right-click them in the library.)",
                            AppName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK)
                        return;
                    // Keep the current book so its position is not lost
                    var current = _audioPath != null ? _settings.GetBook(_audioPath) : null;
                    _settings.Books.Clear();
                    if (current != null) _settings.Books[_audioPath!] = current;
                    SaveSettings();
                }));
            }
        };
        return menu;
    }

    async Task OpenRecentAsync(string path)
    {
        UseWaitCursor = true;
        bool exists = await Task.Run(() => BookSource.Exists(path)); // may be slow on an unreachable drive
        UseWaitCursor = false;
        if (exists)
        {
            await OpenPathAsync(path, atStartup: false);
            return;
        }
        if (MessageBox.Show(this, $"\"{path}\" cannot be found (moved, deleted, or its drive is not connected).\n\nRemove it from the recent books?",
                AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            if (SamePath(path, _audioPath))
            {
                // It is the open book: close it, or saving would put it straight back in the list
                _player.Unload();
                ClearLoadedFile();
                _sleepAt = null;
                CancelSleepAtChapterEnd();
                _player.Fade = 1;
                UpdateUi();
            }
            _settings.Books.Remove(path);
            SaveSettings();
        }
    }

    static readonly int[] SleepMinutes = [15, 30, 45, 60, 90];

    /// <summary>"Sleep timer": pause after N minutes or at the end of the current chapter, fading out the last seconds.</summary>
    ToolStripMenuItem MakeSleepMenu()
    {
        var menu = new ToolStripMenuItem("Sleep timer");
        var off = MakeItem("Off", null, () => SetSleepTimer(0));
        menu.DropDownItems.Add(off);
        var timed = SleepMinutes.Select(m => (m, item: MakeItem($"{m} minutes", null, () => SetSleepTimer(m)))).ToList();
        foreach (var (_, item) in timed) menu.DropDownItems.Add(item);
        var chapterEnd = MakeItem("End of chapter", null, SetSleepAtChapterEnd);
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(chapterEnd);
        menu.DropDownOpening += (_, _) =>
        {
            off.Checked = _sleepAt == null && !_sleepAtChapterEnd;
            foreach (var (m, item) in timed) item.Checked = _sleepMinutes == m && _sleepAt != null;
            chapterEnd.Checked = _sleepAtChapterEnd;
            chapterEnd.Enabled = _chapters.Count > 0;
        };
        return menu;
    }

    static ToolStripMenuItem MakeMenu(string text, params ToolStripItem[] items)
    {
        var menu = new ToolStripMenuItem(text);
        menu.DropDownItems.AddRange(items);
        return menu;
    }

    static ToolStripMenuItem MakeItem(string text, string? keys, Action action)
    {
        // Shortcuts are handled in ProcessCmdKey; here they are only a visual reminder
        var item = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys, ShowShortcutKeys = keys != null };
        item.Click += (_, _) => action();
        return item;
    }

    void BuildLayout()
    {
        // Center area: title, current chapter, subtitles
        var center = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Back };
        // Header: cover (when the book has one) beside the title and the current chapter
        var titles = new Panel { Dock = DockStyle.Fill };
        titles.Controls.Add(_lblChapter);
        titles.Controls.Add(_lblTitle);
        var header = new Panel { Dock = DockStyle.Top, Height = 82 };
        header.Controls.Add(titles);
        _coverHost.Controls.Add(_cover);
        header.Controls.Add(_coverHost);
        center.Controls.Add(_subView);
        center.Controls.Add(header);
        center.Controls.Add(_lblOsd);
        _lblOsd.BringToFront();
        center.Resize += (_, _) => PositionOsd();

        // Chapter list on the right
        var right = _chaptersPanel = new Panel { Dock = DockStyle.Right, Width = ClampPanelWidth(_settings.ChaptersPanelWidth), BackColor = Theme.Panel };
        right.Controls.Add(_lstChapters);
        right.Controls.Add(_lblChaptersHeader);
        _librarySplitter.SplitterMoved += OnPanelResized;
        _chaptersSplitter.SplitterMoved += OnPanelResized;

        // Transport bar at the bottom
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        buttons.Controls.AddRange([_btnStop, _btnPrev, _btnBack, _btnPlay, _btnFwd, _btnNext]);
        foreach (Control b in buttons.Controls)
            if (b != _btnPlay) b.Margin = new Padding(3, 4, 3, 4);

        var volumeBox = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        volumeBox.Controls.AddRange([_btnSpeed, _btnMute, _volume]);
        _btnSpeed.FlatAppearance.BorderColor = Theme.Border;
        _btnSpeed.FlatAppearance.MouseOverBackColor = Theme.Hover;

        var controls = new Panel { Dock = DockStyle.Fill };
        controls.Controls.AddRange([buttons, _lblTime, volumeBox]);
        controls.Layout += (_, _) =>
        {
            var size = controls.ClientSize;
            buttons.Location = new Point((size.Width - buttons.Width) / 2, (size.Height - buttons.Height) / 2);
            _lblTime.Location = new Point(LogicalToDeviceUnits(4), (size.Height - _lblTime.Height) / 2);
            volumeBox.Location = new Point(size.Width - volumeBox.Width, (size.Height - volumeBox.Height) / 2);
        };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 104, BackColor = Theme.Panel, Padding = new Padding(12, 8, 12, 6) };
        bottom.Controls.Add(controls);
        bottom.Controls.Add(_seek);

        // Add order = reverse docking order (the menu docks first, at the very top); the library is at the left.
        // Each divider docks right after its panel, which is what it resizes when dragged
        Controls.Add(center);
        Controls.Add(_chaptersSplitter);
        Controls.Add(right);
        Controls.Add(_librarySplitter);
        Controls.Add(_library);
        Controls.Add(bottom);
        Controls.Add(_menu);
    }

    void WireEvents()
    {
        _btnPlay.Click += (_, _) => TogglePlay();
        _btnStop.Click += (_, _) => Stop();
        _btnBack.Click += (_, _) => SkipBy(-SkipStep);
        _btnFwd.Click += (_, _) => SkipBy(SkipStep);
        _btnPrev.Click += (_, _) => PreviousChapter();
        _btnNext.Click += (_, _) => NextChapter();
        _btnMute.Click += (_, _) => ToggleMute();
        _btnSpeed.Click += (_, _) => _speedMenu.Show(_btnSpeed, Point.Empty, ToolStripDropDownDirection.AboveRight);

        _tip.SetToolTip(_btnPlay, "Play / Pause (Space)");
        _tip.SetToolTip(_btnStop, "Stop (S)");
        _tip.SetToolTip(_btnBack, "Back 10 s (←)");
        _tip.SetToolTip(_btnFwd, "Forward 10 s (→)");
        _tip.SetToolTip(_btnPrev, "Previous chapter (PgUp)");
        _tip.SetToolTip(_btnNext, "Next chapter (PgDn)");
        _tip.SetToolTip(_btnMute, "Mute (M)");
        _tip.SetToolTip(_btnSpeed, "Playback speed (− / +)");

        _seek.ValueCommitted += (_, v) => SeekTo(TimeSpan.FromSeconds(v));
        _seek.HoverText = v =>
        {
            var t = TimeSpan.FromSeconds(v);
            int i = ChapterIndexAt(t);
            return i >= 0 ? $"{FormatTime(t)}  ·  {_chapters[i].Title}" : FormatTime(t);
        };
        _volume.ValueCommitted += (_, v) => SetVolume((float)v);
        _volume.HoverText = v => $"Volume {v:P0}";

        _lstChapters.DrawItem += DrawChapterItem;
        _lstChapters.DoubleClick += (_, _) => PlayChapter(_lstChapters.SelectedIndex);
        _lstChapters.Resize += (_, _) =>
        {
            // The rows are laid out on the width (the times at the right): the list itself only repaints the strip
            // uncovered by a resize, which left the old times behind while the divider was dragged
            _lstChapters.Invalidate();
            if (_chapterScrollPending && _currentChapter >= 0) ShowCurrentChapter(_currentChapter);
        };
        _lstChapters.HandleCreated += (_, _) => Theme.UseDarkScrollBars(_lstChapters);
        // Owner-drawn rows don't rescale by themselves when the window moves to a monitor with another DPI
        _lstChapters.DpiChangedAfterParent += (_, _) => _lstChapters.ItemHeight = _lstChapters.LogicalToDeviceUnits(34);

        _library.BookChosen += async path =>
        {
            if (!SamePath(path, _audioPath)) await LoadAudioAsync(path);
        };
        _library.Changed += SaveSettings;
        _library.OpenBookFinished += MarkOpenBookFinished;
        _library.DetailsEdited += OnDetailsEdited;
        var focusFilter = new LibraryFocusFilter(this);
        Application.AddMessageFilter(focusFilter);
        FormClosed += (_, _) => Application.RemoveMessageFilter(focusFilter);

        _player.Ended += (_, _) => OnBookEnded();
        _player.Error += (_, ex) =>
        {
            SaveSettings(); // the player kept the position: store it before anything else happens
            MarkPaused();
            UpdateUi();
            var cause = ex switch
            {
                NAudio.MmException => "an audio device error",
                IOException => "a problem reading the file",
                _ => "an error",
            };
            MessageBox.Show(this, $"Playback stopped because of {cause}:\n{ex.Message}\n\nPress Play to continue from where you were.",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
    }

    // ───────────────────────────── Drag & drop ─────────────────────────────

    /// <summary>
    /// Drops are delivered to the window (HWND) under the cursor, so every control must accept them,
    /// not just the form. The menu is skipped so it does not interfere with item reordering.
    /// </summary>
    /// <summary>
    /// Lets the window be moved by dragging any non-interactive part of it (background, title, subtitles,
    /// panels), as if it were the title bar. Buttons, seek/volume bars, the chapter list and the menu keep
    /// their own mouse behavior.
    /// </summary>
    void EnableWindowDrag(Control control)
    {
        if (control is ButtonBase or SeekBar or ListBox or ToolStrip or TextBoxBase or ComboBox or Splitter) return;
        if (control is SubtitleView subtitles)
        {
            // A click there plays/pauses: the window moves only once the mouse is dragged
            subtitles.DragStarted += (_, _) => StartWindowDrag();
            return;
        }
        control.MouseDown += OnWindowDragMouseDown;
        foreach (Control child in control.Controls) EnableWindowDrag(child);
    }

    void OnWindowDragMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || e.Clicks != 1) return;
        StartWindowDrag();
    }

    /// <summary>
    /// Hands the drag to Windows as a title-bar drag: moving, snapping and restoring a maximized window all
    /// behave like the real title bar.
    /// </summary>
    void StartWindowDrag()
    {
        NativeDrag.ReleaseCapture();
        NativeDrag.SendMessage(Handle, NativeDrag.WM_NCLBUTTONDOWN, NativeDrag.HTCAPTION, 0);
    }

    static class NativeDrag
    {
        public const int WM_NCLBUTTONDOWN = 0x00A1, HTCAPTION = 2;
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    }

    void EnableFileDrop(Control control)
    {
        if (control is ToolStrip) return;
        control.AllowDrop = true;
        control.DragEnter += OnFileDragEnter;
        control.DragDrop += OnFileDragDrop;
        foreach (Control child in control.Controls) EnableFileDrop(child);
    }

    static string[] SupportedFiles(IDataObject? data) =>
        data?.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => AudioFormats.IsSupported(f) || f.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) || Directory.Exists(f)).ToArray()
            : [];

    void OnFileDragEnter(object? sender, DragEventArgs e) =>
        e.Effect = SupportedFiles(e.Data).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;

    void OnFileDragDrop(object? sender, DragEventArgs e)
    {
        var files = SupportedFiles(e.Data);
        if (files.Length == 0) return;
        Activate();
        // Process after the OLE operation completes, so Explorer is not blocked while loading
        BeginInvoke(async () =>
        {
            // A dropped folder is a book made of its audio files
            var audio = files.FirstOrDefault(AudioFormats.IsSupported) ?? files.FirstOrDefault(Directory.Exists);
            var srt = files.FirstOrDefault(f => f.EndsWith(".srt", StringComparison.OrdinalIgnoreCase));
            if (audio != null) await LoadAudioAsync(audio);
            if (srt != null) LoadSrt(srt);
        });
    }

    // ───────────────────────────── Startup / shutdown ─────────────────────────────

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.UseDarkTitleBar(Handle);
        // The menus' text at the size of the monitor the window is on
        MenuFonts.Apply(DeviceDpi);
        // Only once the handle exists can the handler marshal to the UI thread (Invoke); -= first
        // because the handle can be recreated
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Restore window position and size (only if it is still visible on a screen)
        if (_settings.WindowBounds is [var x, var y, var w, var h] && w > 200 && h > 200)
        {
            var bounds = new Rectangle(x, y, w, h);
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = bounds;
            }
        }
        ApplySplitterLimits();
        UpdateMinimumSize();
        FitToScreen();
        FitPanels();
        if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;
    }

    /// <summary>
    /// With the library shown, the minimum (and default) width can be more than a small screen offers (a
    /// 1366×768 laptop at 125 %): never let the window be wider or taller than the screen's working area.
    /// </summary>
    void FitToScreen()
    {
        var area = Screen.FromRectangle(Bounds).WorkingArea;
        MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
        // A maximized window is a little larger than the area on purpose (its borders are off screen)
        if (WindowState != FormWindowState.Normal) return;
        int width = Math.Min(Width, area.Width), height = Math.Min(Height, area.Height);
        // Too big, or grown past the edge (a larger minimum applied to a window near the right edge): back inside
        var inside = new Rectangle(Math.Clamp(Left, area.Left, area.Right - width), Math.Clamp(Top, area.Top, area.Bottom - height), width, height);
        if (inside != Bounds) Bounds = inside;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _lstChapters.ItemHeight = _lstChapters.LogicalToDeviceUnits(34);
        // The library's search box may have got the focus: Space must play, not type
        if (_library.ContainsFocus) ActiveControl = null;
        SetUpMediaControls();
        UpdateTrayIcon();

        if (_startupFile != null && BookSource.Exists(_startupFile))
            await OpenPathAsync(_startupFile, atStartup: true);
        else
            await RestoreLastSessionAsync(autoPlay: false);
        if (StartSharing() is { } shareError) ShowOsd(shareError);
        _ = CheckForUpdatesAsync(interactive: false);
        _ = Task.Run(UpdateCheck.CleanUpDownloads); // the installer of the last update has done its job
    }

    /// <summary>A file opened from Explorer while the app was already running (see <see cref="SingleInstance"/>).</summary>
    public void OpenFromOtherInstance(string path)
    {
        if (!Visible) RestoreMainWindow(); // hidden in the notification area or behind the mini player
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        var modal = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Modal);
        if (modal != null)
        {
            // Don't swap the book under an open dialog (e.g. a running transcription): open it afterwards
            modal.Activate();
            if (path.Length > 0) _deferredOpen = path;
            return;
        }
        Activate();
        if (path.Length > 0 && BookSource.Exists(path)) _ = OpenPathAsync(path, atStartup: false);
    }

    void OpenDeferredFile()
    {
        var path = _deferredOpen;
        _deferredOpen = null;
        if (path != null && BookSource.Exists(path)) _ = OpenPathAsync(path, atStartup: false);
    }

    /// <summary>Opens an audio or .srt file passed from outside (command line, "Open with", another instance).</summary>
    async Task OpenPathAsync(string path, bool atStartup)
    {
        if (!path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
        {
            // Every book resumes from its saved position; if it is already playing, just keep going
            if (!atStartup && _player.IsLoaded && SamePath(path, _audioPath)) return;
            await LoadAudioAsync(path);
            return;
        }

        // Subtitles need their audio: prefer the book with the same name in the same folder
        var audio = FindAudioFor(path);
        if (audio != null && !SamePath(audio, _audioPath))
        {
            if (!await LoadAudioAsync(audio)) return;
        }
        else if (!_player.IsLoaded)
        {
            await RestoreLastSessionAsync(autoPlay: false);
        }
        LoadSrt(path); // an explicitly chosen .srt wins over the one loaded automatically
    }

    static string? FindAudioFor(string srtPath)
    {
        var folder = Path.GetDirectoryName(srtPath);
        if (folder == null) return null;
        var baseName = Path.GetFileNameWithoutExtension(srtPath);
        var file = AudioFormats.Extensions
            .Select(ext => Path.Combine(folder, baseName + ext))
            .FirstOrDefault(File.Exists);
        // A folder book keeps its subtitles inside the folder, named after it (see BookSource.SubtitlePath)
        return file ?? (string.Equals(BookSource.DisplayName(folder), baseName, StringComparison.OrdinalIgnoreCase) ? folder : null);
    }

    /// <summary>Reopens the last book (paused) at the position where it was left.</summary>
    async Task RestoreLastSessionAsync(bool autoPlay)
    {
        if (_settings.LastFile != null && BookSource.Exists(_settings.LastFile))
            await LoadAudioAsync(_settings.LastFile, autoPlay);
    }

    /// <summary>Saves the current book's position, subtitles and sync into the per-book history.</summary>
    void RememberCurrentBook()
    {
        if (_audioPath == null || !_player.IsLoaded) return;
        double position = _keepSavedPosition
            ? _settings.GetBook(_audioPath)?.PositionSeconds ?? 0   // stopped: don't overwrite the place in the book with 0:00
            : _player.Position.TotalSeconds;
        var book = _settings.RememberBook(_audioPath, position, _srtPath, _subOffset.TotalMilliseconds);
        RememberSecondSubtitles(book);
    }

    /// <summary>Restores a book's saved subtitles, sync and position right after it has been loaded.</summary>
    void RestoreBookState(string path, bool autoPlay)
    {
        var book = _settings.GetBook(path);
        if (book?.SubtitleFile != null && !SamePath(book.SubtitleFile, _srtPath) && File.Exists(book.SubtitleFile))
            LoadSrt(book.SubtitleFile, quiet: true);
        if (book != null && _srtPath != null && SamePath(_srtPath, book.SubtitleFile))
            _subOffset = TimeSpan.FromMilliseconds(book.SubtitleOffsetMs);

        // Near the end (or in the ending skipped) counts as finished: start again from the beginning, past the intro
        var pos = TimeSpan.FromSeconds(book?.PositionSeconds ?? 0);
        var end = _player.Duration - Max(TimeSpan.FromSeconds(5), OutroOf(book));
        if (pos > TimeSpan.FromSeconds(1) && pos < end)
        {
            _player.Seek(pos);
            ShowOsd(autoPlay ? $"Resuming from {FormatTime(pos)}" : $"Resuming from {FormatTime(pos)} — press Space to play");
        }
        else if (IntroOf(book) is var intro && intro > TimeSpan.Zero && intro < end)
        {
            _player.Seek(intro);
        }
        RestoreSecondSubtitles(path, book);
        RememberCurrentBook(); // mark as most recently used
    }

    static bool SamePath(string? a, string? b) =>
        a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    void SaveSettings()
    {
        _lastSave = DateTime.Now;
        RememberCurrentBook();
        _settings.LastFile = _audioPath;
        _settings.Volume = _player.Volume > 0 ? _player.Volume : _volumeBeforeMute;
        _settings.PlaybackSpeed = _player.Speed;
        _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.WindowBounds = [b.X, b.Y, b.Width, b.Height];
        _settings.Save();
        PublishSyncedPositions();
        _library.SyncWithSettings(_audioPath); // progress of the open book, books opened or forgotten
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveSettings();
        // Let the final position reach the shared folder (a write still running would have skipped it)
        _lastPublish.Wait(TimeSpan.FromSeconds(3));
        PublishSyncedPositions();
        _lastPublish.Wait(TimeSpan.FromSeconds(3));
        base.OnFormClosing(e);
    }

    /// <summary>
    /// Before standby/hibernation: pause and save, so the place in the book survives even if the audio
    /// device, the app or the PC does not come back cleanly. Runs synchronously, before the system sleeps.
    /// </summary>
    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Suspend || IsDisposed) return;
        if (InvokeRequired) { Invoke(() => OnPowerModeChanged(sender, e)); return; }
        if (_player.IsPlaying) MarkPaused();
        _player.Pause();
        SaveSettings();
        _lastPublish.Wait(TimeSpan.FromSeconds(2)); // the synced position too, before the network goes away
        UpdateUi();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged; // static event: would keep the form alive
        _timer.Stop();
        KeepAwake.Set(playing: false, keepScreenOn: false);
        _taskbarButtons?.Dispose();
        _tray?.Dispose();   // or the icon lingers in the notification area until the mouse passes over it
        _mini?.Dispose();
        _server?.Dispose();
        _player.Dispose();
        base.OnFormClosed(e);
    }

    void ShowTranscribe()
    {
        if (_audioPath == null)
        {
            MessageBox.Show(this, "Open an audio file to transcribe first.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var transcribedAudio = _audioPath;
        using (var dlg = new TranscribeForm(transcribedAudio, _chapters, _settings))
        {
            var result = dlg.ShowDialog(this);
            SaveSettings();
            // Only attach the subtitles if the same book is still loaded
            if (result == DialogResult.OK && dlg.SrtPath != null && SamePath(transcribedAudio, _audioPath))
            {
                // A translation goes under the book's own subtitles (it is theirs when there are none)
                if (dlg.Translated && _subs != null) LoadSecondSrt(dlg.SrtPath);
                else LoadSrt(dlg.SrtPath);
            }
        }
        OpenDeferredFile();
    }

    void ShowOptions()
    {
        using (var dlg = new OptionsForm(_settings.Subtitles))
        {
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _settings.Subtitles = dlg.Result;
                _subView.SubtitleStyle = dlg.Result;
                SaveSettings();
            }
        }
        OpenDeferredFile();
    }

    // ───────────────────────────── Keyboard ─────────────────────────────

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Media keys with the window in front are handled here (Windows might send them to another app's
        // session); if Windows' media controls deliver the same press too, OnMediaButton drops the duplicate
        if (keyData is Keys.MediaPlayPause or Keys.MediaStop or Keys.MediaNextTrack or Keys.MediaPreviousTrack)
        {
            if (IsDuplicateMediaPress("key")) return true;
        }

        // Typing in the library's search box, or moving through its list, is not a player shortcut
        if (_library.OwnsKey(keyData)) return base.ProcessCmdKey(ref msg, keyData);

        if (_lstChapters.Focused)
        {
            if (keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End) return base.ProcessCmdKey(ref msg, keyData);
            if (keyData == Keys.Enter) { PlayChapter(_lstChapters.SelectedIndex); return true; }
        }

        switch (keyData)
        {
            case Keys.Space:
            case Keys.MediaPlayPause: TogglePlay(); return true;
            case Keys.S:
            case Keys.MediaStop: Stop(); return true;
            case Keys.Left: SkipBy(-SkipStep); return true;
            case Keys.Right: SkipBy(SkipStep); return true;
            case Keys.Control | Keys.Left: SkipBy(-LongSkipStep); return true;
            case Keys.Control | Keys.Right: SkipBy(LongSkipStep); return true;
            case Keys.Home: SeekTo(TimeSpan.Zero); return true;
            case Keys.PageUp:
            case Keys.MediaPreviousTrack: PreviousChapter(); return true;
            case Keys.PageDown:
            case Keys.MediaNextTrack: NextChapter(); return true;
            case Keys.Up: ChangeVolume(0.05f); return true;
            case Keys.Down: ChangeVolume(-0.05f); return true;
            case Keys.M: ToggleMute(); return true;
            case Keys.OemMinus:
            case Keys.Subtract: StepSpeed(-1); return true;
            case Keys.Oemplus:
            case Keys.Oemplus | Keys.Shift: // '+' is Shift+'=' on US/UK layouts
            case Keys.Add: StepSpeed(+1); return true;
            case Keys.G: ChangeOffset(-OffsetStep); return true;
            case Keys.H: ChangeOffset(OffsetStep); return true;
            case Keys.J: ChangeOffset(-_subOffset); return true;
            case Keys.Control | Keys.O: OpenAudioDialog(); return true;
            case Keys.Control | Keys.Shift | Keys.O: OpenFolderDialog(); return true;
            case Keys.Control | Keys.L: ToggleLibrary(); return true;
            case Keys.B: AddBookmark(); return true;
            case Keys.Control | Keys.B: ShowBookmarks(); return true;
            case Keys.Control | Keys.F: ShowSearch(); return true;
            case Keys.Control | Keys.C: CopySubtitle(); return true;
            case Keys.R: RepeatSentence(); return true;
            case Keys.L: ToggleSentenceLoop(); return true;
            case Keys.Shift | Keys.Left: PreviousSentence(); return true;
            case Keys.Shift | Keys.Right: NextSentence(); return true;
            case Keys.K: ToggleSkipSilences(); return true;
            case Keys.Control | Keys.M: ToggleMiniPlayer(); return true;
            case Keys.V: ToggleVoiceBoost(); return true;
            case Keys.Control | Keys.T: OpenSrtDialog(); return true;
            case Keys.Control | Keys.P: ShowOptions(); return true;
            case Keys.Control | Keys.R: ShowTranscribe(); return true;
            case Keys.F1: ShowShortcuts(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ───────────────────────────── File ─────────────────────────────

    async void OpenAudioDialog()
    {
        using var dlg = new OpenFileDialog { Title = "Open audio file", Filter = AudioFormats.DialogFilter };
        if (dlg.ShowDialog(this) == DialogResult.OK) await LoadAudioAsync(dlg.FileName);
    }

    /// <summary>A folder of audio files (one per chapter, possibly in CD subfolders) played as one book.</summary>
    async void OpenFolderDialog()
    {
        using var dlg = new FolderBrowserDialog { Description = "Choose the folder that contains the book's audio files", UseDescriptionForTitle = true };
        if (_audioPath != null) dlg.InitialDirectory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_audioPath)) ?? "";
        if (dlg.ShowDialog(this) == DialogResult.OK) await LoadAudioAsync(dlg.SelectedPath);
    }

    void OpenSrtDialog()
    {
        using var dlg = new OpenFileDialog { Title = "Load subtitles", Filter = "SubRip subtitles (*.srt)|*.srt|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK) LoadSrt(dlg.FileName);
    }

    async Task<bool> LoadAudioAsync(string path, bool autoPlay = true)
    {
        path = Path.GetFullPath(path);
        // Loads can overlap (drag & drop or Ctrl+O while a big file is still opening): only the latest counts
        int generation = ++_loadGeneration;
        UseWaitCursor = true;
        ShowOsd("Loading…");
        try
        {
            // Opening can scan the whole file (e.g. the MP3 seek table): keep it off the UI thread
            var syncFolder = _settings.SyncFolder;
            var (info, reader, syncKey, synced, srt) = await Task.Run(() =>
            {
                var (i, r) = BookAudio.OpenWithInfo(path);
                var key = BookSync.KeyFor(path, i.Asin);
                // The newest position of this book on any PC, if syncing is on; a book with an ASIN may have been
                // saved by an earlier version under its name and size
                var legacy = BookSync.LegacyKeyFor(path) is { } old && old != key ? old : null;
                // The subtitles too: for a folder this lists it, which can be slow on a network drive
                return (i, r, key, syncFolder != null && key != null ? BookSync.Find(syncFolder, key, legacy) : null,
                        BookSource.FindSubtitle(path));
            });
            if (generation != _loadGeneration)
            {
                reader.Dispose(); // superseded by a newer load
                return false;
            }
            RememberCurrentBook(); // keep the position of the book being replaced
            _keepSavedPosition = false;
            _player.Load(reader, path);
            _audioPath = path;

            _fileChapters = BuildChapters(info.Chapters, reader.TotalTime);
            _chaptersFromSubtitles = false;
            StopLoop(quiet: true);
            SetChapters(_fileChapters);

            var title = string.IsNullOrWhiteSpace(info.Title) ? BookSource.DisplayName(path) : info.Title;
            var byline = Byline(info, title);
            _lblTitle.Text = byline.Length > 0 ? $"{title}  —  {byline}" : title;
            Text = $"{(BookSource.IsFolder(path) ? BookSource.DisplayName(path) : Path.GetFileName(path))} — {AppName}";

            // Subtitles: automatically look for an .srt with the same name (or, for an Audible export, the
            // one the exporter left in the book's folder)
            _subs = null;
            ResetSecondSubtitles();
            _srtPath = null;
            _subOffset = TimeSpan.Zero;
            if (srt != null) LoadSrt(srt, quiet: true);
            _lblOsd.Visible = false;
            if (_subs != null && srt != null) ShowOsd($"Subtitles loaded: {Path.GetFileName(srt)}");

            var previous = _settings.GetBook(path);
            // The book's own speed, or the last one used for a book never opened
            _player.Speed = previous?.Speed ?? _settings.PlaybackSpeed;
            UpdateSpeedUi();
            var lastListened = previous?.LastOpened;
            var syncedFrom = ApplySyncedPosition(path, synced);
            RestoreBookState(path, autoPlay);
            CancelSleepAtChapterEnd();
            if (syncedFrom != null) ShowOsd($"Continuing from {FormatTime(_player.Position)}, where you stopped on {syncedFrom}");

            // What the library shows without opening the book; details edited in the library win over the tags
            var book = CurrentBook!;
            _fileDetails = new FileDetails(title, info.Artist, info.Series, info.SeriesNumber, _lblTitle.Text, info.Cover);
            if (!book.DetailsEdited)
            {
                book.Title = title;
                book.Author = info.Artist;
                book.Series = info.Series;
                book.SeriesNumber = info.SeriesNumber;
            }
            book.Asin = info.Asin;
            book.DurationSeconds = reader.TotalTime.TotalSeconds;
            book.SyncKey = syncKey;
            ShowBookDetails();
            _ = SaveLibraryCoverAsync(path, info.Cover);
            _library.SyncWithSettings(_audioPath);
            RefreshBookmarkMarks();
            UpdateMediaControls();

            // Resuming a book left a while ago: smart rewind on the first Play
            _pausedSince = _player.Position > TimeSpan.FromSeconds(1) && lastListened is { } last && last != default ? last.ToLocalTime() : null;
            if (autoPlay) PlayResuming();
            return true;
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration) return false; // a newer load is in charge of the UI
            if (!_player.IsLoaded) ClearLoadedFile();         // the player was already unloaded for the new file
            _lblOsd.Visible = false;
            MessageBox.Show(this, $"Could not open the file:\n{ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            if (generation == _loadGeneration) UseWaitCursor = false;
            UpdateUi();
        }
    }

    /// <summary>Resets the file-related UI state when no file is loaded any more.</summary>
    void ClearLoadedFile()
    {
        _audioPath = null;
        _fileDetails = null;
        _chapters = [];
        _fileChapters = [];
        _chaptersFromSubtitles = false;
        _loop = null;
        _currentChapter = -1;
        _lstChapters.Items.Clear();
        _library.SyncWithSettings(null); // no book open any more: none highlighted
        _seek.Marks = [];
        _lblTitle.Text = "";
        Text = AppName;
        _subs = null;
        ResetSecondSubtitles();
        _srtPath = null;
        _pausedSince = null;
        SetCover(null);
        _seek.Bookmarks = [];
        UpdateMediaControls();
    }

    static List<Chapter> BuildChapters(List<Chapter> raw, TimeSpan duration)
    {
        var sorted = raw.Where(c => c.Start < duration).OrderBy(c => c.Start).ToList();
        var result = new List<Chapter>(sorted.Count);
        for (int i = 0; i < sorted.Count; i++)
        {
            var title = string.IsNullOrWhiteSpace(sorted[i].Title) ? $"Chapter {i + 1}" : sorted[i].Title;
            var end = i + 1 < sorted.Count ? sorted[i + 1].Start : duration;
            result.Add(new Chapter(title, sorted[i].Start, end));
        }
        return result;
    }

    void LoadSrt(string path, bool quiet = false)
    {
        try
        {
            var track = SubtitleTrack.Load(path);
            if (track.Cues.Count == 0)
            {
                if (!quiet) MessageBox.Show(this, "The file does not contain valid SRT subtitles.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _subs = track;
            _srtPath = Path.GetFullPath(path);
            _subOffset = TimeSpan.Zero;
            StopLoop(quiet: true);
            if (!quiet) ShowOsd($"Subtitles: {Path.GetFileName(path)} ({track.Cues.Count} lines)");
            ApplySubtitleChapters();
        }
        catch (Exception ex)
        {
            if (!quiet) MessageBox.Show(this, $"Could not read the subtitles:\n{ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        UpdateUi();
    }

    void RemoveSubtitles()
    {
        _subs = null;
        ResetSecondSubtitles(); // they go with the first ones
        _srtPath = null;
        StopLoop(quiet: true);
        ApplySubtitleChapters(); // chapters found in them go too
        UpdateUi();
    }

    // ───────────────────────────── Commands ─────────────────────────────

    void TogglePlay()
    {
        if (!_player.IsLoaded) { OpenAudioDialog(); return; }
        if (_player.IsPlaying)
        {
            _player.Pause();
            MarkPaused();
            SaveSettings(); // a paused book may stay untouched for days (or the PC may go to sleep)
        }
        else
        {
            PlayResuming();
        }
        UpdateUi();
    }

    void Stop()
    {
        if (!_player.IsLoaded) return;
        RememberCurrentBook();   // save where the book was before going back to 0:00
        _keepSavedPosition = true;
        _pausedSince = null;
        _player.Stop();
        SaveSettings();
        UpdateUi();
    }

    void SkipBy(TimeSpan delta) => SeekTo(_player.Position + delta);

    void SeekTo(TimeSpan time)
    {
        if (!_player.IsLoaded) return;
        _keepSavedPosition = false;
        _pausedSince = null; // a place chosen by the listener: no smart rewind from there
        _player.Seek(time);
        // While paused nothing else records the move: note it now (in memory), so a sync check when the window
        // is activated again does not replace it with an older position from another PC
        if (!_player.IsPlaying) RememberCurrentBook();
        FollowSleepChapter(time);
        UpdateUi();
    }

    void PlayChapter(int index)
    {
        if (index < 0 || index >= _chapters.Count) return;
        _keepSavedPosition = false;
        _pausedSince = null;
        _player.Seek(_chapters[index].Start);
        FollowSleepChapter(_chapters[index].Start);
        MarkListening();
        if (!_player.IsPlaying) _player.Play();
        UpdateUi();
    }

    void PreviousChapter()
    {
        var pos = _player.Position;
        int i = ChapterIndexAt(pos);
        if (i < 0) { SeekTo(TimeSpan.Zero); return; }
        // Like classic players: if the chapter started more than 3 s ago, go back to its start
        if (pos - _chapters[i].Start > TimeSpan.FromSeconds(3) || i == 0) SeekTo(_chapters[i].Start);
        else SeekTo(_chapters[i - 1].Start);
    }

    void NextChapter()
    {
        int i = ChapterIndexAt(_player.Position);
        if (i + 1 < _chapters.Count) SeekTo(_chapters[i + 1].Start);
    }

    void SetVolume(float volume)
    {
        _player.Volume = volume;
        _volume.Value = _player.Volume;
        _btnMute.Text = _player.Volume <= 0 ? Glyphs.Mute : Glyphs.Volume;
    }

    void ChangeVolume(float delta)
    {
        SetVolume(_player.Volume + delta);
        ShowOsd($"Volume {_player.Volume:P0}");
    }

    void SetSpeed(double speed)
    {
        _player.Speed = speed;
        // Every narrator has a pace: the book keeps its speed (and it becomes the default for new books)
        if (CurrentBook is { } book) book.Speed = _player.Speed;
        UpdateSpeedUi();
        ShowOsd(CurrentBook != null ? $"Speed {FormatSpeed(_player.Speed)} for this book" : $"Speed {FormatSpeed(_player.Speed)}");
    }

    /// <summary>Moves to the previous/next speed preset (from the nearest one).</summary>
    void StepSpeed(int direction)
    {
        int nearest = Array.IndexOf(SpeedPresets, SpeedPresets.MinBy(s => Math.Abs(s - _player.Speed)));
        SetSpeed(SpeedPresets[Math.Clamp(nearest + direction, 0, SpeedPresets.Length - 1)]);
    }

    void UpdateSpeedUi()
    {
        _btnSpeed.Text = FormatSpeed(_player.Speed);
        _btnSpeed.ForeColor = _player.Speed == 1.0 ? Theme.Text : Theme.Accent;
        foreach (var (speed, item) in _speedItems) item.Checked = Math.Abs(speed - _player.Speed) < 0.001;
    }

    static string FormatSpeed(double speed) => $"{speed:0.##}×";

    void SetSleepTimer(int minutes)
    {
        _sleepAtChapterEnd = false;
        _sleepMinutes = minutes;
        _sleepAt = minutes > 0 ? DateTime.Now.AddMinutes(minutes) : null;
        _player.Fade = 1;
        ShowOsd(minutes > 0 ? $"Sleep timer: {minutes} minutes" : "Sleep timer off");
        UpdateUi();
    }

    void SetSleepAtChapterEnd()
    {
        if (_chapters.Count == 0) return;
        _sleepAt = null;
        _sleepAtChapterEnd = true;
        _sleepChapter = ChapterIndexAt(_player.Position);
        _player.Fade = 1;
        ShowOsd("Sleep timer: end of chapter");
        UpdateUi();
    }

    /// <summary>A jump made by the user (not the chapter ending): the "end of chapter" timer follows it.</summary>
    void FollowSleepChapter(TimeSpan target)
    {
        if (_sleepAtChapterEnd) _sleepChapter = ChapterIndexAt(target);
    }

    void CancelSleepAtChapterEnd()
    {
        if (!_sleepAtChapterEnd) return;
        _sleepAtChapterEnd = false;
        _player.Fade = 1;
    }

    void SleepNow(string message)
    {
        _player.Pause();
        MarkPaused();
        _player.Fade = 1;            // the next Play starts at the normal volume
        _sleepAt = null;
        _sleepAtChapterEnd = false;
        ShowOsd(message);
        SaveSettings(); // typically the listener is falling asleep: make sure the place is stored
    }

    /// <summary>Called on every UI tick: fades out over the last seconds, then pauses.</summary>
    void UpdateSleepTimer(TimeSpan pos, int chapter)
    {
        double remaining;
        if (_sleepAt is { } at)
        {
            remaining = (at - DateTime.Now).TotalSeconds;
            if (remaining <= 0) { SleepNow("Sleep timer: playback paused"); return; }
        }
        else if (_sleepAtChapterEnd)
        {
            if (chapter != _sleepChapter)
            {
                // Reached the next chapter: stop exactly at its start so resuming begins cleanly
                if (chapter == _sleepChapter + 1 && _player.IsPlaying)
                {
                    SleepNow("Sleep timer: end of chapter");
                    _player.Seek(_chapters[chapter].Start);
                }
                else CancelSleepAtChapterEnd();
                return;
            }
            // Remaining audible time in the chapter, at the current speed (before the first chapter: no fade yet)
            remaining = chapter >= 0 ? (_chapters[chapter].End - pos).TotalSeconds / _player.Speed : double.PositiveInfinity;
        }
        else return;

        _player.Fade = _player.IsPlaying && remaining < SleepFadeSeconds ? (float)(remaining / SleepFadeSeconds) : 1f;
    }

    string SleepStatus()
    {
        if (_sleepAt is { } at) return $"  ·  Sleep in {FormatTime(at - DateTime.Now + TimeSpan.FromSeconds(0.999))}";
        return _sleepAtChapterEnd ? "  ·  Sleep at end of chapter" : "";
    }

    void ToggleMute()
    {
        if (_player.Volume > 0)
        {
            _volumeBeforeMute = _player.Volume;
            SetVolume(0);
            ShowOsd("Muted");
        }
        else
        {
            SetVolume(_volumeBeforeMute > 0 ? _volumeBeforeMute : 0.8f);
            ShowOsd($"Volume {_player.Volume:P0}");
        }
    }

    void ChangeOffset(TimeSpan delta)
    {
        _subOffset += delta;
        var ms = (int)Math.Round(_subOffset.TotalMilliseconds);
        ShowOsd(ms == 0 ? "Subtitles in sync (0 ms)" : $"Subtitle delay: {ms:+0;-0} ms");
        UpdateUi();
    }

    void ShowShortcuts() => MessageBox.Show(this,
        """
        Space           Play / Pause
        S               Stop
        ← / →           Back / Forward 10 s
        Ctrl+← / →      Back / Forward 1 min
        Home            Go to start
        PgUp / PgDn     Previous / Next chapter
        ↑ / ↓           Volume
        M               Mute
        − / +           Slower / faster playback (subtitles stay in sync)
                        Sleep timer: Playback › Sleep timer
        V               Voice boost (evens out the narrator's volume)
        B               Add a bookmark
        Ctrl+B          Bookmarks
        G / H           Subtitles: show 100 ms earlier / later
        J               Reset subtitle sync
        Ctrl+F          Search in the subtitles
        Ctrl+C          Copy the subtitle on screen (also right-click on it)
        R               Repeat the sentence being spoken
        Shift+← / →     Previous / next sentence
        L               Loop the sentence (L again to stop)
        K               Skip silences (shorten long pauses)
        Ctrl+M          Mini player
        Ctrl+O          Open audio file
        Ctrl+Shift+O    Open a folder of audio files as one book
        Ctrl+L          Show / hide the library
        Ctrl+T          Load SRT subtitles
        Ctrl+P          Subtitle appearance
        Ctrl+R          Transcribe with Whisper (runs locally; several books can be queued)

        Double-click a chapter (or press Enter) to jump to it.
        Click the subtitle area to play/pause; drag anywhere to move the window.
        You can also drag an audio file, a folder and/or an .srt file into the window.
        An .srt with the same name as the audio file is loaded automatically.
        Media keys and headset buttons work even when the window is in the background.

        Supported formats: MP3, M4A, M4B, AAC, MP4, WMA, WAV, FLAC, AIFF, OGG.
        Every book reopens where you left off (the library, Ctrl+L), a little earlier after a long pause.
        """, "Keyboard Shortcuts", MessageBoxButtons.OK, MessageBoxIcon.Information);

    // ───────────────────────────── UI updates ─────────────────────────────

    int ChapterIndexAt(TimeSpan pos)
    {
        var t = pos + TimeSpan.FromMilliseconds(50);
        for (int i = _chapters.Count - 1; i >= 0; i--)
            if (_chapters[i].Start <= t) return i;
        return -1;
    }

    void UpdateUi()
    {
        bool loaded = _player.IsLoaded;
        var pos = _player.Position;
        var duration = _player.Duration;

        _seek.Maximum = Math.Max(duration.TotalSeconds, 1);
        _seek.Value = pos.TotalSeconds;
        SetText(_lblTime, $"{FormatTime(pos)} / {FormatTime(duration)}");
        SetText(_btnPlay, _player.IsPlaying ? Glyphs.Pause : Glyphs.Play);

        // Current chapter
        int ci = ChapterIndexAt(pos);
        if (ci != _currentChapter)
        {
            _currentChapter = ci;
            if (ci >= 0) ShowCurrentChapter(ci);
            _lstChapters.Invalidate();
        }
        if (loaded) UpdateSleepTimer(pos, ci);
        if (loaded) UpdateLoop(pos);
        if (loaded) SkipOutro(pos, duration);
        UpdateListeningStats();
        UpdateMiniPlayer();
        UpdateTrayText();
        SetText(_lblChapter, !loaded ? ""
            : (_chapters.Count == 0 ? "No chapters in this file"
              : ci >= 0 ? $"Chapter {ci + 1} of {_chapters.Count}  ·  {_chapters[ci].Title}"
              : "") + SleepStatus() + LoopStatus());

        // Subtitles
        if (!loaded) _subView.ShowText("Open an audio file (Ctrl+O) or drag it here", hint: true);
        else if (_subs == null) _subView.ShowText("No subtitles — press Ctrl+T, drag an .srt file here, or transcribe with Ctrl+R", hint: true);
        else _subView.ShowText(_subs.TextAt(pos - _subOffset) ?? "", second: SecondSubtitleAt(pos));

        if (_lblOsd.Visible && DateTime.Now > _osdUntil) _lblOsd.Visible = false;

        // No standby (and, if chosen, no screensaver/display off) while playing; paused or stopped releases it
        KeepAwake.Set(_player.IsPlaying, _settings.KeepScreenOn);
        UpdateMediaControls();

        // Periodically save the position in case the app is closed abnormally
        if (_player.IsPlaying && DateTime.Now - _lastSave > TimeSpan.FromSeconds(15)) SaveSettings();
    }

    /// <summary>
    /// Selects the chapter that started playing and scrolls it into view (with some of the following
    /// chapters below it). Only when the chapter changes, so browsing the list in between is not undone.
    /// </summary>
    void ShowCurrentChapter(int index)
    {
        if (index >= _lstChapters.Items.Count) return;
        // Minimized, the list has no height: do it when it is shown again (see the list's Resize handler)
        _chapterScrollPending = _lstChapters.ClientSize.Height < _lstChapters.ItemHeight;
        if (_chapterScrollPending) return;
        // Scroll first: selecting an off-screen row would scroll it just to the bottom edge
        int visible = Math.Max(1, _lstChapters.ClientSize.Height / Math.Max(1, _lstChapters.ItemHeight));
        if (index < _lstChapters.TopIndex || index >= _lstChapters.TopIndex + visible)
            _lstChapters.TopIndex = Math.Max(0, index - visible / 3);
        _lstChapters.SelectedIndex = index;
    }

    static void SetText(Control c, string text)
    {
        if (c.Text != text) c.Text = text;
    }

    void ShowOsd(string text)
    {
        _lblOsd.Text = text;
        _lblOsd.Visible = true;
        _lblOsd.BringToFront();
        PositionOsd();
        _osdUntil = DateTime.Now.AddSeconds(1.8);
    }

    void PositionOsd()
    {
        if (_lblOsd.Parent is not { } p) return;
        int margin = LogicalToDeviceUnits(16);
        _lblOsd.Location = new Point(p.ClientSize.Width - _lblOsd.Width - margin, p.ClientSize.Height - _lblOsd.Height - margin);
    }

    /// <summary>
    /// The line under the title: author, who reads the book and the album, each only if the file carries it.
    /// </summary>
    internal static string Byline(MediaInfo info, string title)
    {
        // The album tag of an audiobook often repeats the title: it would be a line saying nothing
        var album = string.Equals(info.Album, title, StringComparison.OrdinalIgnoreCase) ? null : info.Album;
        return string.Join(" · ", new[]
        {
            info.Artist,
            string.IsNullOrWhiteSpace(info.Narrator) ? null : $"read by {info.Narrator}",
            album,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    void DrawChapterItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _chapters.Count) return;
        var g = e.Graphics;
        var b = e.Bounds;
        var chapter = _chapters[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        bool current = e.Index == _currentChapter;
        int L(int v) => _lstChapters.LogicalToDeviceUnits(v);

        using (var bg = new SolidBrush(selected ? Theme.Surface : Theme.Panel))
            g.FillRectangle(bg, b);
        if (current)
        {
            using var accent = new SolidBrush(Theme.Accent);
            g.FillRectangle(accent, b.X, b.Y + L(6), L(3), b.Height - L(12));
        }

        var numRect = new Rectangle(b.X + L(12), b.Y, L(28), b.Height);
        var timeRect = new Rectangle(b.Right - L(76), b.Y, L(64), b.Height);
        var titleRect = new Rectangle(numRect.Right + L(4), b.Y, timeRect.Left - numRect.Right - L(12), b.Height);
        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        using var bold = current ? new Font(_lstChapters.Font, FontStyle.Bold) : null;
        TextRenderer.DrawText(g, (e.Index + 1).ToString("00"), _lstChapters.Font, numRect, Theme.TextDim, flags);
        TextRenderer.DrawText(g, chapter.Title, bold ?? _lstChapters.Font, titleRect,
            current ? Theme.Accent : Theme.Text, flags | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, FormatTime(chapter.Start), _lstChapters.Font, timeRect, Theme.TextDim, flags | TextFormatFlags.Right);
    }
}
