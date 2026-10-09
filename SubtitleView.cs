using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace aBookPlayer;

/// <summary>Subtitle area: centered, word-wrapped text with an optional background box.</summary>
sealed class SubtitleView : Control
{
    SubtitleStyle _style = new();
    Font _font, _secondFont;
    readonly Font _hintFont = new("Segoe UI", 12f);
    string _text = "";
    string? _second;
    bool _hint;

    public SubtitleView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Theme.Back;
        _font = CreateFont(_style);
        _secondFont = CreateSecondFont(_style);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public SubtitleStyle SubtitleStyle
    {
        get => _style.Clone();
        set
        {
            _style = value.Clone();
            var (old, oldSecond) = (_font, _secondFont);
            _font = CreateFont(_style);
            _secondFont = CreateSecondFont(_style);
            old.Dispose();
            oldSecond.Dispose();
            Invalidate();
        }
    }

    /// <summary>The subtitle on screen, or null when there is none (or only a hint is shown).</summary>
    public string? SubtitleText => _hint || string.IsNullOrWhiteSpace(_text) ? null : _text;

    /// <summary>Left click (the mouse may have moved a little while the button was down).</summary>
    public event EventHandler? Clicked;

    /// <summary>The left button is held and the mouse moved: the window should be dragged.</summary>
    public event EventHandler? DragStarted;

    Point? _pressedAt;
    long _pressedTick;
    /// <summary>Milliseconds, as <see cref="Environment.TickCount64"/> (another clock for the tests).</summary>
    internal Func<long> Clock = () => Environment.TickCount64;
    /// <summary>A button let go sooner than this was clicked, not held to drag.</summary>
    const int HoldMilliseconds = 180;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        // The second press of a double-click is not another click (it would undo the first one).
        // A click on the window while another one is in front counts too: it brings it forward and plays or pauses
        _pressedAt = e.Button == MouseButtons.Left && e.Clicks == 1 ? e.Location : null;
        _pressedTick = Clock();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressedAt is not { } start) return;
        // A click is seldom still, least of all one made on the way to a window behind another: the mouse is often
        // moving as the button goes down and up. So a drag is the button held a moment and the mouse moved a little
        // way (more than Windows' own few pixels, next to nothing on a dense screen), or moved far at once
        int moved = Math.Max(Math.Abs(e.X - start.X), Math.Abs(e.Y - start.Y));
        int little = Math.Max(SystemInformation.DragSize.Width, LogicalToDeviceUnits(6)), far = LogicalToDeviceUnits(48);
        if (moved > far || (moved > little && Clock() - _pressedTick >= HoldMilliseconds))
        {
            _pressedAt = null;
            DragStarted?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || _pressedAt == null) return;
        _pressedAt = null;
        Clicked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Shows text; when <paramref name="hint"/> is true it is drawn as a hint (small, gray, no box).
    /// <paramref name="second"/> (a translation) goes under it, smaller and a little dimmer.
    /// </summary>
    public void ShowText(string text, bool hint = false, string? second = null)
    {
        if (string.IsNullOrWhiteSpace(second) || hint) second = null;
        if (text == _text && hint == _hint && second == _second) return;
        _text = text;
        _hint = hint;
        _second = second;
        Invalidate();
    }

    /// <summary>The second subtitle on screen, if any.</summary>
    public string? SecondText => _second;

    static Font CreateSecondFont(SubtitleStyle s)
    {
        var main = CreateFont(s);
        try { return new Font(main.FontFamily, Math.Max(6f, main.Size * 0.72f), FontStyle.Regular); }
        finally { main.Dispose(); }
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
        // The translation alone (no line in the first subtitles right now) is still shown
        if (string.IsNullOrEmpty(_text) && _second == null) return;

        const TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter |
                                      TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
        var font = _hint ? _hintFont : _font;
        var color = _hint ? Theme.TextDim : SubtitleStyle.ParseColor(_style.TextColor, Color.White);

        int maxWidth = Math.Max(LogicalToDeviceUnits(60), Width - 2 * LogicalToDeviceUnits(48));
        var size = string.IsNullOrEmpty(_text) ? Size.Empty : TextRenderer.MeasureText(g, _text, font, new Size(maxWidth, int.MaxValue), flags);
        var secondSize = _second != null ? TextRenderer.MeasureText(g, _second, _secondFont, new Size(maxWidth, int.MaxValue), flags) : Size.Empty;
        int gap = size.Height > 0 && secondSize.Height > 0 ? LogicalToDeviceUnits(8) : 0;
        int width = Math.Min(Math.Max(size.Width, secondSize.Width) + 2, maxWidth), height = size.Height + gap + secondSize.Height;

        int y = !_hint && _style.Position == SubtitlePosition.Bottom
            ? Height - height - LogicalToDeviceUnits(36)
            : (Height - height) / 2;
        var all = new Rectangle((Width - width) / 2, Math.Max(0, y), width, height);

        if (!_hint && _style.ShowBackground && _style.BackgroundOpacity > 0)
        {
            var bg = SubtitleStyle.ParseColor(_style.BackgroundColor, Color.Black);
            int alpha = Math.Clamp(_style.BackgroundOpacity, 0, 100) * 255 / 100;
            var box = Rectangle.Inflate(all, LogicalToDeviceUnits(18), LogicalToDeviceUnits(8));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRect(box, LogicalToDeviceUnits(8));
            using var brush = new SolidBrush(Color.FromArgb(alpha, bg));
            g.FillPath(brush, path);
            g.SmoothingMode = SmoothingMode.None;
        }

        if (size.Height > 0) TextRenderer.DrawText(g, _text, font, new Rectangle(all.X, all.Y, all.Width, size.Height), color, flags);
        if (_second != null)
        {
            // The translation in the same color, a little dimmer, so the original stays the one read first
            var dim = Color.FromArgb((color.R * 3 + BackColor.R) / 4, (color.G * 3 + BackColor.G) / 4, (color.B * 3 + BackColor.B) / 4);
            TextRenderer.DrawText(g, _second, _secondFont, new Rectangle(all.X, all.Y + size.Height + gap, all.Width, secondSize.Height), dim, flags);
        }
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
            _secondFont.Dispose();
            _hintFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
