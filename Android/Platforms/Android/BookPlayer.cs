using Android.Content;
using Android.Media;
using AndroidX.Media3.Common;
using AndroidX.Media3.Session;
using Media3Metadata = AndroidX.Media3.Common.MediaMetadata; // Core has a MediaMetadata of its own (the tag reader)

namespace aBookPlayer.Droid;

/// <summary>
/// The open book, as the pages see it: one continuous book with its chapters and subtitles, while Media3 plays it
/// as a list of files (one, or a folder's parts). Talks to <see cref="PlaybackService"/> through a media controller.
/// </summary>
sealed class BookPlayer
{
	/// <summary>Media3's Player.STATE_ENDED (the binding does not bring the interface's constants).</summary>
	const int StateEnded = 4;

	MediaController? _controller;
	TimeSpan[] _partStarts = [TimeSpan.Zero];
	DateTime _lastSave;

	public string? Path { get; private set; }
	public string Title { get; private set; } = "";
	public string? Author { get; private set; }
	public byte[]? Cover { get; private set; }
	public IReadOnlyList<Chapter> Chapters { get; private set; } = [];
	public SubtitleTrack? Subtitles { get; private set; }
	public TimeSpan Duration { get; private set; }

	public bool IsLoaded => Path != null && _controller != null;
	public bool IsPlaying => _controller?.IsPlaying == true;

	/// <summary>Where the book is: the start of the file playing plus the position in it.</summary>
	public TimeSpan Position
	{
		get
		{
			if (_controller == null || Path == null) return TimeSpan.Zero;
			int index = Math.Clamp(_controller.CurrentMediaItemIndex, 0, _partStarts.Length - 1);
			return _partStarts[index] + TimeSpan.FromMilliseconds(Math.Max(0, _controller.CurrentPosition));
		}
	}

	public double Speed
	{
		get => _controller?.PlaybackParameters?.Speed ?? 1f;
		set
		{
			_controller?.SetPlaybackSpeed((float)value);
			if (Path != null) App.Settings.Book(Path).Speed = value;
		}
	}

	/// <summary>Connects to the playback service (starting it); once.</summary>
	async Task<MediaController> ControllerAsync()
	{
		if (_controller != null) return _controller;
		var context = Platform.AppContext;
		var token = new SessionToken(context, new ComponentName(context, Java.Lang.Class.FromType(typeof(PlaybackService))));
		var future = new MediaController.Builder(context, token).BuildAsync();
		// Waiting for the connection must not block the UI thread, which delivers it
		var connected = await Task.Run(() => future!.Get());
		_controller = Android.Runtime.Extensions.JavaCast<MediaController>(connected!)!;
		// Watches for the end of the book, also while the app is in the background. (Not with a Player.Listener:
		// the binding cannot call the Java interface's default methods, and Media3 calls them all.)
		_stateTimer = Application.Current!.Dispatcher.CreateTimer();
		_stateTimer.Interval = TimeSpan.FromSeconds(1);
		_stateTimer.Tick += (_, _) => WatchState();
		_stateTimer.Start();
		return _controller;
	}

	IDispatcherTimer? _stateTimer;
	int _lastState;

	void WatchState()
	{
		if (_controller == null) return;
		int state = _controller.PlaybackState;
		if (state == StateEnded && _lastState != StateEnded) _ = OnEndedAsync();
		_lastState = state;
	}

	/// <summary>The book to go on with, found when the last one ended: offered once the app is in front.</summary>
	public (string Finished, SeriesCandidate Next)? PendingNext { get; set; }
	public event Action? NextFound;

	/// <summary>
	/// Played to the end: the book is finished (for the library and the PCs), and the next one of its series, if
	/// there is one, is looked for.
	/// </summary>
	async Task OnEndedAsync()
	{
		if (Path is not { } path) return;
		var book = App.Settings.Book(path);
		book.Finished = true;
		book.PositionUpdated = DateTime.UtcNow; // news for the PCs, even without moving
		SavePosition();
		ShowNotice("Finished");
		PendingNext = null;
		var known = App.Settings.Books.ToDictionary(b => b.Key, b => b.Value);
		var folders = App.Settings.LibraryFolders.ToList();
		SeriesCandidate? next;
		try { next = await Task.Run(() => NextInSeries.Find(path, book.Series, book.SeriesNumber, known, folders)); }
		catch { return; }
		// Only if nothing happened meanwhile (another book opened, playback started again)
		if (next == null || path != Path || IsPlaying) return;
		PendingNext = (Title, next);
		NextFound?.Invoke();
	}

