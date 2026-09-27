using static aBookPlayer.DialogControls;

namespace aBookPlayer;

/// <summary>Common look of the app's dialogs: dark theme, app icon, centered on the owner, buttons at the bottom right.</summary>
class DarkDialog : Form
{
    protected readonly FlowLayoutPanel Buttons = new()
    {
        Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 8, 16, 14),
    };

    public DarkDialog(string title, Size clientSize, bool resizable = false)
    {
        Text = title;
        Icon = Theme.AppIcon;
        FormBorderStyle = resizable ? FormBorderStyle.Sizable : FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = clientSize;
        Controls.Add(Buttons);
    }

    /// <summary>
    /// Sizes are in 96-DPI pixels. WinForms scales them only when layout resumes after AutoScaleMode is set,
    /// so it is done here, once the derived dialog has added all its controls.
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);
        PerformLayout();
        if (FormBorderStyle == FormBorderStyle.Sizable) MinimumSize = Size;
        base.OnLoad(e);
    }

    protected Button AddButton(string text, DialogResult result = DialogResult.None, int width = 104)
    {
        var button = MakeButton(text, width);
        button.DialogResult = result;
        Buttons.Controls.Add(button);
        return button;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.UseDarkTitleBar(Handle);
    }

    public static TextBox MakeTextBox() => new()
    {
        BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10.5f),
    };
}

/// <summary>Owner-drawn dark list with rows drawn by a callback (used by bookmarks, search and library).</summary>
sealed class DarkList : ListBox
{
    public DarkList(int rowHeight)
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        IntegralHeight = false;
        LogicalRowHeight = rowHeight;
        ItemHeight = rowHeight;
        DpiChangedAfterParent += (_, _) => ItemHeight = LogicalToDeviceUnits(LogicalRowHeight);
        HandleCreated += (_, _) =>
        {
            ItemHeight = LogicalToDeviceUnits(LogicalRowHeight);
            Theme.UseDarkScrollBars(this);
        };
    }

    int LogicalRowHeight { get; }

    /// <summary>Draws the content of a row (background already painted).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<Graphics, Rectangle, int>? DrawRow { get; set; }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(selected ? Theme.Surface : Theme.Panel))
            e.Graphics.FillRectangle(bg, e.Bounds);
        DrawRow?.Invoke(e.Graphics, e.Bounds, e.Index);
    }

    public int L(int value) => LogicalToDeviceUnits(value);
}

/// <summary>Asks for one line of text (e.g. a bookmark's note).</summary>
sealed class InputDialog : DarkDialog
{
    readonly TextBox _text = MakeTextBox();

    InputDialog(string title, string prompt, string value) : base(title, new Size(460, 150))
    {
        var label = new Label { Text = prompt, AutoSize = true, ForeColor = Theme.TextDim, Location = new Point(18, 18) };
        _text.Text = value;
        _text.SetBounds(18, 44, 424, 28);
        Controls.Add(label);
        Controls.Add(_text);
        AcceptButton = AddButton("OK", DialogResult.OK);
        CancelButton = AddButton("Cancel", DialogResult.Cancel);
        ActiveControl = _text; // type the note right away (the buttons come first in the tab order)
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _text.Focus();
        _text.SelectAll();
    }

    public static string? Ask(IWin32Window owner, string title, string prompt, string value = "")
    {
        using var dlg = new InputDialog(title, prompt, value);
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg._text.Text.Trim() : null;
    }
}

/// <summary>The book's bookmarks: jump to one, rename or delete it.</summary>
sealed class BookmarksForm : DarkDialog
{
    readonly List<Bookmark> _bookmarks;
    readonly Func<double, string> _chapterAt;
    readonly DarkList _list = new(48) { Dock = DockStyle.Fill };

    /// <summary>The bookmark to jump to, when the dialog closes with OK.</summary>
    public Bookmark? Selected { get; private set; }

