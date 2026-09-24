using System.ComponentModel;
using static aBookPlayer.DialogControls;

namespace aBookPlayer;

/// <summary>Subtitle appearance options with a live preview.</summary>
sealed class OptionsForm : Form
{
    const string PreviewText = "This is a sample subtitle\nspanning two lines";

    readonly ComboBox _cmbFont = MakeCombo(260);
    readonly NumericUpDown _numSize = new()
    {
        Minimum = 8, Maximum = 96, Width = 70, BackColor = Theme.Surface, ForeColor = Theme.Text,
        BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Center,
    };
    readonly CheckBox _chkBold = new() { Text = "Bold", AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(16, 6, 0, 0) };
    readonly ColorButton _btnTextColor = new();
    readonly CheckBox _chkBackground = new() { Text = "Show a box behind the text", AutoSize = true, FlatStyle = FlatStyle.Flat };
    readonly ColorButton _btnBackColor = new();
    readonly SeekBar _opacity = new() { Maximum = 100, LiveUpdate = true, Width = 220, BackColor = Theme.Back };
    readonly Label _lblOpacity = new() { AutoSize = true, ForeColor = Theme.TextDim, Margin = new Padding(8, 7, 0, 0) };
    readonly ComboBox _cmbPosition = MakeCombo(160);
    readonly SubtitleView _preview = new() { Dock = DockStyle.Fill };
    bool _loading;

