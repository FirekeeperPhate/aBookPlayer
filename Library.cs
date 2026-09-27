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

    public static bool Exists(string bookPath)
    {
        try { return File.Exists(FileFor(bookPath)); }
        catch { return false; }
    }

    /// <summary>The saved picture, scaled down to <paramref name="size"/> pixels (the library draws them small).</summary>
    public static Image? Load(string bookPath, int size = Size)
    {
        try
        {
            var file = FileFor(bookPath);
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

/// <summary>A book as listed in the library.</summary>
sealed class LibraryEntry
{
    public required string Path { get; init; }
    public BookState? State { get; set; }
    public string Title => State?.Title ?? BookSource.NameFromPath(Path);
    public string? Author => State?.Author;
    /// <summary>"Dungeon Crawler Carl, Book 1" for the books of a series (e.g. an Audible library), else null.</summary>
    public string? SeriesLabel => AudibleExport.SeriesLabel(State?.Series, State?.SeriesNumber);
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
            .ThenBy(b => b.State?.SeriesNumber ?? 0).ThenBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList(),
        LibrarySort.Series => books
            .OrderBy(b => SeriesOf(b) == null).ThenBy(SeriesOf, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(b => b.State?.SeriesNumber ?? 0).ThenBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList(),
        // Most recently listened first, then the others by title
        _ => books.OrderByDescending(b => b.State?.LastOpened ?? DateTime.MinValue)
            .ThenBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList(),
    };

    static string? SeriesOf(LibraryEntry b) => string.IsNullOrWhiteSpace(b.State?.Series) ? null : b.State.Series;
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
        var forget = new ToolStripMenuItem("Forget…", null, (_, _) => Forget()) { ShortcutKeyDisplayString = "Del" };
        _menu.Items.AddRange([open, finished, new ToolStripSeparator(), forget]);
        _menu.Opening += (_, e) =>
        {
            if (Current is not { } entry) { e.Cancel = true; return; }
            finished.Text = entry.Status == LibraryStatus.Finished ? "Mark unfinished" : "Mark finished";
            forget.Enabled = entry.State != null;
        };
        _list.ContextMenuStrip = _menu;
        _list.MouseDown += (_, e) =>
        {
            // Right-click acts on the row under the mouse
            if (e.Button != MouseButtons.Right) return;
            int i = _list.IndexFromPoint(e.Location);
            if (i >= 0) _list.SelectedIndex = i;
        };
        _list.DoubleClick += (_, _) => Open();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Open(); e.Handled = e.SuppressKeyPress = true; }
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
        if (changed) Refill(select: newBook ? _current : null); // another book opened: select it
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

            // Covers of books never opened: embedded picture or an image in their folder, saved for the next time
            foreach (var entry in added)
            {
                var path = entry.Path;
                bool saved = await Task.Run(() => LibraryCovers.Exists(path) || SaveFirstCover(path), ct);
                if (IsDisposed || ct.IsCancellationRequested) return;
                if (saved && _all.Contains(entry))
                {
                    entry.ForgetCover(); // drawn before the picture was there: load it now
                    _list.Invalidate();
                }
            }
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

    static bool SaveFirstCover(string path)
    {
        try
        {
            var first = BookSource.IsFolder(path) ? BookSource.PartsOf(path).FirstOrDefault() : path;
            if (first == null) return false;
            bool folder = BookSource.IsFolder(path);
            var bytes = MediaMetadata.Read(first).Cover ?? CoverArt.FromFolder(folder ? path : System.IO.Path.GetDirectoryName(path), bookFolder: folder);
            LibraryCovers.Save(path, bytes);
            return LibraryCovers.Exists(path);
        }
        catch { return false; }
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
    void Refill(string? select = null)
    {
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
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in rows) _list.Items.Add(r);
        if (rows.Count == 0)
            _list.Items.Add(_all.Count == 0 ? "No books yet: add your audiobook folders (Folders…)" : "No books match.");
        int index = rows.FindIndex(r => string.Equals(r.Path, selected, StringComparison.OrdinalIgnoreCase));
        if (rows.Count > 0) _list.SelectedIndex = Math.Max(0, index);
        // Keep the list where it was: a refresh while browsing must not jump to the top
        if (select == null && top < _list.Items.Count) _list.TopIndex = top;
        _list.EndUpdate();
    }

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

    void ToggleFinished()
    {
        if (Current is not { } entry) return;
        entry.State ??= _settings.Books[entry.Path] = new BookState { Title = entry.Title };
        entry.State.Finished = !entry.State.Finished;
        if (entry.State.Finished) entry.State.PositionSeconds = 0;
        entry.State.PositionUpdated = DateTime.UtcNow;
        Refill();
        // The open book: the player must stop and go back to the start too, or the next save would put its
        // position back (the main window saves then)
        if (string.Equals(entry.Path, _current, StringComparison.OrdinalIgnoreCase)) OpenBookFinished?.Invoke(entry.State.Finished);
        else Changed?.Invoke();
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