    public BookmarksForm(List<Bookmark> bookmarks, Func<double, string> chapterAt) : base("Bookmarks", new Size(560, 440), resizable: true)
    {
        _bookmarks = bookmarks;
        _chapterAt = chapterAt;
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 16, 16, 4) };
        host.Controls.Add(_list);
        Controls.Add(host);
        host.BringToFront();

        var go = AddButton("Go to", DialogResult.None);
        var close = AddButton("Close", DialogResult.Cancel);
        var delete = AddButton("Delete");
        var rename = AddButton("Edit note…", width: 110);
        CancelButton = close;
        go.Click += (_, _) => Go();
        _list.DoubleClick += (_, _) => Go();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Go(); e.Handled = true; }
            if (e.KeyCode == Keys.Delete) { Delete(); e.Handled = true; }
        };
        delete.Click += (_, _) => Delete();
        rename.Click += (_, _) => Rename();
        _list.DrawRow = DrawBookmark;
        Refill();
    }

    void Refill(int select = 0)
    {
        _list.Items.Clear();
        foreach (var b in _bookmarks) _list.Items.Add(b);
        if (_list.Items.Count == 0) _list.Items.Add("No bookmarks yet: press B while listening to add one.");
        else _list.SelectedIndex = Math.Clamp(select, 0, _list.Items.Count - 1);
    }

    Bookmark? Current => _list.SelectedItem as Bookmark;

    void Go()
    {
        if (Current is not { } b) return;
        Selected = b;
        DialogResult = DialogResult.OK;
    }

    void Delete()
    {
        if (Current is not { } b) return;
        int index = _list.SelectedIndex;
        _bookmarks.Remove(b);
        Refill(index);
    }

    void Rename()
    {
        if (Current is not { } b) return;
        var note = InputDialog.Ask(this, "Edit bookmark", "Note:", b.Note);
        if (note == null) return;
        b.Note = note;
        _list.Invalidate();
    }

    void DrawBookmark(Graphics g, Rectangle r, int index)
    {
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        if (_list.Items[index] is not Bookmark b)
        {
            TextRenderer.DrawText(g, _list.Items[index].ToString(), _list.Font, r, Theme.TextDim, flags | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            return;
        }
        int pad = _list.L(10);
        var time = TimeSpan.FromSeconds(b.Seconds);
        var timeText = time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
        var top = new Rectangle(r.X + pad, r.Y + _list.L(5), r.Width - 2 * pad, r.Height / 2 - _list.L(3));
        var bottom = new Rectangle(r.X + pad, r.Y + r.Height / 2, r.Width - 2 * pad, r.Height / 2 - _list.L(5));
        using var bold = new Font(_list.Font, FontStyle.Bold);
        TextRenderer.DrawText(g, b.Note.Length > 0 ? b.Note : "(no note)", bold, top, b.Note.Length > 0 ? Theme.Text : Theme.TextDim, flags);
        TextRenderer.DrawText(g, $"{timeText}  ·  {_chapterAt(b.Seconds)}", _list.Font, bottom, Theme.TextDim, flags);
    }
}

/// <summary>Searches the subtitles (or transcript) and jumps to where a phrase is spoken.</summary>
sealed class SearchForm : DarkDialog
{
    readonly IReadOnlyList<SubtitleCue> _cues;
    readonly TextBox _query = MakeTextBox();
    readonly DarkList _results = new(46) { Dock = DockStyle.Fill };
    readonly Label _count = new() { Dock = DockStyle.Top, Height = 26, ForeColor = Theme.TextDim, Padding = new Padding(0, 6, 0, 0) };
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 200 };

    public TimeSpan? Selected { get; private set; }

    /// <summary>The words searched, to offer them again next time.</summary>
    public string Query => _query.Text;

    public SearchForm(IReadOnlyList<SubtitleCue> cues, string initial) : base("Search in subtitles", new Size(620, 520), resizable: true)
    {
        _cues = cues;
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 16, 16, 4) };
        _query.Dock = DockStyle.Top;
        _query.PlaceholderText = "Words to find";
        host.Controls.Add(_results);
        host.Controls.Add(_count);
        host.Controls.Add(_query);
        Controls.Add(host);
        host.BringToFront();

        var go = AddButton("Go to");
        CancelButton = AddButton("Close", DialogResult.Cancel);
        go.Click += (_, _) => Go();
        _results.DoubleClick += (_, _) => Go();
        _results.DrawRow = DrawResult;
        _query.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Search(); };
        _query.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Down && _results.Items.Count > 0) { _results.Focus(); e.Handled = true; }
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; if (_results.SelectedIndex >= 0) Go(); else Search(); }
        };
        _results.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { Go(); e.Handled = true; } };
        _query.Text = initial;
        Search();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _query.Focus();
        _query.SelectAll();
    }

    void Search()
    {
        _results.BeginUpdate();
        _results.Items.Clear();
        var words = _query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length > 0)
            foreach (var cue in _cues)
                if (words.All(w => cue.Text.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
                    _results.Items.Add(cue);
        _results.EndUpdate();
        _count.Text = words.Length == 0 ? "" : _results.Items.Count switch
        {
            0 => "Not found.",
            1 => "1 match",
            var n => $"{n} matches",
        };
        if (_results.Items.Count > 0) _results.SelectedIndex = 0;
    }

    void Go()
    {
        if (_results.SelectedItem is not SubtitleCue cue) return;
        Selected = cue.Start;
        DialogResult = DialogResult.OK;
    }

    void DrawResult(Graphics g, Rectangle r, int index)
    {
        if (_results.Items[index] is not SubtitleCue cue) return;
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis;
        int pad = _results.L(10), timeWidth = _results.L(70);
        var t = cue.Start;
        var timeText = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        TextRenderer.DrawText(g, timeText, _results.Font, new Rectangle(r.X + pad, r.Y + _results.L(6), timeWidth, r.Height), Theme.Accent,
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(g, cue.Text.Replace('\n', ' '), _results.Font,
            new Rectangle(r.X + pad + timeWidth, r.Y + _results.L(6), r.Width - timeWidth - 2 * pad, r.Height - _results.L(8)), Theme.Text, flags);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _debounce.Dispose();
        base.Dispose(disposing);
    }
}