    public OptionsForm(SubtitleStyle current)
    {
        SuspendLayout();
        Text = "Subtitle Options";
        Icon = Theme.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(580, 580);

        _cmbFont.MaxDropDownItems = 16;
        _cmbFont.Items.AddRange(FontFamily.Families.Select(f => (object)f.Name).ToArray());
        _cmbPosition.Items.AddRange(["Center", "Bottom"]);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(18, 16, 18, 4) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddRow(grid, "Font", _cmbFont);
        AddRow(grid, "Size", Row(_numSize, new Label { Text = "pt", AutoSize = true, ForeColor = Theme.TextDim, Margin = new Padding(6, 7, 0, 0) }, _chkBold));
        AddRow(grid, "Text color", _btnTextColor);
        AddRow(grid, "Background", _chkBackground);
        AddRow(grid, "Background color", _btnBackColor);
        AddRow(grid, "Background opacity", Row(_opacity, _lblOpacity));
        AddRow(grid, "Position", _cmbPosition);

        // Preview
        var previewFrame = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Border, Padding = new Padding(1) };
        previewFrame.Controls.Add(_preview);
        var previewHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 4, 18, 8) };
        previewHost.Controls.Add(previewFrame);
        previewHost.Controls.Add(new Label
        {
            Text = "PREVIEW", Dock = DockStyle.Top, Height = 28, ForeColor = Theme.TextDim,
            Font = new Font("Segoe UI Semibold", 9f), Padding = new Padding(0, 8, 0, 0),
        });

        // Buttons
        var btnDefaults = MakeButton("Defaults");
        var btnOk = MakeButton("OK");
        var btnCancel = MakeButton("Cancel");
        btnOk.DialogResult = DialogResult.OK;
        btnCancel.DialogResult = DialogResult.Cancel;
        btnDefaults.Click += (_, _) => LoadStyle(new SubtitleStyle());
        btnOk.Click += (_, _) => Result = ReadStyle();
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        var buttons = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 4, Padding = new Padding(18, 8, 18, 16) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.Controls.Add(btnDefaults, 0, 0);
        buttons.Controls.Add(btnOk, 2, 0);
        buttons.Controls.Add(btnCancel, 3, 0);

        Controls.Add(previewHost);
        Controls.Add(buttons);
        Controls.Add(grid);

        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);
        PerformLayout();

        Result = current.Clone();
        LoadStyle(current);
        _preview.ShowText(PreviewText);

        _cmbFont.SelectedIndexChanged += (_, _) => UpdatePreview();
        _numSize.ValueChanged += (_, _) => UpdatePreview();
        _chkBold.CheckedChanged += (_, _) => UpdatePreview();
        _btnTextColor.ColorChanged += (_, _) => UpdatePreview();
        _chkBackground.CheckedChanged += (_, _) => UpdatePreview();
        _btnBackColor.ColorChanged += (_, _) => UpdatePreview();
        _opacity.ValueCommitted += (_, _) => UpdatePreview();
        _cmbPosition.SelectedIndexChanged += (_, _) => UpdatePreview();
    }

    public SubtitleStyle Result { get; private set; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.UseDarkTitleBar(Handle);
    }

    void LoadStyle(SubtitleStyle s)
    {
        _loading = true;
        int fontIndex = _cmbFont.FindStringExact(s.FontFamily);
        if (fontIndex < 0) fontIndex = _cmbFont.FindStringExact("Segoe UI");
        _cmbFont.SelectedIndex = fontIndex;
        _numSize.Value = (decimal)Math.Clamp(s.FontSize, (float)_numSize.Minimum, (float)_numSize.Maximum);
        _chkBold.Checked = s.Bold;
        _btnTextColor.Color = SubtitleStyle.ParseColor(s.TextColor, Color.White);
        _chkBackground.Checked = s.ShowBackground;
        _btnBackColor.Color = SubtitleStyle.ParseColor(s.BackgroundColor, Color.Black);
        _opacity.Value = Math.Clamp(s.BackgroundOpacity, 0, 100);
        _cmbPosition.SelectedIndex = s.Position == SubtitlePosition.Bottom ? 1 : 0;
        _loading = false;
        UpdatePreview();
    }

    SubtitleStyle ReadStyle() => new()
    {
        FontFamily = _cmbFont.SelectedItem as string ?? "Segoe UI",
        FontSize = (float)_numSize.Value,
        Bold = _chkBold.Checked,
        TextColor = SubtitleStyle.ToHex(_btnTextColor.Color),
        ShowBackground = _chkBackground.Checked,
        BackgroundColor = SubtitleStyle.ToHex(_btnBackColor.Color),
        BackgroundOpacity = (int)Math.Round(_opacity.Value),
        Position = _cmbPosition.SelectedIndex == 1 ? SubtitlePosition.Bottom : SubtitlePosition.Center,
    };

    void UpdatePreview()
    {
        if (_loading) return;
        var style = ReadStyle();
        _lblOpacity.Text = $"{style.BackgroundOpacity}%";
        _btnBackColor.Enabled = _opacity.Enabled = style.ShowBackground;
        _preview.SubtitleStyle = style;
    }

    static FlowLayoutPanel Row(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        flow.Controls.AddRange(controls);
        return flow;
    }
}

/// <summary>Button that shows a color swatch and opens the color picker.</summary>
sealed class ColorButton : Button
{
    Color _color = Color.White;

    public ColorButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderColor = Theme.Border;
        FlatAppearance.MouseOverBackColor = Theme.Hover;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Size = new Size(140, 32);
        Cursor = Cursors.Hand;
    }

    public event EventHandler? ColorChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Color
    {
        get => _color;
        set { _color = value; Invalidate(); }
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        using var dlg = new ColorDialog { Color = _color, FullOpen = true };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        Color = dlg.Color;
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int L(int v) => LogicalToDeviceUnits(v);
        var swatch = new Rectangle(L(8), (Height - L(18)) / 2, L(32), L(18));
        using (var brush = new SolidBrush(Enabled ? _color : Theme.Surface)) e.Graphics.FillRectangle(brush, swatch);
        using (var pen = new Pen(Theme.TextDim)) e.Graphics.DrawRectangle(pen, swatch);
        var textRect = new Rectangle(swatch.Right + L(8), 0, Width - swatch.Right - L(12), Height);
        TextRenderer.DrawText(e.Graphics, SubtitleStyle.ToHex(_color), Font, textRect,
            Enabled ? Theme.Text : Theme.TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }
}
