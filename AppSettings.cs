using System.Text.Json;
using System.Text.Json.Serialization;

namespace aBookPlayer;

enum SubtitlePosition { Center, Bottom }

sealed class SubtitleStyle
{
    public string FontFamily { get; set; } = "Segoe UI";
    public float FontSize { get; set; } = 22f;
    public bool Bold { get; set; }
    public string TextColor { get; set; } = "#FAFAFA";
    public bool ShowBackground { get; set; } = true;
    public string BackgroundColor { get; set; } = "#000000";
    public int BackgroundOpacity { get; set; } = 55;
    public SubtitlePosition Position { get; set; } = SubtitlePosition.Center;

    public SubtitleStyle Clone() => (SubtitleStyle)MemberwiseClone();

    public static Color ParseColor(string? hex, Color fallback)
    {
        try { return string.IsNullOrWhiteSpace(hex) ? fallback : ColorTranslator.FromHtml(hex); }
        catch { return fallback; }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}

/// <summary>What is remembered for each book.</summary>
sealed class BookState
{
    public double PositionSeconds { get; set; }
    public string? SubtitleFile { get; set; }
    public double SubtitleOffsetMs { get; set; }
    public DateTime LastOpened { get; set; }
    /// <summary>When the position last moved (UTC): the newest one wins when syncing between PCs.</summary>
    public DateTime PositionUpdated { get; set; }

    /// <summary>
    /// <see cref="PositionUpdated"/>, or for books saved before it existed (1.5 and earlier) the last time the
    /// book was listened to: never "now", which would make an old position look like the newest one.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime EffectivePositionUpdated => PositionUpdated != default ? PositionUpdated : LastOpened;
    // Shown in the library without opening the book
    public double DurationSeconds { get; set; }
    public string? Title { get; set; }
    public string? Author { get; set; }
    /// <summary>Played to the end (the position then goes back to 0:00, so it cannot tell by itself).</summary>
    public bool Finished { get; set; }
    /// <summary>Identifies the same book on another PC, where it may live in a different folder (see <see cref="BookSync"/>).</summary>
    public string? SyncKey { get; set; }
    public List<Bookmark> Bookmarks { get; set; } = [];
}

sealed class Bookmark
{
    public double Seconds { get; set; }
    public string Note { get; set; } = "";
    public DateTime Created { get; set; }
}

/// <summary>Settings persisted to %APPDATA%\aBookPlayer\settings.json.</summary>
sealed class AppSettings
{
    public const string AppName = "aBookPlayer";
    const string LegacyFolderName = "Mp3ChapterPlayer";

