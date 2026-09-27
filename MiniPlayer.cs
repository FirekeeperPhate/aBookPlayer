using System.Runtime.InteropServices;

namespace aBookPlayer;

/// <summary>
/// A small always-on-top window: title, the sentence being spoken, the position and the main buttons.
/// The main window is hidden meanwhile; ⤢ (or closing it) brings it back.
/// </summary>
sealed class MiniPlayerForm : Form
{
    readonly Label _title = new() { Dock = DockStyle.Top, Height = 24, AutoEllipsis = true, UseMnemonic = false, Font = new Font("Segoe UI Semibold", 10f) };
    readonly Label _line = new() { Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false, ForeColor = Theme.Text, Font = new Font("Segoe UI", 10.5f) };
    readonly Label _time = new() { AutoSize = true, ForeColor = Theme.TextDim, Font = new Font("Segoe UI", 9f) };
    readonly SeekBar _seek = new() { Dock = DockStyle.Bottom, Height = 22 };
    readonly IconButton _play = new(Glyphs.Play, 16f, 44, 38);
    readonly IconButton _back = new(Glyphs.Rewind, 12f, 34, 34);
    readonly IconButton _forward = new(Glyphs.FastForward, 12f, 34, 34);
    readonly IconButton _restore = new("", 11f, 34, 34); // full screen glyph: back to the main window

    public event EventHandler? PlayPauseClicked, BackClicked, ForwardClicked, RestoreClicked;
    public event EventHandler<double>? SeekRequested;

    public MiniPlayerForm()
    {
        Text = "aBookPlayer";
        Icon = Theme.AppIcon;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Padding = new Padding(12, 8, 12, 6);
        ClientSize = new Size(420, 132);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        buttons.Controls.AddRange([_back, _play, _forward, _restore]);
        foreach (Control b in buttons.Controls) b.Margin = new Padding(2, 0, 2, 0);
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44 };
        _time.Location = new Point(2, 14);
        bottom.Controls.Add(buttons);
        bottom.Controls.Add(_time);

        Controls.Add(_line);
        Controls.Add(_title);
        Controls.Add(bottom);
        Controls.Add(_seek);

        _play.Click += (_, _) => PlayPauseClicked?.Invoke(this, EventArgs.Empty);
        _back.Click += (_, _) => BackClicked?.Invoke(this, EventArgs.Empty);
        _forward.Click += (_, _) => ForwardClicked?.Invoke(this, EventArgs.Empty);
        _restore.Click += (_, _) => RestoreClicked?.Invoke(this, EventArgs.Empty);
        _seek.ValueCommitted += (_, v) => SeekRequested?.Invoke(this, v);

        // No title bar: drag it by any empty spot
        foreach (Control c in new Control[] { this, _title, _line, _time, bottom, buttons })
            c.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                ReleaseCapture();
                SendMessage(Handle, 0x00A1 /* WM_NCLBUTTONDOWN */, 2 /* HTCAPTION */, 0);
            };
        var tip = DarkToolTip.Create();
        tip.SetToolTip(_restore, "Back to the full window (Ctrl+M)");
        tip.SetToolTip(_back, "Back 10 s");
        tip.SetToolTip(_forward, "Forward 10 s");
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.UseDarkTitleBar(Handle);
    }

    /// <summary>Sizes are 96-DPI pixels: scaled once all controls exist (WinForms scales when layout resumes).</summary>
    protected override void OnLoad(EventArgs e)
    {
        var location = Location;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);
        PerformLayout();
        Location = location; // scaling moves the window too
        base.OnLoad(e);
    }

    /// <summary>
    /// Closing it (Alt+F4) means "back to the full window": the main window is hidden meanwhile, so letting the
    /// mini player go would leave the app playing with no window at all.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            RestoreClicked?.Invoke(this, EventArgs.Empty);
        }
        base.OnFormClosing(e);
    }

    /// <summary>A thin border, since the window has no frame.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    public void ShowState(string title, string? line, TimeSpan position, TimeSpan duration, bool playing)
    {
        if (_title.Text != title) _title.Text = title;
        var text = line?.Replace('\n', ' ') ?? "";
        if (_line.Text != text) _line.Text = text;
        var time = $"{Format(position)} / {Format(duration)}";
        if (_time.Text != time) _time.Text = time;
        _seek.Maximum = Math.Max(duration.TotalSeconds, 1);
        _seek.Value = position.TotalSeconds;
        var glyph = playing ? Glyphs.Pause : Glyphs.Play;
        if (_play.Text != glyph) _play.Text = glyph;
    }

    static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
}
