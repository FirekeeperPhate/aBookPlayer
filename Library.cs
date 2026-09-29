using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using static aBookPlayer.DialogControls;

namespace aBookPlayer;

/// <summary>
/// Small cover pictures kept for the library, so it can show them without opening every book; and the cover chosen
/// by the user for a book (Edit details), which wins over the one in its files.
/// </summary>
static class LibraryCovers
{
    const int Size = 160, CustomSize = 600;
    static readonly string Folder = Path.Combine(AppPaths.Local, "covers");

    static string FileFor(string bookPath, bool custom = false)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(bookPath).ToLowerInvariant()));
        return Path.Combine(Folder, Convert.ToHexString(hash)[..20] + (custom ? "-custom.jpg" : ".jpg"));
    }

    /// <summary>Sets (or, with null, removes) the cover chosen by the user; kept large enough for the player and Windows.</summary>
    public static void SaveCustom(string bookPath, byte[]? picture)
    {
        var file = FileFor(bookPath, custom: true);
        using var image = CoverArt.ToImage(picture);
        if (image == null)
        {
            if (File.Exists(file)) File.Delete(file);
            return;
        }
        Directory.CreateDirectory(Folder);
        using var resized = Math.Max(image.Width, image.Height) > CustomSize ? Thumbnail(image, CustomSize) : new Bitmap(image);
        resized.Save(file, ImageFormat.Jpeg);
    }

    /// <summary>The cover chosen by the user, as JPEG bytes (for the player's header and Windows' media controls).</summary>
    public static byte[]? LoadCustom(string bookPath)
    {
        try
        {
            var file = FileFor(bookPath, custom: true);
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }
        catch { return null; }
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

    public static bool Exists(string bookPath)
    {
        try { return File.Exists(FileFor(bookPath)) || File.Exists(FileFor(bookPath, custom: true)); }
        catch { return false; }
    }

    /// <summary>The saved picture (the user's own first), scaled down to <paramref name="size"/> pixels (the library draws them small).</summary>
    public static Image? Load(string bookPath, int size = Size, bool custom = true)
    {
        try
        {
            var file = FileFor(bookPath, custom: true);
            if (!custom || !File.Exists(file)) file = FileFor(bookPath);
            if (!File.Exists(file)) return null;
            using var stream = new MemoryStream(File.ReadAllBytes(file));
            using var image = Image.FromStream(stream);
            return size < Math.Max(image.Width, image.Height) ? Thumbnail(image, size) : new Bitmap(image);
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

/// <summary>
/// Details read from the files of books never opened, kept on disk next to the covers: a big library (a NAS) is read
/// once, not at every start. An entry is used while the book is unchanged (the file's, or the folder's, date and size).
/// </summary>
static class LibraryDetailsCache
{
    /// <summary><paramref name="Seen"/>: the last day the book was in the library (a NAS switched off must not empty the cache).</summary>
    sealed record Entry(string Stamp, string Title, string? Author, string? Series, int? Number, DateTime Seen);

    static readonly string FilePath = Path.Combine(AppPaths.Local, "covers", "details.json");
    static readonly object Gate = new();
    static Dictionary<string, Entry>? _entries;
    static bool _dirty;

    static Dictionary<string, Entry> Entries
    {
        get
        {
            if (_entries != null) return _entries;
            try
            {
                if (File.Exists(FilePath))
                    _entries = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath));
            }
            catch { /* damaged: read the books again */ }
            return _entries = new Dictionary<string, Entry>(_entries ?? new Dictionary<string, Entry>(), StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>What tells whether the book changed: a single file's size and date, or a book folder's date (files added or removed).</summary>
    public static string? StampOf(string bookPath)
    {
        try
        {
            if (Directory.Exists(bookPath)) return "d" + Directory.GetLastWriteTimeUtc(bookPath).Ticks;
            var file = new FileInfo(bookPath);
            return file.Exists ? $"f{file.Length}:{file.LastWriteTimeUtc.Ticks}" : null;
        }
        catch { return null; }
    }

    public static BookDetails? Get(string bookPath, string stamp)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(bookPath, out var e) || e.Stamp != stamp) return null;
            if (e.Seen < DateTime.UtcNow.Date)
            {
                Entries[bookPath] = e with { Seen = DateTime.UtcNow.Date };
                _dirty = true; // at most once a day per book
            }
            return new BookDetails(e.Title, e.Author, e.Series, e.Number);
        }
    }

    public static void Put(string bookPath, string stamp, BookDetails d)
    {
        lock (Gate)
        {
            Entries[bookPath] = new Entry(stamp, d.Title, d.Author, d.Series, d.Number, DateTime.UtcNow.Date);
            _dirty = true;
        }
    }

    /// <summary>Writes the cache when something changed, dropping the books not seen in the library for 90 days.</summary>
    public static void Save()
    {
        lock (Gate)
        {
            if (_entries == null || !_dirty) return;
            foreach (var gone in _entries.Where(e => e.Value.Seen < DateTime.UtcNow.AddDays(-90)).Select(e => e.Key).ToList()) _entries.Remove(gone);
            _dirty = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(_entries));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { /* only a cache */ }
        }
    }
}

