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

/// <summary>Settings persisted to %APPDATA%\aBookPlayer\settings.json.</summary>
sealed class AppSettings : IListeningHistory
{
    public const string AppName = "aBookPlayer";
    const string LegacyFolderName = "Mp3ChapterPlayer";

    /// <summary>
    /// Moves data saved by earlier builds (settings in %APPDATA%, Whisper models in %LOCALAPPDATA%)
    /// from the old "Mp3ChapterPlayer" folders, so nothing is lost or downloaded again.
    /// </summary>
    public static void MigrateLegacyFolders()
    {
        if (AppPaths.Portable) return; // a portable copy never had those folders
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
    /// <summary>Shorten the narrator's long pauses.</summary>
    public bool SkipSilences { get; set; }
    /// <summary>Look for a newer version on GitHub at most once a day (Help menu).</summary>
    public bool CheckForUpdates { get; set; } = true;
    public DateTime LastUpdateCheck { get; set; }
    /// <summary>A version the user chose not to download: the automatic check does not offer it again.</summary>
    public string? SkippedVersion { get; set; }
    /// <summary>Seconds skipped at the start and end of books that have no values of their own.</summary>
    public double DefaultSkipIntroSeconds { get; set; }
    public double DefaultSkipOutroSeconds { get; set; }
    /// <summary>Minutes to listen each day (0 = no goal), for the statistics and the streak.</summary>
    public int DailyGoalMinutes { get; set; }
    /// <summary>Library groups (by author or series) the user collapsed, as "author:…" / "series:…".</summary>
    public List<string> CollapsedLibraryGroups { get; set; } = [];
    /// <summary>Icon in the notification area; minimizing hides the window there.</summary>
    public bool TrayIcon { get; set; }
    public int[]? MiniPlayerLocation { get; set; }
    /// <summary>Seconds listened per day ("yyyy-MM-dd", local date), for the statistics.</summary>
    public Dictionary<string, double> ListeningDays { get; set; } = [];
    /// <summary>Listening time saved by skipping silences, in seconds.</summary>
    public double SilenceSavedSeconds { get; set; }
    /// <summary>Folder shared between PCs (OneDrive, Dropbox…) where positions are synced; null = off.</summary>
    public string? SyncFolder { get; set; }
    /// <summary>Folders scanned by the library for books.</summary>
    public List<string> LibraryFolders { get; set; } = [];
    /// <summary>The library panel at the left of the subtitles (File → Library, Ctrl+L).</summary>
    public bool ShowLibrary { get; set; } = true;
    /// <summary>Widths of the library and chapter panels in 96-DPI pixels, set by dragging their dividers.</summary>
    public int LibraryPanelWidth { get; set; } = 330;
    public int ChaptersPanelWidth { get; set; } = 320;
    public SubtitleStyle Subtitles { get; set; } = new();
    /// <summary>Model id (see <see cref="WhisperModelInfo.Id"/>, e.g. "BaseEn" or "Medium-Q5_0"), independent of the UI language.</summary>
    public string WhisperModel { get; set; } = "BaseEn";
    public string WhisperLanguage { get; set; } = "en";
    public bool WhisperSaveText { get; set; } = true;
    /// <summary>Translate into English instead of transcribing (saved as "Book.en.srt", shown under the subtitles).</summary>
    public bool WhisperTranslate { get; set; }
    /// <summary>Transcribe on the graphics card when possible (see <see cref="GpuSupport"/>).</summary>
    public bool WhisperUseGpu { get; set; } = true;
    public int[]? WindowBounds { get; set; }
    public bool WindowMaximized { get; set; }

    static readonly string FilePath = Path.Combine(AppPaths.Roaming, "settings.json");

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
                    settings.ListeningDays ??= [];
                    settings.CollapsedLibraryGroups ??= [];
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
            // The oldest books go first, but never those with bookmarks or details edited by hand (a book edited
            // in the library but never opened would otherwise look like the oldest of all)
            foreach (var old in Books.Where(b => b.Value.Bookmarks is not { Count: > 0 } && !b.Value.DetailsEdited).OrderBy(b => b.Value.LastOpened)
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
