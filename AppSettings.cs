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
    public SubtitleStyle Subtitles { get; set; } = new();
    /// <summary>GgmlType of the model (e.g. "BaseEn"), independent of the UI language.</summary>
    public string WhisperModel { get; set; } = "BaseEn";
    public string WhisperLanguage { get; set; } = "en";
    public bool WhisperSaveText { get; set; } = true;
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
    public void RememberBook(string path, double positionSeconds, string? subtitleFile, double subtitleOffsetMs)
    {
        Books[Path.GetFullPath(path)] = new BookState
        {
            PositionSeconds = Math.Max(0, positionSeconds),
            SubtitleFile = subtitleFile,
            SubtitleOffsetMs = subtitleOffsetMs,
            LastOpened = DateTime.UtcNow,
        };
        if (Books.Count > MaxBooks)
            foreach (var old in Books.OrderBy(b => b.Value.LastOpened).Take(Books.Count - MaxBooks).Select(b => b.Key).ToList())
                Books.Remove(old);
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
