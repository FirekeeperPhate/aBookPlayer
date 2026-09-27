using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using static aBookPlayer.DialogControls;

namespace aBookPlayer;

/// <summary>Small cover pictures kept for the library, so it can show them without opening every book.</summary>
static class LibraryCovers
{
    const int Size = 160;
    static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppSettings.AppName, "covers");

    static string FileFor(string bookPath)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(bookPath).ToLowerInvariant()));
        return Path.Combine(Folder, Convert.ToHexString(hash)[..20] + ".jpg");
    }

    public static void Save(string bookPath, byte[]? cover)
    {
        try
        {
            var file = FileFor(bookPath);
            using var image = CoverArt.ToImage(cover);
            if (image == null)
            {
                if (File.Exists(file)) File.Delete(file);
                return;
            }
            Directory.CreateDirectory(Folder);
            using var thumb = Thumbnail(image, Size);
            thumb.Save(file, ImageFormat.Jpeg);
        }
        catch { /* only a thumbnail */ }
    }

    public static Image? Load(string bookPath)
    {
        try
        {
            var file = FileFor(bookPath);
            if (!File.Exists(file)) return null;
            using var stream = new MemoryStream(File.ReadAllBytes(file));
            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }
        catch { return null; }
    }

    public static Bitmap Thumbnail(Image image, int size)
    {
        double scale = Math.Min(size / (double)image.Width, size / (double)image.Height);
        int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale));
        var thumb = new Bitmap(w, h);
        using var g = Graphics.FromImage(thumb);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(image, 0, 0, w, h);
        return thumb;
    }
}

/// <summary>A book as listed in the library.</summary>
sealed class LibraryEntry
{
    public required string Path { get; init; }
    public BookState? State { get; set; }
    public string Title => State?.Title ?? BookSource.NameFromPath(Path);
    public string? Author => State?.Author;
    public Image? Cover { get; set; }
    public bool Missing { get; set; }

    /// <summary>0..1, or null when it has never been opened (length unknown).</summary>
    public double? Progress => State is { DurationSeconds: > 0 } s ? Math.Clamp(s.PositionSeconds / s.DurationSeconds, 0, 1) : null;

    public LibraryStatus Status =>
        State is { Finished: true } ? LibraryStatus.Finished
        : State is { PositionSeconds: > 1 } ? LibraryStatus.InProgress
        : LibraryStatus.NotStarted;
}

enum LibraryStatus { InProgress, NotStarted, Finished }

/// <summary>Finds books in the library folders: book folders, single-file books, and "CD 1/CD 2" sets.</summary>
static class LibraryScanner
{
    static readonly string[] DiscWords = ["cd", "disc", "disk", "part", "parte", "vol", "volume"];

    public static List<string> Scan(IEnumerable<string> roots, CancellationToken ct)
    {
        var books = new List<string>();
        foreach (var root in roots)
        {
            try { if (Directory.Exists(root)) ScanFolder(root, books, ct, depth: 0); }
            catch (OperationCanceledException) { throw; }
            catch { /* unreadable folder: skip it */ }
        }
        return books;
    }

    static void ScanFolder(string folder, List<string> books, CancellationToken ct, int depth)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > 6) return;
        var files = Directory.EnumerateFiles(folder).Where(AudioFormats.IsSupported).ToList();
        var subfolders = Directory.EnumerateDirectories(folder).ToList();
        bool discFolders = subfolders.Count > 0 && subfolders.All(IsDiscFolder);

        if (depth > 0 && (discFolders && files.Count == 0 || files.Count > 1 && !files.All(IsChapteredFile)))
        {
            books.Add(folder); // chapters as separate files (possibly in CD subfolders): one book
            return;
        }
        books.AddRange(files); // single files (e.g. .m4b audiobooks), each a book
        foreach (var sub in subfolders) ScanFolder(sub, books, ct, depth + 1);
    }

    static bool IsDiscFolder(string path)
    {
        var name = System.IO.Path.GetFileName(path).ToLowerInvariant();
        return DiscWords.Any(w => name.StartsWith(w, StringComparison.Ordinal)) && name.Any(char.IsDigit);
    }

    /// <summary>A whole book in one file: M4B, or anything big enough (over ~150 MB) to be more than a chapter.</summary>
    static bool IsChapteredFile(string path) =>
        System.IO.Path.GetExtension(path).Equals(".m4b", StringComparison.OrdinalIgnoreCase) || new FileInfo(path).Length > 150L * 1024 * 1024;
}