	/// <summary>
	/// Opens a book where it was left, reading its details, chapters (for a folder, one per file, placed with the
	/// files' lengths) and subtitles off the UI thread.
	/// </summary>
	public async Task OpenAsync(string path, bool play)
	{
		SavePosition();
		var controller = await ControllerAsync();
		var syncFolder = App.Settings.SyncFolder;
		var (info, parts, starts, total, subtitles, key, synced) = await Task.Run(() => Read(path, syncFolder));

		Path = path;
		Title = string.IsNullOrWhiteSpace(info.Title) ? BookSource.DisplayName(path) : info.Title!;
		Author = info.Artist;
		Cover = info.Cover;
		Chapters = info.Chapters.OrderBy(c => c.Start).ToList();
		Subtitles = subtitles;
		Duration = total;
		_partStarts = starts;

		var book = App.Settings.Book(path);
		book.Title = Title;
		book.Author = info.Artist;
		book.Series = info.Series;
		book.SeriesNumber = info.SeriesNumber;
		book.DurationSeconds = total.TotalSeconds;
		book.LastOpened = DateTime.UtcNow;
		book.Asin = info.Asin;
		book.SyncKey = key;
		App.Settings.LastBook = path;

		// Another device (a PC) listened further, more recently: continue from there
		Notice = null;
		if (synced != null && synced.Updated > book.EffectivePositionUpdated.AddSeconds(2))
		{
			book.PositionSeconds = synced.Seconds;
			book.PositionUpdated = synced.Updated; // that device's position, not a newer one of this phone
			book.Finished = synced.Finished;
			ShowNotice($"Continuing from {Format(TimeSpan.FromSeconds(synced.Seconds))}, where you stopped on {synced.Machine}");
		}
		// Left at the very end (finished): start again from the beginning
		if (book.PositionSeconds >= total.TotalSeconds - 5) book.PositionSeconds = 0;

		// Each file is an item; the notification shows the book, and the chapter file's name for a folder
		var items = new List<MediaItem>();
		for (int i = 0; i < parts.Length; i++)
		{
			var meta = new Media3Metadata.Builder()
				.SetTitle(parts.Length > 1 ? PartTitle(i) : Title)!
				.SetArtist(parts.Length > 1 ? Title : Author)!
				.SetAlbumTitle(Title)!;
			if (Cover != null) meta.SetArtworkData(Cover, Java.Lang.Integer.ValueOf(Media3Metadata.PictureTypeFrontCover));
			items.Add(new MediaItem.Builder()
				.SetUri(Android.Net.Uri.FromFile(new Java.IO.File(parts[i])))!
				.SetMediaId(parts[i])!
				.SetMediaMetadata(meta.Build()!)!
				.Build()!);
		}
		var (index, offset) = Locate(TimeSpan.FromSeconds(book.PositionSeconds));
		controller.SetMediaItems(items, index, (long)offset.TotalMilliseconds);
		controller.SetPlaybackSpeed((float)(book.Speed ?? App.Settings.PlaybackSpeed));
		if (PlaybackService.Player is { } player) player.SkipSilenceEnabled = App.Settings.SkipSilences;
		controller.Prepare();
		PendingNext = null;
		if (play)
		{
			MarkListening();
			controller.Play();
		}
		App.Settings.Save();

		string PartTitle(int i) => Chapters.LastOrDefault(c => c.Start <= starts[i] + TimeSpan.FromMilliseconds(50))?.Title
			?? System.IO.Path.GetFileNameWithoutExtension(parts[i]);
	}

	static (MediaInfo Info, string[] Parts, TimeSpan[] Starts, TimeSpan Total, SubtitleTrack? Subtitles, string? Key, SyncedPosition? Synced)
		Read(string path, string? syncFolder)
	{
		var parts = BookSource.IsFolder(path) ? BookSource.PartsOf(path) : [path];
		if (parts.Length == 0) throw new IOException("The folder does not contain any supported audio files.");
		// Where each file starts in the book (the last entry is the book's length)
		var starts = new TimeSpan[parts.Length + 1];
		for (int i = 0; i < parts.Length; i++) starts[i + 1] = starts[i] + LengthOf(parts[i]);
		var info = BookSource.IsFolder(path) ? BookSource.ReadFolderInfo(path, parts, i => starts[i]) : BookSource.ReadFileInfo(path);
		SubtitleTrack? subtitles = null;
		try
		{
			if (BookSource.FindSubtitle(path) is { } srt) subtitles = SubtitleTrack.Load(srt);
		}
		catch { /* unreadable subtitles: the book still plays */ }
		// The same key the Windows app uses (ASIN, or name and size), and the newest position of the other devices
		var key = BookSync.KeyFor(path, info.Asin);
		SyncedPosition? synced = null;
		if (syncFolder != null && key != null)
			synced = BookSync.Find(syncFolder, key, BookSync.LegacyKeyFor(path) is { } old && old != key ? old : null);
		return (info, parts, starts[..^1], starts[^1], subtitles, key, synced);
	}

	/// <summary>A short message for the player page (where the position came from, for example).</summary>
	public string? Notice { get; private set; }
	public DateTime NoticeUntil { get; private set; }

	void ShowNotice(string text)
	{
		Notice = text;
		NoticeUntil = DateTime.UtcNow.AddSeconds(6);
	}

	static string Format(TimeSpan t) =>
		t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

	DateTime _lastSyncCheck;

