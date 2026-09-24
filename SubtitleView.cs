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
