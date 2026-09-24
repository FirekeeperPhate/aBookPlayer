using System.Runtime.InteropServices;

namespace aBookPlayer;

static class Theme
{
    public static readonly Color Back = Color.FromArgb(22, 22, 26);
    public static readonly Color Panel = Color.FromArgb(32, 32, 38);
    public static readonly Color Surface = Color.FromArgb(48, 48, 56);
    public static readonly Color Hover = Color.FromArgb(62, 62, 72);
    public static readonly Color Border = Color.FromArgb(64, 64, 74);
    public static readonly Color Track = Color.FromArgb(72, 72, 84);
    public static readonly Color Text = Color.FromArgb(232, 232, 238);
    public static readonly Color TextDim = Color.FromArgb(150, 150, 162);
    public static readonly Color Accent = Color.FromArgb(86, 160, 255);

    public static readonly string IconFontName = DetectIconFont();

    /// <summary>Application icon (all sizes), shared by every window.</summary>
    public static readonly Icon AppIcon = LoadAppIcon();

    static Icon LoadAppIcon()
    {
        using var stream = typeof(Theme).Assembly.GetManifestResourceStream("aBookPlayer.ico");
        return stream != null ? new Icon(stream) : SystemIcons.Application;
    }

    static string DetectIconFont()
    {
        try
        {
            using var family = new FontFamily("Segoe Fluent Icons");
            return family.Name;
        }
        catch (ArgumentException)
        {
            return "Segoe MDL2 Assets";
        }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    public static void UseDarkTitleBar(IntPtr handle)
    {
        int on = 1;
        DwmSetWindowAttribute(handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
    }

    public static void UseDarkScrollBars(Control control) =>
        SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
}

static class Glyphs
{
    public const string Play = "";
    public const string Pause = "";
    public const string Stop = "";
    public const string Previous = "";
    public const string Next = "";
    public const string Rewind = "";
    public const string FastForward = "";
    public const string Volume = "";
    public const string Mute = "";
}

sealed class IconButton : Button
{
    public IconButton(string glyph, float size = 14f, int width = 44, int height = 40)
    {
        SetStyle(ControlStyles.Selectable, false);
        Text = glyph;
        Font = new Font(Theme.IconFontName, size);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = Theme.Hover;
        FlatAppearance.MouseDownBackColor = Theme.Surface;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Size = new Size(width, height);
        Margin = new Padding(3, 0, 3, 0);
        TabStop = false;
        UseMnemonic = false;
        Cursor = Cursors.Hand;
    }

    protected override bool ShowFocusCues => false;
}

/// <summary>Dark-themed building blocks shared by the dialogs.</summary>
static class DialogControls
{
    public static Button MakeButton(string text, int width = 104, int height = 32)
    {
        var b = new Button
        {
            Text = text, FlatStyle = FlatStyle.Flat, BackColor = Theme.Surface, ForeColor = Theme.Text,
            Size = new Size(width, height), Margin = new Padding(8, 0, 0, 0), UseMnemonic = false,
        };
        b.FlatAppearance.BorderColor = Theme.Border;
        b.FlatAppearance.MouseOverBackColor = Theme.Hover;
        return b;
    }

    public static ComboBox MakeCombo(int width) => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
        BackColor = Theme.Surface, ForeColor = Theme.Text, Width = width,
    };

    /// <summary>Adds a "caption | control" row to a two-column settings grid.</summary>
    public static void AddRow(TableLayoutPanel grid, string caption, Control control)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label
        {
            Text = caption, AutoSize = true, ForeColor = Theme.TextDim,
            Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 20, 6),
        }, 0, row);
        control.Anchor = AnchorStyles.Left;
        control.Margin = new Padding(0, 5, 0, 5);
        grid.Controls.Add(control, 1, row);
    }
}

static class DarkToolTip
{
    public static ToolTip Create()
    {
        var tip = new ToolTip { OwnerDraw = true, BackColor = Theme.Surface, ForeColor = Theme.Text, InitialDelay = 400 };
        tip.Draw += (_, e) =>
        {
            e.DrawBackground();
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
            e.DrawText(TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
        return tip;
    }
}

sealed class DarkMenuRenderer() : ToolStripProfessionalRenderer(new DarkColorTable())
{
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextDim;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.Text;
        base.OnRenderArrow(e);
    }

    static readonly Font CheckFont = new(Theme.IconFontName, 9f);

    // The default check mark is a black bitmap, invisible on the dark background
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) =>
        TextRenderer.DrawText(e.Graphics, "", CheckFont, e.ImageRectangle, Theme.Accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

    sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color MenuStripGradientBegin => Theme.Panel;
        public override Color MenuStripGradientEnd => Theme.Panel;
        public override Color ToolStripDropDownBackground => Theme.Panel;
        public override Color ImageMarginGradientBegin => Theme.Panel;
        public override Color ImageMarginGradientMiddle => Theme.Panel;
        public override Color ImageMarginGradientEnd => Theme.Panel;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Theme.Hover;
        public override Color MenuItemSelected => Theme.Hover;
        public override Color MenuItemSelectedGradientBegin => Theme.Hover;
        public override Color MenuItemSelectedGradientEnd => Theme.Hover;
        public override Color MenuItemPressedGradientBegin => Theme.Surface;
        public override Color MenuItemPressedGradientMiddle => Theme.Surface;
        public override Color MenuItemPressedGradientEnd => Theme.Surface;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Panel;
    }
}
