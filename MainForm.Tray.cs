namespace aBookPlayer;

// Mini player and the notification-area icon
public sealed partial class MainForm
{
    MiniPlayerForm? _mini;
    NotifyIcon? _tray;
    ToolStripMenuItem? _trayItem;

    // ───────────────────────────── Mini player ─────────────────────────────

    /// <summary>Ctrl+M: the small always-on-top player instead of the main window, and back.</summary>
    void ToggleMiniPlayer()
    {
        if (_mini is { Visible: true })
        {
            RestoreMainWindow();
            return;
        }
        if (_mini == null || _mini.IsDisposed)
        {
            _mini = new MiniPlayerForm();
            _mini.PlayPauseClicked += (_, _) => TogglePlay();
            _mini.BackClicked += (_, _) => SkipBy(-SkipStep);
            _mini.ForwardClicked += (_, _) => SkipBy(SkipStep);
            _mini.SeekRequested += (_, seconds) => SeekTo(TimeSpan.FromSeconds(seconds));
            _mini.RestoreClicked += (_, _) => RestoreMainWindow();
            _mini.LocationChanged += (_, _) =>
            {
                if (_mini.Visible) _settings.MiniPlayerLocation = [_mini.Left, _mini.Top];
            };
        }
        // Where it was last time (if still on a screen), otherwise the bottom right corner
        var area = Screen.FromControl(this).WorkingArea;
        bool saved = _settings.MiniPlayerLocation is [var x, var y] && Screen.AllScreens.Any(s => s.WorkingArea.Contains(x + 20, y + 20));
        if (saved) _mini.Location = new Point(_settings.MiniPlayerLocation![0], _settings.MiniPlayerLocation[1]);
        UpdateMiniPlayer(force: true);
        _mini.Show();
        // The first time it is sized for the screen's DPI only when shown: place it in the corner afterwards
        if (!saved) _mini.Location = new Point(area.Right - _mini.Width - 24, area.Bottom - _mini.Height - 24);
        Hide();
    }

    /// <summary>Brings the main window back (from the mini player or the notification area).</summary>
    void RestoreMainWindow()
    {
        if (_mini is { IsDisposed: false }) _mini.Hide();
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = _settings.WindowMaximized ? FormWindowState.Maximized : FormWindowState.Normal;
        Activate();
    }

    /// <summary>Called on every UI tick while the mini player is shown.</summary>
    void UpdateMiniPlayer(bool force = false)
    {
        if (_mini == null || _mini.IsDisposed || (!_mini.Visible && !force)) return;
        var pos = _player.Position;
        var title = !_player.IsLoaded ? "No book open" : CurrentBook?.Title ?? _lblTitle.Text;
        int ci = ChapterIndexAt(pos);
        var line = _subs?.TextAt(pos - _subOffset) ?? (ci >= 0 ? _chapters[ci].Title : null);
        _mini.ShowState(title, line, pos, _player.Duration, _player.IsPlaying);
    }

    // ───────────────────────────── Notification area ─────────────────────────────

    ToolStripMenuItem MakeTrayItem()
    {
        _trayItem = new ToolStripMenuItem("Minimize to the notification area") { Checked = _settings.TrayIcon };
        _trayItem.Click += (_, _) =>
        {
            _settings.TrayIcon = _trayItem.Checked = !_trayItem.Checked;
            UpdateTrayIcon();
            SaveSettings();
        };
        return _trayItem;
    }

    void UpdateTrayIcon()
    {
        if (!_settings.TrayIcon)
        {
            _tray?.Dispose();
            _tray = null;
            return;
        }
        if (_tray != null) return;
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false };
        menu.Items.Add(MakeItem("Play / Pause", null, TogglePlay));
        menu.Items.Add(MakeItem("Previous chapter", null, PreviousChapter));
        menu.Items.Add(MakeItem("Next chapter", null, NextChapter));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(MakeItem("Mini player", null, ToggleMiniPlayer));
        menu.Items.Add(MakeItem("Show aBookPlayer", null, RestoreMainWindow));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(MakeItem("Exit", null, Close));
        _tray = new NotifyIcon { Icon = Theme.AppIcon, Text = AppName, ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) RestoreMainWindow();
        };
    }

    /// <summary>Minimized with the notification-area option on: the window leaves the taskbar.</summary>
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_settings.TrayIcon && WindowState == FormWindowState.Minimized && Visible) Hide();
        // Restored from maximized after the library was shown: Windows applies the larger minimum from the old
        // left edge, which can push the window past the right edge of the screen
        if (WindowState == FormWindowState.Normal && _lastWindowState == FormWindowState.Maximized && IsHandleCreated)
            BeginInvoke(FitToScreen);
        _lastWindowState = WindowState;
    }

    FormWindowState _lastWindowState;

    /// <summary>Called on every UI tick: the icon's tooltip says what is playing (at most 127 characters).</summary>
    void UpdateTrayText()
    {
        if (_tray == null) return;
        var text = !_player.IsLoaded ? AppName
            : $"{(_player.IsPlaying ? "▶" : "❚❚")} {CurrentBook?.Title ?? AppName} · {FormatTime(_player.Position)}";
        if (text.Length > 127) text = text[..127];
        if (_tray.Text != text) _tray.Text = text;
    }
}