	/// <summary>
	/// Back to the app with the book paused: a PC may have moved on meanwhile (as the Windows app checks when its
	/// window is activated). Moves there when its position is newer.
	/// </summary>
	public async Task CheckSyncAsync()
	{
		if (Path is not { } path || _controller == null || IsPlaying || App.Settings.SyncFolder is not { } folder) return;
		var book = App.Settings.Book(path);
		if (book.SyncKey is not { } key || DateTime.UtcNow - _lastSyncCheck < TimeSpan.FromSeconds(10)) return;
		_lastSyncCheck = DateTime.UtcNow;
		var synced = await Task.Run(() => BookSync.Find(folder, key, BookSync.LegacyKeyFor(path) is { } old && old != key ? old : null));
		// Only if nothing changed meanwhile: same book, still paused
		if (synced == null || path != Path || IsPlaying || synced.Updated <= book.EffectivePositionUpdated.AddSeconds(2)) return;
		var target = TimeSpan.FromSeconds(synced.Seconds);
		if (target >= Duration) return;
		SeekTo(target);
		book.PositionSeconds = synced.Seconds;
		book.PositionUpdated = synced.Updated;
		book.Finished = synced.Finished;
		ShowNotice($"Moved to {Format(target)}, where you stopped on {synced.Machine}");
		App.Settings.Save();
	}

	/// <summary>A file's length, from its header (Android's own reader: no decoding).</summary>
	static TimeSpan LengthOf(string file)
	{
		using var retriever = new MediaMetadataRetriever();
		retriever.SetDataSource(file);
		return long.TryParse(retriever.ExtractMetadata(MetadataKey.Duration), out var ms) ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;
	}

	/// <summary>The file playing at <paramref name="position"/> in the book, and the position in it.</summary>
	(int Index, TimeSpan Offset) Locate(TimeSpan position)
	{
		int index = 0;
		for (int i = 0; i < _partStarts.Length; i++)
			if (_partStarts[i] <= position) index = i;
		return (index, position - _partStarts[index]);
	}

	public void TogglePlay()
	{
		if (_controller == null) return;
		if (_controller.IsPlaying) _controller.Pause();
		else
		{
			// At the end: from the beginning again
			if (_controller.PlaybackState == StateEnded) _controller.SeekTo(0, 0);
			MarkListening();
			_controller.Play();
		}
		SavePosition();
	}

	/// <summary>A finished book played again is in progress once more (library, sync).</summary>
	void MarkListening()
	{
		if (Path != null && App.Settings.Book(Path) is { Finished: true } book)
		{
			book.Finished = false;
			book.PositionUpdated = DateTime.UtcNow;
		}
	}

	public void SeekTo(TimeSpan position)
	{
		if (_controller == null) return;
		position = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, Math.Max(0, Duration.Ticks - 1)));
		var (index, offset) = Locate(position);
		_controller.SeekTo(index, (long)offset.TotalMilliseconds);
	}

	public void SkipBy(TimeSpan delta) => SeekTo(Position + delta);

	public int ChapterIndexAt(TimeSpan position)
	{
		for (int i = Chapters.Count - 1; i >= 0; i--)
			if (Chapters[i].Start <= position + TimeSpan.FromMilliseconds(50)) return i;
		return -1;
	}

	/// <summary>To the start of the chapter, or of the previous one right after a chapter began.</summary>
	public void PreviousChapter()
	{
		int i = ChapterIndexAt(Position);
		if (i < 0) { SeekTo(TimeSpan.Zero); return; }
		SeekTo(Position - Chapters[i].Start > TimeSpan.FromSeconds(3) || i == 0 ? Chapters[i].Start : Chapters[i - 1].Start);
	}

	public void NextChapter()
	{
		int i = ChapterIndexAt(Position);
		if (i + 1 < Chapters.Count) SeekTo(Chapters[i + 1].Start);
	}

	/// <summary>Called on every UI tick: listening time for the statistics, and the position saved now and then.</summary>
	public void Tick(TimeSpan sinceLastTick)
	{
		if (!IsPlaying) return;
		ListeningStats.Add(App.Settings, DateTime.Now, Math.Min(sinceLastTick.TotalSeconds, 1.0));
		if (Path != null) App.Settings.Book(Path).ListenedSeconds += Math.Min(sinceLastTick.TotalSeconds, 1.0);
		if (DateTime.UtcNow - _lastSave > TimeSpan.FromSeconds(15)) SavePosition();
	}

	/// <summary>Stores the position in the book's state (the same the Windows app keeps) and saves the settings.</summary>
	public void SavePosition()
	{
		if (Path == null || _controller == null) return;
		_lastSave = DateTime.UtcNow;
		var book = App.Settings.Book(Path);
		double seconds = Position.TotalSeconds;
		if (Math.Abs(book.PositionSeconds - seconds) > 0.5) book.PositionUpdated = DateTime.UtcNow;
		book.PositionSeconds = seconds;
		book.LastOpened = DateTime.UtcNow;
		App.Settings.Save();
		// This phone's file in the shared folder (written in the background, only when something changed)
		if (App.Settings.SyncFolder is { } folder) BookSync.Publish(folder, App.Settings.Books.Values);
	}
}