/// <summary>A book as listed in the library.</summary>
sealed class LibraryEntry
{
    public required string Path { get; init; }
    public BookState? State { get; set; }
    /// <summary>What the files say, read in the background for a book never opened (the history knows the others).</summary>
    public BookDetails? Scanned { get; set; }
    public string Title => State?.Title ?? Scanned?.Title ?? BookSource.NameFromPath(Path);
    public string? Author => State != null ? State.Author : Scanned?.Author;
    public string? Series => State != null ? State.Series : Scanned?.Series;
    public int? SeriesNumber => State != null ? State.SeriesNumber : Scanned?.Number;
    /// <summary>"Dungeon Crawler Carl, Book 1" for the books of a series (e.g. an Audible library), else null.</summary>
    public string? SeriesLabel => AudibleExport.SeriesLabel(Series, SeriesNumber);
    /// <summary>Row-sized cover, loaded the first time the row is drawn (see <see cref="CoverLoaded"/>).</summary>
    public Image? Cover { get; set; }
    public bool CoverLoaded { get; set; }
    /// <summary>The size (pixels) <see cref="Cover"/> was loaded for.</summary>
    public int CoverSize { get; set; }
    public bool Missing { get; set; }

    /// <summary>Drops the cover so the next drawing loads it again (it was saved anew).</summary>
    public void ForgetCover()
    {
        Cover?.Dispose();
        Cover = null;
        CoverLoaded = false;
    }

    /// <summary>0..1, or null when it has never been opened (length unknown).</summary>
    public double? Progress => State is { DurationSeconds: > 0 } s ? Math.Clamp(s.PositionSeconds / s.DurationSeconds, 0, 1) : null;

    public LibraryStatus Status =>
        State is { Finished: true } ? LibraryStatus.Finished
        : State is { PositionSeconds: > 1 } ? LibraryStatus.InProgress
        : LibraryStatus.NotStarted;
}

enum LibraryStatus { InProgress, NotStarted, Finished }

/// <summary>How the library lists the books, for a library of hundreds of titles.</summary>
enum LibrarySort { Recent, Author, Series }

/// <summary>Orders the library rows: kept out of the form so it can be tested on its own.</summary>
static class LibraryOrder
{
    public static List<LibraryEntry> Sort(IEnumerable<LibraryEntry> books, LibrarySort sort) => sort switch
    {
        // By author, then by series, so the books of a series stay together and in reading order
        // The series name, then its number: the label "Series, Book 10" would sort as text before "…, Book 2"
        LibrarySort.Author => books
            .OrderBy(b => b.Author == null).ThenBy(b => b.Author, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(b => SeriesOf(b) == null).ThenBy(SeriesOf, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(b => b.SeriesNumber ?? 0).ThenBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList(),
        LibrarySort.Series => books
            .OrderBy(b => SeriesOf(b) == null).ThenBy(SeriesOf, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(b => b.SeriesNumber ?? 0).ThenBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList(),
        // Most recently listened first, then the others by title
        _ => books.OrderByDescending(b => b.State?.LastOpened ?? DateTime.MinValue)
            .ThenBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList(),
    };

    static string? SeriesOf(LibraryEntry b) => string.IsNullOrWhiteSpace(b.Series) ? null : b.Series;

    /// <summary>
    /// The rows as the list shows them: sorted by author or series, a <see cref="LibraryGroup"/> header before each
    /// group, whose books are left out when it is collapsed; other sorts are listed as they are.
    /// </summary>
    public static List<object> Group(List<LibraryEntry> sorted, LibrarySort sort, ICollection<string> collapsed)
    {
        if (sort == LibrarySort.Recent) return [.. sorted];
        var items = new List<object>();
        // The rows are already in group order: consecutive rows with the same key form a group
        foreach (var run in sorted.GroupBy(b => GroupKey(b, sort)).ToList())
        {
            var books = run.ToList();
            var name = sort == LibrarySort.Author ? books[0].Author?.Trim() ?? "Unknown author" : SeriesOf(books[0])?.Trim() ?? "Not in a series";
            bool closed = collapsed.Contains(run.Key);
            items.Add(new LibraryGroup(run.Key, name, books.Count, books.Count(b => b.Status == LibraryStatus.Finished), closed));
            if (!closed) items.AddRange(books);
        }
        return items;
    }

    static string GroupKey(LibraryEntry b, LibrarySort sort) => sort == LibrarySort.Author
        ? "author:" + (b.Author?.Trim().ToLowerInvariant() ?? "")
        : "series:" + (SeriesOf(b)?.Trim().ToLowerInvariant() ?? "");
}

/// <summary>A header in the library list: an author or a series, with how many books it has.</summary>
sealed record LibraryGroup(string Key, string Name, int Books, int Finished, bool Collapsed);

/// <summary>A book that may come next in a series.</summary>
sealed record SeriesCandidate(string Path, string Title, string? Series, int? Number, bool Finished);

/// <summary>Finds the next book of a series (kept apart so it can be tested).</summary>
static class SeriesOrder
{
    /// <summary>The unfinished book with the lowest number after <paramref name="number"/> in the same series.</summary>
    public static SeriesCandidate? Next(string series, int number, IEnumerable<SeriesCandidate> books) =>
        books.Where(b => b.Number > number && !b.Finished && string.Equals(b.Series?.Trim(), series.Trim(), StringComparison.CurrentCultureIgnoreCase))
             .OrderBy(b => b.Number)
             .FirstOrDefault();

    /// <summary>
    /// The books stored beside this one (same folder, or folders next to its own): where the rest of a series
    /// usually is. Reading the tags of the whole library would be too slow.
    /// </summary>
    public static IEnumerable<string> Nearby(string book, IEnumerable<string> others)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(book));
        if (parent == null) return [];
        var grandparent = Path.GetDirectoryName(parent);
        return others.Where(o =>
        {
            var p = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(o));
            return string.Equals(p, parent, StringComparison.OrdinalIgnoreCase)
                || (grandparent != null && string.Equals(Path.GetDirectoryName(p ?? ""), grandparent, StringComparison.OrdinalIgnoreCase));
        });
    }
}

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

