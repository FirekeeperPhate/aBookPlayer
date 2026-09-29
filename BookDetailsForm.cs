using static aBookPlayer.DialogControls;

namespace aBookPlayer;

/// <summary>
/// Title, author, series and cover of a book, when its files' tags are wrong or missing. The edited details stay
/// (the tags no longer replace them) until "Use the file's details" is chosen.
/// </summary>
sealed class BookDetailsForm : DarkDialog
{
    readonly TextBox _title = MakeTextBox(), _author = MakeTextBox(), _series = MakeTextBox();
    readonly NumericUpDown _number = new()
    {
        Minimum = 0, Maximum = 9999, Width = 80, BackColor = Theme.Surface, ForeColor = Theme.Text,
        BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Center,
    };
    readonly PictureBox _cover = new() { Size = new Size(160, 160), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Surface };
    readonly Image? _fileCover;   // the file's own cover, for "Use the file's cover" (owned by the dialog)

    /// <summary>The picture chosen, or null to use the file's own again; only meaningful when <see cref="CoverChanged"/>.</summary>
    public byte[]? Cover { get; private set; }
    public bool CoverChanged { get; private set; }
    /// <summary>Forget the edits: the file's tags (and cover) are used again.</summary>
    public bool UseFileDetails { get; private set; }

    public string BookTitle => _title.Text.Trim();
    public string? Author => NullIfEmpty(_author.Text);
    public string? Series => NullIfEmpty(_series.Text);
    public int? Number => _number.Value > 0 && Series != null ? (int)_number.Value : null;

    static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public BookDetailsForm(string title, string? author, string? series, int? number, Image? cover, Image? fileCover, bool edited)
        : base("Edit details", new Size(600, 340))
    {
        _title.Text = title;
        _author.Text = author ?? "";
        _series.Text = series ?? "";
        _number.Value = Math.Clamp(number ?? 0, 0, 9999);
        _cover.Image = cover;
        _fileCover = fileCover;

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(18, 16, 18, 4) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        void Row(int row, string caption, Control control)
        {
            grid.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = Theme.TextDim, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 14, 8) }, 0, row);
            control.Anchor = control is TextBox ? AnchorStyles.Left | AnchorStyles.Right : AnchorStyles.Left;
            control.Margin = new Padding(0, 4, 14, 4);
            grid.Controls.Add(control, 1, row);
        }
        Row(0, "Title", _title);
        Row(1, "Author", _author);
        Row(2, "Series", _series);
        var numberRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        numberRow.Controls.Add(_number);
        numberRow.Controls.Add(new Label { Text = "0 = no number", AutoSize = true, ForeColor = Theme.TextDim, Margin = new Padding(8, 6, 0, 0) });
        Row(3, "Number", numberRow);

        var coverBox = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(4, 4, 0, 0) };
        var choose = MakeButton("Choose…", 160, 30);
        var remove = MakeButton("Use the file's cover", 160, 30);
        choose.Margin = remove.Margin = new Padding(0, 6, 0, 0);
        coverBox.Controls.AddRange([_cover, choose, remove]);
        grid.Controls.Add(coverBox, 2, 0);
        grid.SetRowSpan(coverBox, 5);
        Controls.Add(grid);
        grid.BringToFront();

        var ok = AddButton("OK", DialogResult.OK);
        CancelButton = AddButton("Cancel", DialogResult.Cancel);
        var reset = AddButton("Use the file's details", width: 170);
        reset.Enabled = edited;
        AcceptButton = ok;

        choose.Click += (_, _) => ChooseCover();
        remove.Click += (_, _) => SetCover(null, fileCover != null ? new Bitmap(fileCover) : null);
        reset.Click += (_, _) =>
        {
            UseFileDetails = true;
            DialogResult = DialogResult.OK;
        };
        ok.Click += (_, _) =>
        {
            if (BookTitle.Length > 0) return;
            MessageBox.Show(this, "The title cannot be empty.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.None;
        };
    }

    void ChooseCover()
    {
        using var dlg = new OpenFileDialog { Title = "Cover picture", Filter = "Pictures (*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var bytes = File.ReadAllBytes(dlg.FileName);
            var image = CoverArt.ToImage(bytes) ?? throw new InvalidDataException("Not a picture this app can read.");
            SetCover(bytes, image);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not use this picture:\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void SetCover(byte[]? bytes, Image? image)
    {
        var old = _cover.Image;
        _cover.Image = image;
        old?.Dispose();
        Cover = bytes;
        CoverChanged = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cover.Image?.Dispose();
            _fileCover?.Dispose();
        }
        base.Dispose(disposing);
    }
}
