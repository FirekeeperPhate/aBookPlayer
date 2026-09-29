namespace aBookPlayer;

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
    /// <summary>Audible book id, when the book comes from an Audible export (see <see cref="AudibleExport"/>).</summary>
    public string? Asin { get; set; }
    /// <summary>Series name, for the library ("Dungeon Crawler Carl" + <see cref="SeriesNumber"/>).</summary>
    public string? Series { get; set; }
    public int? SeriesNumber { get; set; }
    public List<Bookmark> Bookmarks { get; set; } = [];
    /// <summary>This book's playback speed (every narrator has a pace); null = the default speed.</summary>
    public double? Speed { get; set; }
    /// <summary>Real time spent listening to this book (for the statistics).</summary>
    public double ListenedSeconds { get; set; }
    /// <summary>Seconds skipped at the start and at the end (credits, "This is Audible"…); null = the default for all books.</summary>
    public double? SkipIntroSeconds { get; set; }
    public double? SkipOutroSeconds { get; set; }
    /// <summary>Second subtitles shown under the first (a translation); "" = removed on purpose (none loaded automatically).</summary>
    public string? SecondSubtitleFile { get; set; }
    /// <summary>Title, author and series were edited in the library: the file's tags no longer replace them.</summary>
    public bool DetailsEdited { get; set; }
}

sealed class Bookmark
{
    public double Seconds { get; set; }
    public string Note { get; set; } = "";
    public DateTime Created { get; set; }
}
