using System.Text.Json;

namespace aBookPlayer.Droid;

/// <summary>
/// What the app remembers, in its own data folder: the library folders and, for every book, the same
/// <see cref="BookState"/> as the Windows app (position, speed, bookmarks…), so positions can be synced as they are.
/// </summary>
sealed class MobileSettings : IListeningHistory
{
	public List<string> LibraryFolders { get; set; } = [];
	/// <summary>The folder shared with the PCs (kept in sync by Syncthing, FolderSync…) where positions are synced; null = off.</summary>
	public string? SyncFolder { get; set; }
	/// <summary>PCs sharing their library with this phone (streaming).</summary>
	public List<RemoteServer> Servers { get; set; } = [];
	/// <summary>For the books of a PC: the last position this phone told it about (when it moved), to tell it the newer ones.</summary>
	public Dictionary<string, DateTime> SentPositions { get; set; } = [];
	/// <summary>Books copied from a PC over mobile data too (through a VPN), not only on Wi-Fi.</summary>
	public bool DownloadOverMobileData { get; set; }
	/// <summary>Keyed by the book's full path.</summary>
	public Dictionary<string, BookState> Books { get; set; } = [];
	public string? LastBook { get; set; }
	public double PlaybackSpeed { get; set; } = 1.0;
	public bool SkipSilences { get; set; }
	/// <summary>The size of the subtitles in the player, in the phone's own text units (22 as the app comes).</summary>
	public double SubtitleSize { get; set; } = StandardSubtitleSize;
	public const double StandardSubtitleSize = 22;
	/// <summary>Seconds skipped at the start and at the end of the books that have no skips of their own (as on the PC).</summary>
	public double DefaultSkipIntroSeconds { get; set; }
	public double DefaultSkipOutroSeconds { get; set; }
	public Dictionary<string, double> ListeningDays { get; set; } = [];
	/// <summary>The library's filter (0 all books, 1 in progress, 2 not started, 3 finished) and order (LibrarySort), as on the PC.</summary>
	public int LibraryShow { get; set; }
	public int LibrarySortBy { get; set; }
	/// <summary>The authors and series closed in the library when it lists by author or by series.</summary>
	public HashSet<string> CollapsedLibraryGroups { get; set; } = [];

	static string FilePath => Path.Combine(FileSystem.AppDataDirectory, "settings.json");
	static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

	public static MobileSettings Load()
	{
		try
		{
			if (File.Exists(FilePath) && JsonSerializer.Deserialize<MobileSettings>(File.ReadAllText(FilePath), Options) is { } loaded)
			{
				loaded.LibraryFolders ??= [];
				loaded.Books ??= [];
				loaded.ListeningDays ??= [];
				loaded.Servers ??= [];
				loaded.SentPositions ??= [];
				loaded.CollapsedLibraryGroups ??= [];
				return loaded;
			}
		}
		catch { /* damaged: start again */ }
		return new MobileSettings();
	}

	public void Save()
	{
		try
		{
			var tmp = FilePath + ".tmp";
			File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
			File.Move(tmp, FilePath, overwrite: true);
		}
		catch { /* never stop the app for this */ }
	}

	/// <summary>The book's state, created the first time it is opened.</summary>
	public BookState Book(string path) => Books.TryGetValue(path, out var book) ? book : Books[path] = new BookState();
}
