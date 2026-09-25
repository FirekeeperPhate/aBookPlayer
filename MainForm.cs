using Microsoft.Win32;
using NAudio.Wave;

namespace aBookPlayer;

public sealed class MainForm : Form
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

    readonly MenuStrip _menu = new();
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
    readonly ContextMenuStrip _speedMenu = new() { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true };
    readonly List<(double Speed, ToolStripMenuItem Item)> _speedItems = [];

    static readonly double[] SpeedPresets = [0.5, 0.75, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0];

    public MainForm(string? startupFile = null)
    {
        _startupFile = startupFile;
        SuspendLayout();
        Text = AppName;
        Icon = Theme.AppIcon;
        ClientSize = new Size(1040, 640);
        MinimumSize = new Size(780, 480);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.75f);
        AllowDrop = true;

        BuildMenu();
        BuildLayout();
        WireEvents();
        EnableFileDrop(this);

        // Same as designer-generated code: sizes are 96-DPI pixels and get scaled here
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);
        PerformLayout();

        _player.Volume = _settings.Volume;
        _player.Speed = _settings.PlaybackSpeed;
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
                MakeRecentMenu(),
                MakeItem("Load SRT subtitles…", "Ctrl+T", OpenSrtDialog),
                MakeItem("Remove subtitles", null, RemoveSubtitles),
                new ToolStripSeparator(),
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
                MakeKeepScreenOnItem()),
            MakeMenu("&Subtitles",
                MakeItem("Show 100 ms earlier", "G", () => ChangeOffset(-OffsetStep)),
                MakeItem("Show 100 ms later", "H", () => ChangeOffset(OffsetStep)),
                MakeItem("Reset sync", "J", () => ChangeOffset(-_subOffset)),
                new ToolStripSeparator(),
                MakeItem("Transcribe audio with Whisper…", "Ctrl+R", ShowTranscribe),
                new ToolStripSeparator(),
                MakeItem("Subtitle appearance…", "Ctrl+P", ShowOptions)),
            MakeMenu("&Help",
                MakeItem("Keyboard shortcuts", "F1", ShowShortcuts)),
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
                var item = new ToolStripMenuItem($"&{(++n) % 10}  {Path.GetFileNameWithoutExtension(path)}{position}") { ToolTipText = path };
                item.Click += async (_, _) => await OpenRecentAsync(path);
                menu.DropDownItems.Add(item);
            }
            if (n == 0) menu.DropDownItems.Add(new ToolStripMenuItem("(empty)") { Enabled = false });
            else
            {
                menu.DropDownItems.Add(new ToolStripSeparator());
                menu.DropDownItems.Add(MakeItem("Clear list", null, () =>
                {
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
        bool exists = await Task.Run(() => File.Exists(path)); // may be slow on an unreachable drive
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
        center.Controls.Add(_subView);
        center.Controls.Add(_lblChapter);
        center.Controls.Add(_lblTitle);
        center.Controls.Add(_lblOsd);
        _lblOsd.BringToFront();
        center.Resize += (_, _) => PositionOsd();

        // Chapter list on the right
        var right = new Panel { Dock = DockStyle.Right, Width = 320, BackColor = Theme.Panel };
        right.Controls.Add(_lstChapters);
        right.Controls.Add(_lblChaptersHeader);

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

        // Add order = reverse docking order (the menu docks first, at the very top)
        Controls.Add(center);
        Controls.Add(right);
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
        _lstChapters.HandleCreated += (_, _) => Theme.UseDarkScrollBars(_lstChapters);
        // Owner-drawn rows don't rescale by themselves when the window moves to a monitor with another DPI
        _lstChapters.DpiChangedAfterParent += (_, _) => _lstChapters.ItemHeight = _lstChapters.LogicalToDeviceUnits(34);

        _player.Ended += (_, _) =>
        {
            // The book is over: nothing left for a sleep timer to stop
            _sleepAt = null;
            CancelSleepAtChapterEnd();
            _player.Fade = 1;
            UpdateUi();
        };
        _player.Error += (_, ex) =>
        {
            SaveSettings(); // the player kept the position: store it before anything else happens
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
            ? files.Where(f => AudioFormats.IsSupported(f) || f.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)).ToArray()
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
            var audio = files.FirstOrDefault(AudioFormats.IsSupported);
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
        if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _lstChapters.ItemHeight = _lstChapters.LogicalToDeviceUnits(34);

        if (_startupFile != null && File.Exists(_startupFile))
            await OpenPathAsync(_startupFile, atStartup: true);
        else
            await RestoreLastSessionAsync(autoPlay: false);
    }

    /// <summary>A file opened from Explorer while the app was already running (see <see cref="SingleInstance"/>).</summary>
    public void OpenFromOtherInstance(string path)
    {
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
        if (path.Length > 0 && File.Exists(path)) _ = OpenPathAsync(path, atStartup: false);
    }

    void OpenDeferredFile()
    {
        var path = _deferredOpen;
        _deferredOpen = null;
        if (path != null && File.Exists(path)) _ = OpenPathAsync(path, atStartup: false);
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
        return AudioFormats.Extensions
            .Select(ext => Path.Combine(folder, baseName + ext))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Reopens the last book (paused) at the position where it was left.</summary>
    async Task RestoreLastSessionAsync(bool autoPlay)
    {
        if (_settings.LastFile != null && File.Exists(_settings.LastFile))
            await LoadAudioAsync(_settings.LastFile, autoPlay);
    }

    /// <summary>Saves the current book's position, subtitles and sync into the per-book history.</summary>
    void RememberCurrentBook()
    {
        if (_audioPath == null || !_player.IsLoaded) return;
        double position = _keepSavedPosition
            ? _settings.GetBook(_audioPath)?.PositionSeconds ?? 0   // stopped: don't overwrite the place in the book with 0:00
            : _player.Position.TotalSeconds;
        _settings.RememberBook(_audioPath, position, _srtPath, _subOffset.TotalMilliseconds);
    }

    /// <summary>Restores a book's saved subtitles, sync and position right after it has been loaded.</summary>
    void RestoreBookState(string path, bool autoPlay)
    {
        var book = _settings.GetBook(path);
        if (book?.SubtitleFile != null && !SamePath(book.SubtitleFile, _srtPath) && File.Exists(book.SubtitleFile))
            LoadSrt(book.SubtitleFile, quiet: true);
        if (book != null && _srtPath != null && SamePath(_srtPath, book.SubtitleFile))
            _subOffset = TimeSpan.FromMilliseconds(book.SubtitleOffsetMs);

        // Near the end counts as finished: start again from the beginning
        var pos = TimeSpan.FromSeconds(book?.PositionSeconds ?? 0);
        if (pos > TimeSpan.FromSeconds(1) && pos < _player.Duration - TimeSpan.FromSeconds(5))
        {
            _player.Seek(pos);
            ShowOsd(autoPlay ? $"Resuming from {FormatTime(pos)}" : $"Resuming from {FormatTime(pos)} — press Space to play");
        }
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
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveSettings();
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
        _player.Pause();
        SaveSettings();
        UpdateUi();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged; // static event: would keep the form alive
        _timer.Stop();
        KeepAwake.Set(playing: false, keepScreenOn: false);
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
                LoadSrt(dlg.SrtPath);
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
            var (info, reader) = await Task.Run(() => (MediaMetadata.Read(path), AudioFormats.Open(path)));
            if (generation != _loadGeneration)
            {
                reader.Dispose(); // superseded by a newer load
                return false;
            }
            RememberCurrentBook(); // keep the position of the book being replaced
            _keepSavedPosition = false;
            _player.Load(reader, path);
            _audioPath = path;

            _chapters = BuildChapters(info.Chapters, reader.TotalTime);
            _currentChapter = -1;
            _lstChapters.BeginUpdate();
            _lstChapters.Items.Clear();
            foreach (var c in _chapters) _lstChapters.Items.Add(c.Title);
            _lstChapters.EndUpdate();
            _seek.Marks = _chapters.Select(c => c.Start.TotalSeconds).ToArray();

            var title = string.IsNullOrWhiteSpace(info.Title) ? Path.GetFileNameWithoutExtension(path) : info.Title;
            var byline = string.Join(" · ", new[] { info.Artist, info.Album }.Where(s => !string.IsNullOrWhiteSpace(s)));
            _lblTitle.Text = byline.Length > 0 ? $"{title}  —  {byline}" : title;
            Text = $"{Path.GetFileName(path)} — {AppName}";

            // Subtitles: automatically look for an .srt with the same name
            _subs = null;
            _srtPath = null;
            _subOffset = TimeSpan.Zero;
            var srt = Path.ChangeExtension(path, ".srt");
            if (File.Exists(srt)) LoadSrt(srt, quiet: true);
            _lblOsd.Visible = false;
            if (_subs != null) ShowOsd($"Subtitles loaded: {Path.GetFileName(srt)}");
            RestoreBookState(path, autoPlay);
            CancelSleepAtChapterEnd();

            if (autoPlay) _player.Play();
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
        _chapters = [];
        _currentChapter = -1;
        _lstChapters.Items.Clear();
        _seek.Marks = [];
        _lblTitle.Text = "";
        Text = AppName;
        _subs = null;
        _srtPath = null;
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
            if (!quiet) ShowOsd($"Subtitles: {Path.GetFileName(path)} ({track.Cues.Count} lines)");
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
        _srtPath = null;
        UpdateUi();
    }

    // ───────────────────────────── Commands ─────────────────────────────

    void TogglePlay()
    {
        if (!_player.IsLoaded) { OpenAudioDialog(); return; }
        if (_player.IsPlaying)
        {
            _player.Pause();
            SaveSettings(); // a paused book may stay untouched for days (or the PC may go to sleep)
        }
        else
        {
            _keepSavedPosition = false;
            _player.Play();
        }
        UpdateUi();
    }

    void Stop()
    {
        if (!_player.IsLoaded) return;
        RememberCurrentBook();   // save where the book was before going back to 0:00
        _keepSavedPosition = true;
        _player.Stop();
        SaveSettings();
        UpdateUi();
    }

    void SkipBy(TimeSpan delta) => SeekTo(_player.Position + delta);

    void SeekTo(TimeSpan time)
    {
        if (!_player.IsLoaded) return;
        _keepSavedPosition = false;
        _player.Seek(time);
        FollowSleepChapter(time);
        UpdateUi();
    }

    void PlayChapter(int index)
    {
        if (index < 0 || index >= _chapters.Count) return;
        _keepSavedPosition = false;
        _player.Seek(_chapters[index].Start);
        FollowSleepChapter(_chapters[index].Start);
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
        UpdateSpeedUi();
        ShowOsd($"Speed {FormatSpeed(_player.Speed)}");
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
        G / H           Subtitles: show 100 ms earlier / later
        J               Reset subtitle sync
        Ctrl+O          Open audio file
        Ctrl+T          Load SRT subtitles
        Ctrl+P          Subtitle appearance
        Ctrl+R          Transcribe audio with Whisper (runs locally)

        Double-click a chapter (or press Enter) to jump to it.
        You can also drag an audio file and/or an .srt file into the window.
        An .srt with the same name as the audio file is loaded automatically.

        Supported formats: MP3, M4A, M4B, AAC, MP4, WMA, WAV, FLAC, AIFF, OGG.
        Every book reopens where you left off (File › Recent books).
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
            if (ci >= 0 && !_lstChapters.Focused) _lstChapters.SelectedIndex = ci;
            _lstChapters.Invalidate();
        }
        if (loaded) UpdateSleepTimer(pos, ci);
        SetText(_lblChapter, !loaded ? ""
            : (_chapters.Count == 0 ? "No chapters in this file"
              : ci >= 0 ? $"Chapter {ci + 1} of {_chapters.Count}  ·  {_chapters[ci].Title}"
              : "") + SleepStatus());

        // Subtitles
        if (!loaded) _subView.ShowText("Open an audio file (Ctrl+O) or drag it here", hint: true);
        else if (_subs == null) _subView.ShowText("No subtitles — press Ctrl+T, drag an .srt file here, or transcribe with Ctrl+R", hint: true);
        else _subView.ShowText(_subs.TextAt(pos - _subOffset) ?? "");

        if (_lblOsd.Visible && DateTime.Now > _osdUntil) _lblOsd.Visible = false;

        // No standby (and, if chosen, no screensaver/display off) while playing; paused or stopped releases it
        KeepAwake.Set(_player.IsPlaying, _settings.KeepScreenOn);

        // Periodically save the position in case the app is closed abnormally
        if (_player.IsPlaying && DateTime.Now - _lastSave > TimeSpan.FromSeconds(15)) SaveSettings();
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
