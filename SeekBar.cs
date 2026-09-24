using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace aBookPlayer;

/// <summary>Custom-drawn seek/volume bar with chapter marks.</summary>
sealed class SeekBar : Control
{
    readonly ToolTip _tip = DarkToolTip.Create();
    double _maximum = 1;
    double _value;
    bool _dragging, _hover;
    string? _lastTip;
    int _lastTipX = int.MinValue;
    IReadOnlyList<double> _marks = [];

    public SeekBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Theme.Panel;
        Cursor = Cursors.Hand;
        Height = 30;
    }

    /// <summary>Raised when the mouse is released (or while dragging when LiveUpdate is set).</summary>
    public event EventHandler<double>? ValueCommitted;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool LiveUpdate { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<double, string>? HoverText { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Maximum
    {
        get => _maximum;
        set { _maximum = Math.Max(value, 0.0001); _value = Math.Min(_value, _maximum); Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Value
    {
        get => _value;
        set
        {
            if (_dragging) return;
            var v = Math.Clamp(value, 0, _maximum);
            if (v == _value) return;
            _value = v;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<double> Marks
    {
        get => _marks;
        set { _marks = value; Invalidate(); }
    }

    int TrackLeft => LogicalToDeviceUnits(8);
    int TrackWidth => Math.Max(1, Width - 2 * TrackLeft);

    double ValueFromX(int x) => Math.Clamp((x - TrackLeft) / (double)TrackWidth, 0, 1) * _maximum;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        Capture = true;
        _value = ValueFromX(e.X);
        if (LiveUpdate) ValueCommitted?.Invoke(this, _value);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            _value = ValueFromX(e.X);
            if (LiveUpdate) ValueCommitted?.Invoke(this, _value);
            Invalidate();
        }
        if (HoverText != null)
        {
            var text = HoverText(ValueFromX(e.X));
            if (text != _lastTip || Math.Abs(e.X - _lastTipX) > 6)
            {
                _lastTip = text;
                _lastTipX = e.X;
                _tip.Show(text, this, e.X + LogicalToDeviceUnits(10), -LogicalToDeviceUnits(30));
            }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
        ValueCommitted?.Invoke(this, _value);
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _lastTip = null;
        _tip.Hide(this);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        bool active = _hover || _dragging;
        float thickness = LogicalToDeviceUnits(active ? 6 : 4);
        float cy = Height / 2f;
        float left = TrackLeft, right = TrackLeft + TrackWidth;
        float x = left + (float)(_value / _maximum) * TrackWidth;

        using (var pen = new Pen(Theme.Track, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pen, left, cy, right, cy);

        if (x > left)
        {
            using var pen = new Pen(Enabled ? Theme.Accent : Theme.TextDim, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, left, cy, x, cy);
        }

        // Chapter marks: small gaps in the bar
        using (var gap = new SolidBrush(BackColor))
        {
            float w = LogicalToDeviceUnits(2);
            foreach (var m in _marks)
            {
                if (m <= 0 || m >= _maximum) continue;
                float mx = left + (float)(m / _maximum) * TrackWidth;
                g.FillRectangle(gap, mx - w / 2, cy - thickness, w, thickness * 2);
            }
        }

        float r = LogicalToDeviceUnits(active ? 8 : 6);
        using var thumb = new SolidBrush(!Enabled ? Theme.TextDim : active ? Color.White : Theme.Text);
        g.FillEllipse(thumb, x - r, cy - r, 2 * r, 2 * r);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