    /// <summary>
    /// Moves data saved by earlier builds (settings in %APPDATA%, Whisper models in %LOCALAPPDATA%)
    /// from the old "Mp3ChapterPlayer" folders, so nothing is lost or downloaded again.
    /// </summary>
    public static void MigrateLegacyFolders()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            try
            {
                var basePath = Environment.GetFolderPath(root);
                var legacy = Path.Combine(basePath, LegacyFolderName);
                var current = Path.Combine(basePath, AppName);
                if (Directory.Exists(legacy) && !Directory.Exists(current)) Directory.Move(legacy, current);
            }
            catch { /* best effort: at worst the app starts with defaults */ }
        }
    }

    public string? LastFile { get; set; }

    /// <summary>Position, subtitles and sync of every book opened, keyed by full path.</summary>
    public Dictionary<string, BookState> Books { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Pre-1.2 settings kept only the last file's state: read for migration, no longer written
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double LastPositionSeconds { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public string? LastSubtitleFile { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double SubtitleOffsetMs { get; set; }

    public float Volume { get; set; } = 0.8f;
    public double PlaybackSpeed { get; set; } = 1.0;
    /// <summary>While playing, also keep the screen on (no screensaver, no display timeout); standby is always prevented.</summary>
    public bool KeepScreenOn { get; set; } = true;
    /// <summary>After a long pause, resume a few seconds earlier to pick up the thread.</summary>
    public bool SmartRewind { get; set; } = true;
    public bool VoiceBoost { get; set; }
    /// <summary>Folder shared between PCs (OneDrive, Dropbox…) where positions are synced; null = off.</summary>
    public string? SyncFolder { get; set; }
    /// <summary>Folders scanned by the library for books.</summary>
    public List<string> LibraryFolders { get; set; } = [];
    public SubtitleStyle Subtitles { get; set; } = new();
    /// <summary>Model id (see <see cref="WhisperModelInfo.Id"/>, e.g. "BaseEn" or "Medium-Q5_0"), independent of the UI language.</summary>
    public string WhisperModel { get; set; } = "BaseEn";
    public string WhisperLanguage { get; set; } = "en";
    public bool WhisperSaveText { get; set; } = true;
    /// <summary>Transcribe on the graphics card when possible (see <see cref="GpuSupport"/>).</summary>
    public bool WhisperUseGpu { get; set; } = true;
    public int[]? WindowBounds { get; set; }
    public bool WindowMaximized { get; set; }

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName, "settings.json");

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (settings != null)
                {
                    settings.Subtitles ??= new();
                    settings.LibraryFolders ??= [];
                    settings.NormalizeBooks();
                    settings.MigrateLegacyPosition();
                    return settings;
                }
            }
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new AppSettings();
    }

    public const int MaxBooks = 200;

    public BookState? GetBook(string path) =>
        Books.TryGetValue(Path.GetFullPath(path), out var book) ? book : null;

    /// <summary>Stores a book's state and marks it as the most recently used; keeps at most <see cref="MaxBooks"/> books.</summary>
    public BookState RememberBook(string path, double positionSeconds, string? subtitleFile, double subtitleOffsetMs)
    {
        path = Path.GetFullPath(path);
        // Update in place: bookmarks, duration, sync data and the like must survive every save
        if (!Books.TryGetValue(path, out var book)) Books[path] = book = new BookState();
        positionSeconds = Math.Max(0, positionSeconds);
        if (Math.Abs(book.PositionSeconds - positionSeconds) > 0.5 || book.EffectivePositionUpdated == default)
            book.PositionUpdated = DateTime.UtcNow;
        else if (book.PositionUpdated == default)
            book.PositionUpdated = book.LastOpened; // saved by 1.5 or earlier: the position dates from the last listening
        book.PositionSeconds = positionSeconds;
        book.SubtitleFile = subtitleFile;
        book.SubtitleOffsetMs = subtitleOffsetMs;
        book.LastOpened = DateTime.UtcNow;
        book.Bookmarks ??= [];
        if (Books.Count > MaxBooks)
            // The oldest books go first, but never those with bookmarks
            foreach (var old in Books.Where(b => b.Value.Bookmarks is not { Count: > 0 }).OrderBy(b => b.Value.LastOpened)
                         .Take(Books.Count - MaxBooks).Select(b => b.Key).ToList())
                Books.Remove(old);
        return book;
    }

    /// <summary>Most recently used books first.</summary>
    public IEnumerable<string> RecentBooks(int count) =>
        Books.OrderByDescending(b => b.Value.LastOpened).Select(b => b.Key).Take(count);

    /// <summary>JSON creates a case-sensitive dictionary: rebuild it with Windows path semantics.</summary>
    internal void NormalizeBooks()
    {
        var books = new Dictionary<string, BookState>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, state) in Books ?? new Dictionary<string, BookState>())
            if (state != null && (!books.TryGetValue(path, out var existing) || existing.LastOpened < state.LastOpened))
                books[path] = state;
        Books = books;
    }

    /// <summary>Moves the single "last position" of pre-1.2 settings into the per-book history.</summary>
    internal void MigrateLegacyPosition()
    {
        if (LastFile != null && GetBook(LastFile) == null && (LastPositionSeconds > 0 || LastSubtitleFile != null))
            RememberBook(LastFile, LastPositionSeconds, LastSubtitleFile, SubtitleOffsetMs);
        LastPositionSeconds = 0;
        LastSubtitleFile = null;
        SubtitleOffsetMs = 0;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* never block the app if saving fails */ }
    }
}
