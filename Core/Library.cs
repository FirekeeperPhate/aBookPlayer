namespace aBookPlayer;

/// <summary>
/// Details read from the files of books never opened, kept on disk next to the covers: a big library (a NAS) is read
/// once, not at every start. An entry is used while the book is unchanged (the file's, or the folder's, date and size).
/// </summary>
static class LibraryDetailsCache
{
    /// <summary><paramref name="Seen"/>: the last day the book was in the library (a NAS switched off must not empty the cache).</summary>
    sealed record Entry(string Stamp, string Title, string? Author, string? Series, int? Number, DateTime Seen);

    /// <summary>Where the cache is kept (each app sets it to its data folder); null keeps it in memory only.</summary>
    public static string? FilePath { get; set; }

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
                if (FilePath != null && File.Exists(FilePath))
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
            if (_entries == null || !_dirty || FilePath == null) return;
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

/// <summary>A book as listed in the library (each app adds what it needs to show it, such as its cover picture).</summary>
class LibraryEntry
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
    public bool Missing { get; set; }

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
    public static List<T> Sort<T>(IEnumerable<T> books, LibrarySort sort) where T : LibraryEntry => sort switch
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
    public static List<object> Group<T>(List<T> sorted, LibrarySort sort, ICollection<string> collapsed) where T : LibraryEntry
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