/// <summary>
/// The library, beside the subtitles at the left of the main window: the books listened to (with their progress) and
/// those found in the library folders. File → Library (Ctrl+L) shows and hides it.
/// </summary>
sealed class LibraryPanel : Panel
{
    readonly AppSettings _settings;
    readonly DarkList _list = new(68) { Dock = DockStyle.Fill };
    readonly TextBox _filter = DarkDialog.MakeTextBox();
    readonly ComboBox _show = MakeCombo(100);
    readonly ComboBox _sort = MakeCombo(100);
    readonly Label _status = new()
    {
        Dock = DockStyle.Top, Height = 24, Visible = false, ForeColor = Theme.TextDim, Padding = new Padding(12, 2, 8, 0),
        AutoEllipsis = true, UseMnemonic = false,
    };
    readonly ContextMenuStrip _menu = new() { Renderer = new DarkMenuRenderer(), ShowImageMargin = false };
    readonly List<LibraryEntry> _all = [];
    readonly CancellationTokenSource _cts = new();
    CancellationTokenSource? _scan;
    bool _scanning;
    DateTime _lastScan;
    string? _current;

    /// <summary>A book was chosen (double-click, Enter or Open): the main window opens it.</summary>
    public event Action<string>? BookChosen;

    /// <summary>The panel changed the settings (a book forgotten or marked finished, other folders): save them.</summary>
    public event Action? Changed;

    /// <summary>The open book was marked finished (true) or unfinished: the main window updates the player and saves.</summary>
    public event Action<bool>? OpenBookFinished;

    /// <summary>A book's details (title, author, series, cover) were edited: the main window saves, and shows them if it is open.</summary>
    public event Action<string>? DetailsEdited;

    public LibraryPanel(AppSettings settings)
    {
        _settings = settings;
        BackColor = Theme.Panel;

        var header = new Panel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(0, 8, 8, 2) };
        var title = new Label
        {
            Dock = DockStyle.Fill, Padding = new Padding(14, 8, 0, 0), Text = "LIBRARY",
            Font = new Font("Segoe UI Semibold", 9f), ForeColor = Theme.TextDim,
        };
        var folders = new Button
        {
            Text = "Folders…", Dock = DockStyle.Right, Width = 84, FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel,
            ForeColor = Theme.TextDim, TabStop = false, UseMnemonic = false, Cursor = Cursors.Hand,
        };
        folders.FlatAppearance.BorderSize = 0;
        folders.FlatAppearance.MouseOverBackColor = Theme.Hover;
        header.Controls.Add(title);
        header.Controls.Add(folders);