/// <summary>All the books: those listened to (with their progress) and those found in the library folders.</summary>
sealed class LibraryForm : DarkDialog
{
    readonly AppSettings _settings;
    readonly DarkList _list = new(84) { Dock = DockStyle.Fill };
    readonly TextBox _filter = MakeTextBox();
    readonly ComboBox _show = MakeCombo(160);
    readonly Label _status = new() { AutoSize = true, ForeColor = Theme.TextDim, Margin = new Padding(12, 9, 0, 0) };
    readonly List<LibraryEntry> _all = [];
    readonly CancellationTokenSource _cts = new();
    readonly string? _openBook;

    /// <summary>The book to open, when the dialog closes with OK.</summary>
    public string? Selected { get; private set; }

    public LibraryForm(AppSettings settings, string? openBook) : base("Library", new Size(760, 600), resizable: true)
    {
        _settings = settings;
        _openBook = openBook != null ? Path.GetFullPath(openBook) : null;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(16, 14, 16, 8) };
        _filter.Width = 260;
        _filter.PlaceholderText = "Search title or author";
        _show.Items.AddRange(["All books", "In progress", "Not started", "Finished"]);
        _show.SelectedIndex = 0;
        _show.Margin = new Padding(10, 1, 0, 0);
        var folders = MakeButton("Folders…", 100);
        folders.Margin = new Padding(10, 0, 0, 0);
        top.Controls.AddRange([_filter, _show, folders, _status]);

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 0, 16, 4) };
        host.Controls.Add(_list);
        Controls.Add(host);
        Controls.Add(top);
        host.BringToFront();

        var open = AddButton("Open");
        CancelButton = AddButton("Close", DialogResult.Cancel);
        var remove = AddButton("Forget", width: 96);
        var finished = AddButton("Mark finished", width: 124);

        open.Click += (_, _) => Open();
        _list.DoubleClick += (_, _) => Open();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Open(); e.Handled = true; }
            if (e.KeyCode == Keys.Delete) { Forget(); e.Handled = true; }
        };
        _list.SelectedIndexChanged += (_, _) =>
            finished.Text = Current?.Status == LibraryStatus.Finished ? "Mark unfinished" : "Mark finished";
        remove.Click += (_, _) => Forget();
        finished.Click += (_, _) => ToggleFinished();
        folders.Click += (_, _) => EditFolders();
        _filter.TextChanged += (_, _) => Refill();
        _show.SelectedIndexChanged += (_, _) => Refill();
        _list.DrawRow = DrawEntry;

        foreach (var (path, state) in _settings.Books)
            _all.Add(new LibraryEntry { Path = path, State = state, Cover = LibraryCovers.Load(path) });
        Refill();
        _ = LoadInBackgroundAsync();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _list.Focus();
    }

    /// <summary>Adds the books found in the library folders and marks the ones that are no longer there.</summary>
    async Task LoadInBackgroundAsync()
    {
        _status.Text = _settings.LibraryFolders.Count > 0 ? "Looking for books…" : "";
        var known = _all.Select(b => b.Path).ToList();
        var roots = _settings.LibraryFolders.ToList();
        var ct = _cts.Token;
        try
        {
            var (found, missing) = await Task.Run(() =>
            {
                var books = LibraryScanner.Scan(roots, ct);
                var gone = known.Where(p => !BookSource.Exists(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return (books, gone);
            }, ct);
            if (IsDisposed) return;
            foreach (var entry in _all) entry.Missing = missing.Contains(entry.Path);
            var present = _all.Select(b => b.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = found.Where(present.Add).Select(p => new LibraryEntry { Path = p }).ToList();
            _all.AddRange(added);
            _status.Text = "";
            Refill();

            // Covers of books never opened: embedded picture or an image in their folder
            foreach (var entry in added)
            {
                var path = entry.Path;
                entry.Cover = await Task.Run(() => LibraryCovers.Load(path) ?? FirstCover(path), ct);
                if (IsDisposed) return;
                _list.Invalidate();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed) _status.Text = "Could not scan: " + ex.Message;
        }
    }

    static Image? FirstCover(string path)
    {
        try
        {
            var first = BookSource.IsFolder(path) ? BookSource.PartsOf(path).FirstOrDefault() : path;
            if (first == null) return null;
            bool folder = BookSource.IsFolder(path);
            var bytes = MediaMetadata.Read(first).Cover ?? CoverArt.FromFolder(folder ? path : System.IO.Path.GetDirectoryName(path), bookFolder: folder);
            LibraryCovers.Save(path, bytes);
            return LibraryCovers.Load(path);
        }
        catch { return null; }
    }

    void Refill()
    {
        var selected = Current?.Path;
        var words = _filter.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wanted = _show.SelectedIndex switch
        {
            1 => LibraryStatus.InProgress,
            2 => LibraryStatus.NotStarted,
            3 => LibraryStatus.Finished,
            _ => (LibraryStatus?)null,
        };
        var rows = _all
            .Where(b => wanted == null || b.Status == wanted)
            .Where(b => words.All(w => b.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase)
                                       || (b.Author?.Contains(w, StringComparison.CurrentCultureIgnoreCase) ?? false)))
            // Most recently listened first, then the others by title
            .OrderByDescending(b => b.State?.LastOpened ?? DateTime.MinValue)
            .ThenBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in rows) _list.Items.Add(r);
        if (rows.Count == 0)
            _list.Items.Add(_all.Count == 0 ? "No books yet. Add the folders where you keep your audiobooks with Folders…" : "No books match.");
        _list.EndUpdate();
        int index = rows.FindIndex(r => string.Equals(r.Path, selected, StringComparison.OrdinalIgnoreCase));
        if (rows.Count > 0) _list.SelectedIndex = Math.Max(0, index);
    }

    LibraryEntry? Current => _list.SelectedItem as LibraryEntry;

    void Open()
    {
        if (Current is not { } entry) return;
        if (entry.Missing)
        {
            MessageBox.Show(this, $"\"{entry.Path}\" cannot be found (moved, deleted, or its drive is not connected).", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Selected = entry.Path;
        DialogResult = DialogResult.OK;
    }

    void Forget()
    {
        if (Current is not { State: not null } entry) return;
        if (string.Equals(entry.Path, _openBook, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "This is the book that is open now: open another one first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, $"Forget \"{entry.Title}\", with its position and bookmarks?\n\nThe audio files are not touched.", Text,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK)
            return;
        _settings.Books.Remove(entry.Path);
        entry.State = null;
        bool inFolders = _settings.LibraryFolders.Any(f => entry.Path.StartsWith(f, StringComparison.OrdinalIgnoreCase));
        if (!inFolders) _all.Remove(entry);
        Refill();
    }

    void ToggleFinished()
    {
        if (Current is not { } entry) return;
        entry.State ??= _settings.Books[entry.Path] = new BookState { Title = entry.Title };
        entry.State.Finished = !entry.State.Finished;
        if (entry.State.Finished) entry.State.PositionSeconds = 0;
        entry.State.PositionUpdated = DateTime.UtcNow;
        Refill();
    }

    void EditFolders()
    {
        using var dlg = new LibraryFoldersForm(_settings.LibraryFolders);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _settings.LibraryFolders = dlg.Folders;
        // Rescan: drop the entries that only came from the folders
        _all.RemoveAll(b => b.State == null);
        Refill();
        _ = LoadInBackgroundAsync();
    }

    void DrawEntry(Graphics g, Rectangle r, int index)
    {
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        if (_list.Items[index] is not LibraryEntry b)
        {
            TextRenderer.DrawText(g, _list.Items[index].ToString(), _list.Font, r, Theme.TextDim,
                flags | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            return;
        }
        int pad = _list.L(10), coverSize = r.Height - 2 * _list.L(8);
        var coverRect = new Rectangle(r.X + pad, r.Y + _list.L(8), coverSize, coverSize);
        if (b.Cover != null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            double scale = Math.Min(coverSize / (double)b.Cover.Width, coverSize / (double)b.Cover.Height);
            int w = (int)(b.Cover.Width * scale), h = (int)(b.Cover.Height * scale);
            g.DrawImage(b.Cover, coverRect.X + (coverSize - w) / 2, coverRect.Y + (coverSize - h) / 2, w, h);
        }
        else
        {
            using var placeholder = new SolidBrush(Theme.Surface);
            g.FillRectangle(placeholder, coverRect);
            using var icon = new Font(Theme.IconFontName, coverSize / 3f, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, "", icon, coverRect, Theme.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        int x = coverRect.Right + _list.L(14), width = r.Right - x - pad;
        var textColor = b.Missing ? Theme.TextDim : Theme.Text;
        using var bold = new Font(_list.Font.FontFamily, _list.Font.Size + 1, FontStyle.Bold);
        TextRenderer.DrawText(g, b.Title, bold, new Rectangle(x, r.Y + _list.L(8), width, _list.L(24)), textColor, flags);

        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(b.Author)) details.Add(b.Author);
        if (b.State is { DurationSeconds: > 0 } s) details.Add(FormatLength(TimeSpan.FromSeconds(s.DurationSeconds)));
        if (b.Missing) details.Add("not found");
        TextRenderer.DrawText(g, string.Join("  ·  ", details), _list.Font, new Rectangle(x, r.Y + _list.L(32), width, _list.L(20)), Theme.TextDim, flags);

        // Progress
        var status = b.Status switch
        {
            LibraryStatus.Finished => "Finished",
            LibraryStatus.NotStarted => "Not started",
            _ when b.Progress is { } p => $"{p:P0}  ·  {FormatLength(TimeSpan.FromSeconds(b.State!.DurationSeconds * (1 - p)))} left",
            _ => "In progress",
        };
        int barY = r.Y + _list.L(62), barWidth = Math.Min(_list.L(220), width);
        float thickness = _list.L(4);
        using (var track = new Pen(Theme.Track, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawLine(track, x + thickness / 2, barY, x + barWidth - thickness / 2, barY);
            double fill = b.Status == LibraryStatus.Finished ? 1 : b.Progress ?? 0;
            if (fill > 0)
            {
                using var accent = new Pen(b.Status == LibraryStatus.Finished ? Theme.TextDim : Theme.Accent, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(accent, x + thickness / 2, barY, x + thickness / 2 + (float)fill * (barWidth - thickness), barY);
            }
            g.SmoothingMode = SmoothingMode.None;
        }
        TextRenderer.DrawText(g, status, _list.Font, new Rectangle(x + barWidth + _list.L(12), barY - _list.L(10), width - barWidth - _list.L(12), _list.L(20)),
            Theme.TextDim, flags);
    }

    static string FormatLength(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
            foreach (var b in _all) b.Cover?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>The folders the library looks into.</summary>
sealed class LibraryFoldersForm : DarkDialog
{
    readonly DarkList _list = new(36) { Dock = DockStyle.Fill };

    public List<string> Folders { get; }

    public LibraryFoldersForm(List<string> folders) : base("Library folders", new Size(560, 360), resizable: true)
    {
        Folders = [.. folders];
        var info = new Label
        {
            Dock = DockStyle.Top, Height = 44, ForeColor = Theme.TextDim, Padding = new Padding(0, 0, 0, 8),
            Text = "Books in these folders appear in the library: audiobook files (like .m4b) and folders of chapter files.",
        };
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 14, 16, 4) };
        host.Controls.Add(_list);
        host.Controls.Add(info);
        Controls.Add(host);
        host.BringToFront();

        AcceptButton = AddButton("OK", DialogResult.OK);
        CancelButton = AddButton("Cancel", DialogResult.Cancel);
        var remove = AddButton("Remove");
        var add = AddButton("Add…");
        add.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Folder with your audiobooks", UseDescriptionForTitle = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            if (!Folders.Contains(dlg.SelectedPath, StringComparer.OrdinalIgnoreCase)) Folders.Add(dlg.SelectedPath);
            Refill();
        };
        remove.Click += (_, _) =>
        {
            if (_list.SelectedItem is string f && Folders.Remove(f)) Refill();
        };
        _list.DrawRow = (g, r, i) =>
            TextRenderer.DrawText(g, _list.Items[i].ToString(), _list.Font, Rectangle.Inflate(r, -_list.L(10), 0),
                Folders.Count == 0 ? Theme.TextDim : Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.PathEllipsis);
        Refill();
    }

    void Refill()
    {
        _list.Items.Clear();
        foreach (var f in Folders) _list.Items.Add(f);
        if (Folders.Count == 0) _list.Items.Add("No folders yet: click Add…");
    }
}
