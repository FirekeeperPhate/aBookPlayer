using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace aBookPlayer;

/// <summary>Subtitle area: centered, word-wrapped text with an optional background box.</summary>
sealed class SubtitleView : Control
{
    SubtitleStyle _style = new();
    Font _font;
    readonly Font _hintFont = new("Segoe UI", 12f);
    string _text = "";
    bool _hint;

    public SubtitleView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Theme.Back;
        _font = CreateFont(_style);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public SubtitleStyle SubtitleStyle
    {
        get => _style.Clone();
        set
        {
            _style = value.Clone();
            var old = _font;
            _font = CreateFont(_style);
            old.Dispose();
            Invalidate();
        }
    }

    /// <summary>The subtitle on screen, or null when there is none (or only a hint is shown).</summary>
    public string? SubtitleText => _hint || string.IsNullOrWhiteSpace(_text) ? null : _text;

    /// <summary>Left click without moving the mouse.</summary>
    public event EventHandler? Clicked;

    /// <summary>The left button is held and the mouse moved: the window should be dragged.</summary>
    public event EventHandler? DragStarted;

    Point? _pressedAt;
    bool _activationClick;

    const int WM_MOUSEACTIVATE = 0x0021;

    protected override void WndProc(ref Message m)
    {
        // Sent when the window is clicked while another one is in front: that click only brings it forward
        if (m.Msg == WM_MOUSEACTIVATE && FindForm() is { } form && Form.ActiveForm != form) _activationClick = true;
        base.WndProc(ref m);
    }

    bool _pressActivated;   // the current press is the one that brought the window forward

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        // Tied to this press, whatever the button: a right-click activation must not swallow a later click
        _pressActivated = _activationClick;
        _activationClick = false;
        // The second press of a double-click is not another click (it would undo the first one)
        _pressedAt = e.Button == MouseButtons.Left && e.Clicks == 1 ? e.Location : null;
    }

    bool TakeActivationClick()
    {
        bool was = _pressActivated;
        _pressActivated = false;
        return was;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressedAt is not { } start) return;
        var drag = SystemInformation.DragSize;
        if (Math.Abs(e.X - start.X) > drag.Width / 2 || Math.Abs(e.Y - start.Y) > drag.Height / 2)
        {
            _pressedAt = null;
            _activationClick = false;
            DragStarted?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || _pressedAt == null) return;
        _pressedAt = null;
        // The click that brought the window to the front must not also play or pause (a drag still moves it)
        if (TakeActivationClick()) return;
        Clicked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows text; when <paramref name="hint"/> is true it is drawn as a hint (small, gray, no box).</summary>
    public void ShowText(string text, bool hint = false)
    {
        if (text == _text && hint == _hint) return;
        _text = text;
        _hint = hint;
        Invalidate();
    }

    static Font CreateFont(SubtitleStyle s)
    {
        float size = Math.Clamp(s.FontSize, 6f, 120f);
        try { return new Font(s.FontFamily, size, s.Bold ? FontStyle.Bold : FontStyle.Regular); }
        catch (ArgumentException)
        {
            try { return new Font(s.FontFamily, size, FontStyle.Regular); } // font family without a bold variant
            catch (ArgumentException) { return new Font("Segoe UI", size); }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (string.IsNullOrEmpty(_text)) return;

        const TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter |
                                      TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
        var font = _hint ? _hintFont : _font;
        var color = _hint ? Theme.TextDim : SubtitleStyle.ParseColor(_style.TextColor, Color.White);

        int maxWidth = Math.Max(LogicalToDeviceUnits(60), Width - 2 * LogicalToDeviceUnits(48));
        var size = TextRenderer.MeasureText(g, _text, font, new Size(maxWidth, int.MaxValue), flags);
        size.Width = Math.Min(size.Width + 2, maxWidth);

        int y = !_hint && _style.Position == SubtitlePosition.Bottom
            ? Height - size.Height - LogicalToDeviceUnits(36)
            : (Height - size.Height) / 2;
        var rect = new Rectangle((Width - size.Width) / 2, Math.Max(0, y), size.Width, size.Height);

        if (!_hint && _style.ShowBackground && _style.BackgroundOpacity > 0)
        {
            var bg = SubtitleStyle.ParseColor(_style.BackgroundColor, Color.Black);
            int alpha = Math.Clamp(_style.BackgroundOpacity, 0, 100) * 255 / 100;
            var box = Rectangle.Inflate(rect, LogicalToDeviceUnits(18), LogicalToDeviceUnits(8));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRect(box, LogicalToDeviceUnits(8));
            using var brush = new SolidBrush(Color.FromArgb(alpha, bg));
            g.FillPath(brush, path);
            g.SmoothingMode = SmoothingMode.None;
        }

        TextRenderer.DrawText(g, _text, font, rect, color, flags);
    }

    static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
            _hintFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