        _filter.PlaceholderText = "Search title, author or series";
        _filter.Dock = DockStyle.Fill;
        var search = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(12, 2, 12, 4) };
        search.Controls.Add(_filter);

        _show.Items.AddRange(["All books", "In progress", "Not started", "Finished"]);
        _show.SelectedIndex = 0;
        _sort.Items.AddRange(["Recently listened", "By author", "By series"]);
        _sort.SelectedIndex = 0;
        var filters = new TableLayoutPanel { Dock = DockStyle.Top, Height = 34, ColumnCount = 2, RowCount = 1, Padding = new Padding(12, 0, 12, 4) };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _show.Dock = _sort.Dock = DockStyle.Fill;
        _show.Margin = new Padding(0, 0, 4, 0);
        _sort.Margin = new Padding(4, 0, 0, 0);
        filters.Controls.Add(_show, 0, 0);
        filters.Controls.Add(_sort, 1, 0);

        // Add order = reverse docking order: the header docks first, at the top
        Controls.Add(_list);
        Controls.Add(_status);
        Controls.Add(filters);
        Controls.Add(search);
        Controls.Add(header);

        var open = new ToolStripMenuItem("Open", null, (_, _) => Open());
        var finished = new ToolStripMenuItem("Mark finished", null, (_, _) => ToggleFinished());
        var edit = new ToolStripMenuItem("Edit details…", null, (_, _) => EditDetails());
        var forget = new ToolStripMenuItem("Forget…", null, (_, _) => Forget()) { ShortcutKeyDisplayString = "Del" };
        _menu.Items.AddRange([open, finished, edit, new ToolStripSeparator(), forget]);
        _menu.Opening += (_, e) =>
        {
            if (Current is not { } entry) { e.Cancel = true; return; }
            finished.Text = entry.Status == LibraryStatus.Finished ? "Mark unfinished" : "Mark finished";
            forget.Enabled = entry.State != null;
        };
        _list.ContextMenuStrip = _menu;
        // Group headers (by author or series) have their own, shorter rows
        _list.DrawMode = DrawMode.OwnerDrawVariable;
        _list.MeasureItem += OnListMeasureItem;
        _list.DpiChangedAfterParent += (_, _) => Refill(); // measured again at the new size
        _list.MouseDown += (_, e) =>
        {
            int i = _list.IndexFromPoint(e.Location);
            // A click on a group header opens or closes it (the second press of a double-click does not undo it)
            if (e.Button == MouseButtons.Left && e.Clicks == 1 && i >= 0 && _list.Items[i] is LibraryGroup group)
            {
                ToggleGroup(group);
                return;
            }
            // Right-click acts on the row under the mouse
            if (e.Button == MouseButtons.Right && i >= 0) _list.SelectedIndex = i;
        };
        _list.DoubleClick += (_, _) => Open();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                if (_list.SelectedItem is LibraryGroup group) ToggleGroup(group);
                else Open();
                e.Handled = e.SuppressKeyPress = true;
            }
            if (e.KeyCode == Keys.Delete) { Forget(); e.Handled = e.SuppressKeyPress = true; }
        };
        _filter.KeyDown += (_, e) =>
        {
            // Esc clears the search, then leaves it; ↓ goes to the list
            if (e.KeyCode == Keys.Escape)
            {
                if (_filter.TextLength > 0) _filter.Clear();
                else _list.Focus();
                e.Handled = e.SuppressKeyPress = true;
            }
            if (e.KeyCode == Keys.Down) { _list.Focus(); e.Handled = e.SuppressKeyPress = true; }
        };
        folders.Click += (_, _) => EditFolders();
        _filter.TextChanged += (_, _) => Refill();
        _show.SelectedIndexChanged += (_, _) => Refill();
        _sort.SelectedIndexChanged += (_, _) => Refill();
        _list.DrawRow = DrawEntry;

        // Covers are loaded when their row is first drawn: a big library would otherwise keep them all in memory
        foreach (var (path, state) in _settings.Books)
            _all.Add(new LibraryEntry { Path = path, State = state });
        Refill();
    }

    /// <summary>
    /// The folders are scanned whenever the panel appears (not at every start with the panel hidden), like the old
    /// Library window did at each opening: books copied there meanwhile show up.
    /// </summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && !IsDisposed && !_scanning) _ = LoadInBackgroundAsync();
    }

    /// <summary>Scans the folders again if the last scan is older than <paramref name="age"/> (the window was reactivated).</summary>
    public void RescanIfOlder(TimeSpan age)
    {
        if (Visible && !IsDisposed && !_scanning && DateTime.UtcNow - _lastScan > age) _ = LoadInBackgroundAsync();
    }

    public void FocusList() => _list.Focus();

    /// <summary>Books found in the library folders but never opened, with their details once read from their files.</summary>
    public List<(string Path, BookDetails? Details)> UnopenedBooks() => _all.Where(b => b.State == null).Select(b => (b.Path, b.Scanned)).ToList();

    /// <summary>The search box or a filter has the focus: keys typed go there.</summary>
    public bool TextInputFocused => Visible && (_filter.Focused || _show.Focused || _sort.Focused);

    /// <summary>
    /// Keys the panel needs for itself (typing in the search box, moving through the list) instead of the player's
    /// shortcuts: without this, Space or a letter typed in the search would play/pause or skip. Shortcuts a text box
    /// has no use for (Ctrl+O, F1, media keys…) still reach the player.
    /// </summary>
    public bool OwnsKey(Keys keyData)
    {
        if (!Visible || !ContainsFocus) return false;
        var key = keyData & Keys.KeyCode;
        bool ctrl = (keyData & Keys.Control) != 0, alt = (keyData & Keys.Alt) != 0;
        if (alt || key is >= Keys.F1 and <= Keys.F24 || key is >= Keys.BrowserBack and <= Keys.LaunchApplication2) return false;
        if (_filter.Focused)
            return !ctrl || key is Keys.A or Keys.C or Keys.V or Keys.X or Keys.Z or Keys.Y or Keys.Left or Keys.Right
                                or Keys.Back or Keys.Delete or Keys.Home or Keys.End;
        if (_show.Focused || _sort.Focused)
            return !ctrl && key is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown or Keys.Enter or Keys.Escape;
        return _list.Focused && keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown
                                        or Keys.Enter or Keys.Delete or Keys.Apps or (Keys.Shift | Keys.F10);
    }

    /// <summary>
    /// Brings the list up to date with the per-book history: books opened or forgotten elsewhere, the open book
    /// (marked in the list) and its progress.
    /// </summary>
    public void SyncWithSettings(string? currentBook)
    {
        if (IsDisposed) return;
        var current = currentBook != null ? Path.GetFullPath(currentBook) : null;
        bool newBook = !string.Equals(current, _current, StringComparison.OrdinalIgnoreCase), changed = newBook;
        _current = current;

        var byPath = new Dictionary<string, LibraryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _all) byPath.TryAdd(entry.Path, entry);
        foreach (var (path, state) in _settings.Books)
        {
            if (!byPath.TryGetValue(path, out var entry))
            {
                _all.Add(new LibraryEntry { Path = path, State = state });
                changed = true;
            }
            else if (!ReferenceEquals(entry.State, state))
            {
                entry.State = state;
                changed = true;
            }
        }
        // Forgotten elsewhere (Recent books → Clear list, a missing book removed): the folders may still list it
        foreach (var entry in _all.Where(b => b.State != null && !_settings.Books.ContainsKey(b.Path)).ToList())
        {
            entry.State = null;
            changed = true;
            if (!InFolders(entry.Path))
            {
                _all.Remove(entry);
                entry.ForgetCover();
            }
        }
        // A book finished, started or given another author/series in place: filters, groups and their counts change
        if (changed || Signature() != _shown) Refill(select: newBook ? _current : null); // another book opened: select it
        else _list.Invalidate(); // progress and titles change in place
    }

    /// <summary>The cover picture of a book was saved again (the book was just opened).</summary>
    public void ReloadCover(string path)
    {
        if (IsDisposed) return;
        var full = Path.GetFullPath(path);
        var entry = _all.FirstOrDefault(b => string.Equals(b.Path, full, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return;
        entry.ForgetCover();
        _list.Invalidate();
    }

    void SetStatus(string text)
    {
        _status.Text = text;
        _status.Visible = text.Length > 0;
    }

    /// <summary>
    /// Adds the books found in the library folders, drops the ones no longer there (never opened, so nothing to
    /// keep) and marks the opened ones that cannot be found.
    /// </summary>
    async Task LoadInBackgroundAsync()
    {
        // Only the first scan says so: the later ones (the panel shown again, the window reactivated) run quietly
        if (_lastScan == default) SetStatus(_settings.LibraryFolders.Count > 0 ? "Looking for books…" : "");
        _lastScan = DateTime.UtcNow;
        var known = _all.Select(b => b.Path).ToList();
        var roots = _settings.LibraryFolders.ToList();
        // A new scan (the folders changed) makes the previous one obsolete: its results must not arrive later
        // Disposed too: each linked source stays registered on _cts (alive for the whole session) until then
        var previous = _scan;
        previous?.Cancel();
        previous?.Dispose();
        var scan = _scan = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var ct = scan.Token;
        _scanning = true;
        try
        {
            var (found, missing) = await Task.Run(() =>
            {
                // Full paths, as the history stores them: "C:/Books/x" from a typed folder must match "C:\Books\x"
                var books = LibraryScanner.Scan(roots, ct).Select(p => Path.GetFullPath(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var gone = known.Where(p => !books.Contains(p) && !BookSource.Exists(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return (books, gone);
            }, ct);
            if (IsDisposed || ct.IsCancellationRequested) return;
            foreach (var entry in _all) entry.Missing = missing.Contains(entry.Path);
            foreach (var entry in _all.Where(b => b.State == null && b.Missing).ToList())
            {
                _all.Remove(entry);
                entry.ForgetCover();
            }
            var present = _all.Select(b => b.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = found.Where(present.Add).Select(p => new LibraryEntry { Path = p }).ToList();
            _all.AddRange(added);
            SetStatus("");
            Refill();

            // Books never opened: title, author and series from their files (for the groups, sorting and search), and
            // their cover (embedded picture or an image in their folder), saved for the next time. Once per session
            // for each book; the list is rebuilt every few books so they move to their groups
            var unread = _all.Where(b => b.State == null && b.Scanned == null).ToList();
            int pending = 0;
            foreach (var entry in unread)
            {
                var path = entry.Path;
                var (details, coverSaved) = await Task.Run(() => ReadUnopened(path), ct);
                if (IsDisposed || ct.IsCancellationRequested) return;
                if (!_all.Contains(entry)) continue;
                entry.Scanned = details;
                if (coverSaved) entry.ForgetCover(); // drawn before the picture was there: load it now
                if (++pending >= 25)
                {
                    pending = 0;
                    Refill();
                }
                else _list.Invalidate();
            }
            if (pending > 0) Refill();
            await Task.Run(LibraryDetailsCache.Save);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed) SetStatus("Could not scan: " + ex.Message);
        }
        finally
        {
            // Still the latest scan: done with it (a newer one disposed this one already)
            if (ReferenceEquals(_scan, scan))
            {
                _scanning = false;
                _scan = null;
                scan.Dispose();
            }
        }
    }

    /// <summary>
    /// Details of a book never opened, and its cover saved if there was none yet (the files are read once for both).
    /// Known and unchanged since the last time: taken from the cache, without reading its files.
    /// </summary>
    static (BookDetails? Details, bool CoverSaved) ReadUnopened(string path)
    {
        try
        {
            var stamp = LibraryDetailsCache.StampOf(path);
            if (stamp != null && LibraryDetailsCache.Get(path, stamp) is { } known) return (known, false);
            var (details, cover) = BookSource.ReadDetails(path);
            if (stamp != null) LibraryDetailsCache.Put(path, stamp, details);
            if (LibraryCovers.Exists(path)) return (details, false);
            LibraryCovers.Save(path, cover);
            return (details, LibraryCovers.Exists(path));
        }
        catch { return (null, false); }
    }

    /// <summary>
    /// A row's cover, loaded (at the size it is drawn) the first time the row is painted, and again when rows
    /// change size (the window moved to a monitor with other scaling), so it is never stretched and blurry.
    /// </summary>
    static Image? CoverOf(LibraryEntry b, int size)
    {
        if (b.CoverLoaded && b.CoverSize != size) b.ForgetCover();
        if (!b.CoverLoaded)
        {
            b.CoverLoaded = true;
            b.CoverSize = size;
            b.Cover = LibraryCovers.Load(b.Path, size);
        }
        return b.Cover;
    }

    /// <summary>Rebuilds the list; <paramref name="select"/> (the book just opened) is selected and scrolled into view.</summary>
    /// <summary>What decides where each book is listed (status, author, series): the list is rebuilt when it changes.</summary>
    string Signature()
    {
        var text = new StringBuilder();
        foreach (var b in _all) text.Append(b.Status).Append('\u0001').Append(b.Author).Append('\u0001').Append(b.Series).Append(b.SeriesNumber).Append('\u0002');
        return text.ToString();
    }

    string _shown = "";

    void Refill(string? select = null)
    {
        _shown = Signature();
        var selected = select ?? Current?.Path ?? _current;
        int top = _list.TopIndex;
        var words = _filter.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wanted = _show.SelectedIndex switch
        {
            1 => LibraryStatus.InProgress,
            2 => LibraryStatus.NotStarted,
            3 => LibraryStatus.Finished,
            _ => (LibraryStatus?)null,
        };
        var rows = LibraryOrder.Sort(_all
            .Where(b => wanted == null || b.Status == wanted)
            .Where(b => words.All(w => b.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase)
                                       || (b.Author?.Contains(w, StringComparison.CurrentCultureIgnoreCase) ?? false)
                                       || (b.SeriesLabel?.Contains(w, StringComparison.CurrentCultureIgnoreCase) ?? false))),
            (LibrarySort)_sort.SelectedIndex);
        // By author or by series: a header per group, which a click collapses (all open while searching)
        var items = LibraryOrder.Group(rows, (LibrarySort)_sort.SelectedIndex,
            words.Length > 0 ? [] : _settings.CollapsedLibraryGroups);
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var item in items) _list.Items.Add(item);
        if (rows.Count == 0)
            _list.Items.Add(_all.Count == 0 ? "No books yet: add your audiobook folders (Folders…)" : "No books match.");
        int index = items.FindIndex(i => i is LibraryEntry e && string.Equals(e.Path, selected, StringComparison.OrdinalIgnoreCase));
        if (items.Count > 0) _list.SelectedIndex = Math.Max(0, index);
        // Keep the list where it was: a refresh while browsing must not jump to the top
        if (select == null && top < _list.Items.Count) _list.TopIndex = top;
        _list.EndUpdate();
    }

    void ToggleGroup(LibraryGroup group)
    {
        var collapsed = _settings.CollapsedLibraryGroups;
        if (!collapsed.Remove(group.Key)) collapsed.Add(group.Key);
        Refill();
        if (_list.Items.OfType<LibraryGroup>().FirstOrDefault(g => g.Key == group.Key) is { } header)
            _list.SelectedIndex = _list.Items.IndexOf(header);
        Changed?.Invoke();
    }

    void OnListMeasureItem(object? sender, MeasureItemEventArgs e) =>
        e.ItemHeight = e.Index >= 0 && e.Index < _list.Items.Count && _list.Items[e.Index] is LibraryGroup ? _list.L(38) : _list.L(68);

    LibraryEntry? Current => _list.SelectedItem as LibraryEntry;

    /// <summary>Inside one of the library folders (not merely a folder whose name starts the same, like "Books 2").</summary>
    bool InFolders(string path) => _settings.LibraryFolders.Any(f =>
    {
        try
        {
            var root = Path.GetFullPath(f);
            if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar; // "D:\" already ends in one
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    });

    void Open()
    {
        if (Current is not { } entry) return;
        if (entry.Missing)
        {
            MessageBox.Show(FindForm(), $"\"{entry.Path}\" cannot be found (moved, deleted, or its drive is not connected).", "Library",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        BookChosen?.Invoke(entry.Path);
    }

    void Forget()
    {
        if (Current is not { State: not null } entry) return;
        if (string.Equals(entry.Path, _current, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(FindForm(), "This is the book that is open now: open another one first.", "Library",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(FindForm(), $"Forget \"{entry.Title}\", with its position and bookmarks?\n\nThe audio files are not touched.", "Library",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK)
            return;
        _settings.Books.Remove(entry.Path);
        entry.State = null;
        if (!InFolders(entry.Path))
        {
            _all.Remove(entry);
            entry.ForgetCover();
        }
        Refill();
        Changed?.Invoke();
    }

    /// <summary>A history entry for a book never opened, starting from what its files say (read after the scan).</summary>
    BookState NewState(LibraryEntry entry) =>
        _settings.Books[entry.Path] = new BookState { Title = entry.Title, Author = entry.Author, Series = entry.Series, SeriesNumber = entry.SeriesNumber };

    void ToggleFinished()
    {
        if (Current is not { } entry) return;
        entry.State ??= NewState(entry);
        entry.State.Finished = !entry.State.Finished;
        if (entry.State.Finished) entry.State.PositionSeconds = 0;
        entry.State.PositionUpdated = DateTime.UtcNow;
        Refill();
        // The open book: the player must stop and go back to the start too, or the next save would put its
        // position back (the main window saves then)
        if (string.Equals(entry.Path, _current, StringComparison.OrdinalIgnoreCase)) OpenBookFinished?.Invoke(entry.State.Finished);
        else Changed?.Invoke();
    }

    void EditDetails()
    {
        if (Current is not { } entry) return;
        bool edited = entry.State?.DetailsEdited == true;
        using (var dlg = new BookDetailsForm(entry.Title, entry.Author, entry.Series, entry.SeriesNumber,
                   LibraryCovers.Load(entry.Path, 320), LibraryCovers.Load(entry.Path, 320, custom: false), edited))
        {
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            // A book never opened gets its entry now, like "Mark finished" does
            var state = entry.State ??= NewState(entry);
            try
            {
                if (dlg.UseFileDetails)
                {
                    state.DetailsEdited = false;
                    LibraryCovers.SaveCustom(entry.Path, null);
                    // The open book gets the file's details back from the main window; the others read them now
                    if (!string.Equals(entry.Path, _current, StringComparison.OrdinalIgnoreCase)) _ = RestoreFileDetailsAsync(entry, state);
                }
                else
                {
                    state.Title = dlg.BookTitle;
                    state.Author = dlg.Author;
                    state.Series = dlg.Series;
                    state.SeriesNumber = dlg.Number;
                    state.DetailsEdited = true;
                    if (dlg.CoverChanged) LibraryCovers.SaveCustom(entry.Path, dlg.Cover);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                MessageBox.Show(FindForm(), $"The cover could not be saved:\n{ex.Message}", "Library", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        entry.ForgetCover();
        Refill();
        DetailsEdited?.Invoke(entry.Path);
    }

    /// <summary>"Use the file's details" on a book that is not open: its title, author and series are read from its files again.</summary>
    async Task RestoreFileDetailsAsync(LibraryEntry entry, BookState state)
    {
        BookDetails? details;
        try { details = (await Task.Run(() => BookSource.ReadDetails(entry.Path))).Details; }
        catch { return; } // unreachable now: they come back when the book is opened
        if (IsDisposed || state.DetailsEdited) return; // edited again meanwhile
        (state.Title, state.Author, state.Series, state.SeriesNumber) = (details.Title, details.Author, details.Series, details.Number);
        entry.Scanned = details;
        Refill();
        Changed?.Invoke();
    }

    void EditFolders()
    {
        using var dlg = new LibraryFoldersForm(_settings.LibraryFolders);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        _settings.LibraryFolders = dlg.Folders;
        // Rescan: drop the entries that only came from the folders
        foreach (var gone in _all.Where(b => b.State == null)) gone.ForgetCover();
        _all.RemoveAll(b => b.State == null);
        Refill();
        Changed?.Invoke();
        _ = LoadInBackgroundAsync();
    }

    void DrawEntry(Graphics g, Rectangle r, int index)
    {
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        if (_list.Items[index] is LibraryGroup group)
        {
            // ▾ Author (or series)                         4 books · 1 finished
            using (var line = new Pen(Theme.Border)) g.DrawLine(line, r.X + _list.L(12), r.Y, r.Right - _list.L(10), r.Y);
            // Chevron right / down from the Windows icon font, like Explorer's tree
            var arrow = new Rectangle(r.X + _list.L(10), r.Y, _list.L(24), r.Height);
            using (var icons = new Font(Theme.IconFontName, _list.L(11), GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, group.Collapsed ? "" : "", icons, arrow, Theme.TextDim,
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
            var count = group.Books == 1 ? "1 book" : $"{group.Books} books";
            if (group.Finished > 0) count += $" · {group.Finished} finished";
            int countWidth = TextRenderer.MeasureText(g, count, _list.Font).Width + _list.L(4);
            using var heading = new Font(_list.Font, FontStyle.Bold);
            TextRenderer.DrawText(g, group.Name, heading, new Rectangle(arrow.Right, r.Y, r.Right - arrow.Right - countWidth - _list.L(16), r.Height),
                Theme.Text, flags | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, count, _list.Font, new Rectangle(r.Right - countWidth - _list.L(10), r.Y, countWidth, r.Height),
                Theme.TextDim, flags | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            return;
        }
        if (_list.Items[index] is not LibraryEntry b)
        {
            TextRenderer.DrawText(g, _list.Items[index].ToString(), _list.Font, Rectangle.Inflate(r, -_list.L(12), 0), Theme.TextDim,
                flags | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            return;
        }
        // The open book: an accent bar at the left, like the current chapter
        bool current = string.Equals(b.Path, _current, StringComparison.OrdinalIgnoreCase);
        if (current)
        {
            using var mark = new SolidBrush(Theme.Accent);
            g.FillRectangle(mark, r.X, r.Y + _list.L(8), _list.L(3), r.Height - 2 * _list.L(8));
        }

        int coverSize = r.Height - 2 * _list.L(8);
        var coverRect = new Rectangle(r.X + _list.L(12), r.Y + _list.L(8), coverSize, coverSize);
        if (CoverOf(b, coverSize) is { } cover)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            double scale = Math.Min(coverSize / (double)cover.Width, coverSize / (double)cover.Height);
            int w = (int)(cover.Width * scale), h = (int)(cover.Height * scale);
            g.DrawImage(cover, coverRect.X + (coverSize - w) / 2, coverRect.Y + (coverSize - h) / 2, w, h);
        }
        else
        {
            using var placeholder = new SolidBrush(Theme.Surface);
            g.FillRectangle(placeholder, coverRect);
            using var icon = new Font(Theme.IconFontName, coverSize / 3f, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, "", icon, coverRect, Theme.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        int x = coverRect.Right + _list.L(12), width = r.Right - x - _list.L(10);
        var textColor = b.Missing ? Theme.TextDim : current ? Theme.Accent : Theme.Text;
        using var bold = new Font(_list.Font, FontStyle.Bold);
        TextRenderer.DrawText(g, b.Title, bold, new Rectangle(x, r.Y + _list.L(6), width, _list.L(22)), textColor, flags);

        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(b.Author)) details.Add(b.Author);
        if (b.SeriesLabel is { } series) details.Add(series);
        if (b.Missing) details.Add("not found");
        else if (b.State is { DurationSeconds: > 0 } s) details.Add(FormatLength(TimeSpan.FromSeconds(s.DurationSeconds)));
        TextRenderer.DrawText(g, string.Join("  ·  ", details), _list.Font, new Rectangle(x, r.Y + _list.L(27), width, _list.L(20)), Theme.TextDim, flags);

        // Progress
        var status = b.Status switch
        {
            LibraryStatus.Finished => "Finished",
            LibraryStatus.NotStarted => "Not started",
            _ when b.Progress is { } p => $"{p:P0}  ·  {FormatLength(TimeSpan.FromSeconds(b.State!.DurationSeconds * (1 - p)))} left",
            _ => "In progress",
        };
        int barY = r.Y + _list.L(56), barWidth = Math.Min(_list.L(80), width / 3);
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
        TextRenderer.DrawText(g, status, _list.Font, new Rectangle(x + barWidth + _list.L(10), barY - _list.L(10), width - barWidth - _list.L(10), _list.L(20)),
            Theme.TextDim, flags);
    }

    static string FormatLength(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _scan?.Dispose();
            _cts.Dispose();
            _menu.Dispose();
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
            Text = "Books in these folders appear in the library: audiobook files (like .m4b, or an Audible\n" +
                   "export's .mp3 files) and folders of chapter files.",
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
