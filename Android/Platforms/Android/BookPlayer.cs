using Android.Content;
using Android.Media;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer.Source;
using AndroidX.Media3.Session;
using Media3Metadata = AndroidX.Media3.Common.MediaMetadata; // Core has a MediaMetadata of its own (the tag reader)

namespace aBookPlayer.Droid;

/// <summary>
/// The open book, as the pages see it: one continuous book with its chapters and subtitles. The player plays a
/// folder's files joined into one item, so the notification, the lock screen and Bluetooth show the position in the
/// whole book. Talks to <see cref="PlaybackService"/> through a media controller.
/// </summary>
sealed class BookPlayer
{
	/// <summary>Media3's Player.STATE_ENDED (the binding does not bring the interface's constants).</summary>
	const int StateEnded = 4;
	/// <summary>The sleep timer's last seconds fade out.</summary>
	const double SleepFadeSeconds = 10;

	MediaController? _controller;
	DateTime _lastSave;

	public string? Path { get; private set; }
	public string Title { get; private set; } = "";
	public string? Author { get; private set; }
	public byte[]? Cover { get; private set; }
	public IReadOnlyList<Chapter> Chapters { get; private set; } = [];
	public SubtitleTrack? Subtitles { get; private set; }
	public TimeSpan Duration { get; private set; }

	/// <summary>A book is in the player (not after the service was stopped: it must then be opened again).</summary>
	public bool IsLoaded => Path != null && _controller is { IsConnected: true };
	/// <summary>A book is being opened (its files read): another open waits for it to finish.</summary>
	public bool IsOpening { get; private set; }
	public bool IsPlaying => _controller?.IsPlaying == true;

	/// <summary>Where the book is (its files are one item: the item's position is the book's).</summary>
	public TimeSpan Position =>
		_controller == null || Path == null ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Max(0, _controller.CurrentPosition));

	public double Speed
	{
		get => _controller?.PlaybackParameters?.Speed ?? 1f;
		set
		{
			_controller?.SetPlaybackSpeed((float)value);
			if (Path != null) App.Settings.Book(Path).Speed = value;
		}
	}

	/// <summary>Silences shortened (pauses between sentences and chapters), for all books.</summary>
	public bool SkipSilences
	{
		get => App.Settings.SkipSilences;
		set
		{
			App.Settings.SkipSilences = value;
			if (PlaybackService.Player is { } player) player.SkipSilenceEnabled = value;
			App.Settings.Save();
		}
	}

	Task<MediaController>? _connecting;

	/// <summary>
	/// Connects to the playback service (starting it), and again if the service was stopped meanwhile; callers
	/// arriving while a connection is being made share it.
	/// </summary>
	Task<MediaController> ControllerAsync()
	{
		if (_controller is { IsConnected: true }) return Task.FromResult(_controller);
		if (_connecting is { IsCompleted: false }) return _connecting;
		return _connecting = ConnectAsync();
	}

	async Task<MediaController> ConnectAsync()
	{
		_controller?.Release();
		_controller = null;
		var context = Platform.AppContext;
		var token = new SessionToken(context, new ComponentName(context, Java.Lang.Class.FromType(typeof(PlaybackService))));
		var future = new MediaController.Builder(context, token).BuildAsync();
		// Waiting for the connection must not block the UI thread, which delivers it
		var connected = await Task.Run(() => future!.Get());
		_controller = Android.Runtime.Extensions.JavaCast<MediaController>(connected!)!;
		// Watches the book (its end, the sleep timer, the ending to skip), also while the app is in the background.
		// Not with a Player.Listener: the binding cannot call the Java interface's default methods, which Media3 calls.
		if (_watch == null)
		{
			_watch = Application.Current!.Dispatcher.CreateTimer();
			_watch.Interval = TimeSpan.FromMilliseconds(500);
			_watch.Tick += (_, _) => Watch();
			_watch.Start();
		}
		return _controller;
	}

	IDispatcherTimer? _watch;
	int _lastState;
	TimeSpan _lastPosition;
	DateTime _lastWatch = DateTime.UtcNow;

	void Watch()
	{
		var now = DateTime.UtcNow;
		var elapsed = now - _lastWatch;
		_lastWatch = now;
		if (!IsLoaded || IsOpening) return;
		int state = _controller!.PlaybackState;
		var position = Position;
		var last = _lastPosition;
		_lastPosition = position;
		bool ended = state == StateEnded && _lastState != StateEnded;
		_lastState = state;
		if (ended)
		{
			_ = OnEndedAsync();
			return;
		}
		// A jump made outside the app (the lock screen's seek bar): the sleep timer follows it to its chapter
		if ((position - last).Duration() > TimeSpan.FromSeconds(3)) FollowSleepChapter(position);
		if (!IsPlaying)
		{
			// A deadline passed while paused: nothing left to stop
			if (SleepAt is { } at && now >= at) CancelSleep();
			return;
		}
		var book = App.Settings.Book(Path!);
		// A finished book played again from the notification, the lock screen or a headset (not through TogglePlay):
		// past its intro when it starts over, and in progress once more
		if (book.Finished && position < EndOf(book, Duration))
		{
			if (position < TimeSpan.FromSeconds(2) && StartOf(book, Duration) is var restart && restart > TimeSpan.Zero) SeekTo(restart);
			MarkListening();
		}
		// Listening time (statistics) and the position saved now and then
		double seconds = Math.Min(elapsed.TotalSeconds, 1.0);
		ListeningStats.Add(App.Settings, DateTime.Now, seconds);
		book.ListenedSeconds += seconds;
		if (now - _lastSave > TimeSpan.FromSeconds(15)) SavePosition();

		// Playing into the ending to skip ends the book there (a jump into it, to hear it after all, plays on)
		var outro = OutroOf(book);
		if (outro > TimeSpan.Zero && outro < Duration)
		{
			var start = Duration - outro;
			if (last < start && position >= start && position - last < TimeSpan.FromSeconds(3))
			{
				_controller.Pause();
				_ = OnEndedAsync();
				return;
			}
		}
		WatchSleep(position, now);
	}

	// ───────────────────────────── End of the book ─────────────────────────────

	/// <summary>The book to go on with, found when the last one ended: offered once the app is in front.</summary>
	public (string Finished, SeriesCandidate Next)? PendingNext { get; set; }
	public event Action? NextFound;

	/// <summary>
	/// Played to the end (or to the ending skipped): the book is finished (for the library and the PCs), and the
	/// next one of its series, if there is one, is looked for.
	/// </summary>
	async Task OnEndedAsync()
	{
		if (Path is not { } path) return;
		CancelSleep();
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

	// ───────────────────────────── Intro and ending ─────────────────────────────

	public static TimeSpan IntroOf(BookState book) => TimeSpan.FromSeconds(Math.Max(0, book.SkipIntroSeconds ?? App.Settings.DefaultSkipIntroSeconds));
	public static TimeSpan OutroOf(BookState book) => TimeSpan.FromSeconds(Math.Max(0, book.SkipOutroSeconds ?? App.Settings.DefaultSkipOutroSeconds));

	/// <summary>Where a book counts as over: its last 5 seconds, or the ending skipped when longer.</summary>
	static TimeSpan EndOf(BookState book, TimeSpan duration) =>
		duration - (OutroOf(book) > TimeSpan.FromSeconds(5) ? OutroOf(book) : TimeSpan.FromSeconds(5));

	/// <summary>Where a book starts: past its intro, when that leaves something to hear.</summary>
	TimeSpan StartOf(BookState book, TimeSpan duration)
	{
		var intro = IntroOf(book);
		return intro > TimeSpan.Zero && intro < duration - OutroOf(book) ? intro : TimeSpan.Zero;
	}

	// ───────────────────────────── Opening ─────────────────────────────

	/// <summary>
	/// Opens a book where it was left, reading its details, chapters (for a folder, one per file, placed with the
	/// files' lengths) and subtitles off the UI thread. Ignored while another book is being opened (a second tap).
	/// </summary>
	public async Task OpenAsync(string path, bool play)
	{
		if (IsOpening) return;
		// The book playing until now keeps its place (not while opening: the player still has the old book)
		SavePosition();
		IsOpening = true;
		try { await LoadAsync(path, play); }
		finally { IsOpening = false; }
	}

	async Task LoadAsync(string path, bool play)
	{
		var controller = await ControllerAsync();
		var syncFolder = App.Settings.SyncFolder;
		var (info, parts, lengths, total, subtitles, key, synced) = await Task.Run(() => Read(path, syncFolder));
		var exo = PlaybackService.Player ?? throw new InvalidOperationException("The player could not be started.");

		CancelSleep();
		Path = path;
		Title = string.IsNullOrWhiteSpace(info.Title) ? BookSource.DisplayName(path) : info.Title!;
		Author = info.Artist;
		Cover = info.Cover;
		Chapters = info.Chapters.OrderBy(c => c.Start).ToList();
		Subtitles = subtitles;
		Duration = total;

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
		// Not started, or left at the very end or in the ending skipped (finished): from the beginning, past the intro
		var position = TimeSpan.FromSeconds(book.PositionSeconds);
		var end = EndOf(book, total);
		if (position <= TimeSpan.FromSeconds(1) || position >= end) position = StartOf(book, total);

		// One item for the whole book, its files joined: the notification shows the book and where it is in it
		var meta = new Media3Metadata.Builder().SetTitle(Title)!.SetArtist(Author)!.SetAlbumTitle(Title)!;
		if (Cover != null) meta.SetArtworkData(Cover, Java.Lang.Integer.ValueOf(Media3Metadata.PictureTypeFrontCover));
		var item = new MediaItem.Builder().SetMediaId(path)!.SetMediaMetadata(meta.Build()!)!.Build()!;
		var source = new ConcatenatingMediaSource2.Builder().UseDefaultMediaSourceFactory(Platform.AppContext)!.SetMediaItem(item)!;
		for (int i = 0; i < parts.Length; i++)
			source.Add(MediaItem.FromUri(Android.Net.Uri.FromFile(new Java.IO.File(parts[i])))!,
				lengths[i] > TimeSpan.Zero ? (long)lengths[i].TotalMilliseconds : C.TimeUnset);
		exo.SetMediaSource(source.Build(), (long)position.TotalMilliseconds);
		exo.SkipSilenceEnabled = App.Settings.SkipSilences;
		controller.SetPlaybackSpeed((float)(book.Speed ?? App.Settings.PlaybackSpeed));
		controller.Prepare();
		_lastPosition = position;
		PendingNext = null;
		if (play)
		{
			MarkListening();
			controller.Play();
		}
		App.Settings.Save();
	}

	static (MediaInfo Info, string[] Parts, TimeSpan[] Lengths, TimeSpan Total, SubtitleTrack? Subtitles, string? Key, SyncedPosition? Synced)
		Read(string path, string? syncFolder)
	{
		var parts = BookSource.IsFolder(path) ? BookSource.PartsOf(path) : [path];
		if (parts.Length == 0) throw new IOException("The folder does not contain any supported audio files.");
		// Where each file starts in the book (the last entry is the book's length)
		var lengths = parts.Select(LengthOf).ToArray();
		var starts = new TimeSpan[parts.Length + 1];
		for (int i = 0; i < parts.Length; i++) starts[i + 1] = starts[i] + lengths[i];
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
		return (info, parts, lengths, starts[^1], subtitles, key, synced);
	}

	/// <summary>A file's length, from its header (Android's own reader: no decoding).</summary>
	static TimeSpan LengthOf(string file)
	{
		using var retriever = new MediaMetadataRetriever();
		retriever.SetDataSource(file);
		return long.TryParse(retriever.ExtractMetadata(MetadataKey.Duration), out var ms) ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;
	}

	// ───────────────────────────── Notices and sync ─────────────────────────────

	/// <summary>A short message for the player page (where the position came from, for example).</summary>
	public string? Notice { get; private set; }
	public DateTime NoticeUntil { get; private set; }

	public void ShowNotice(string text)
	{
		Notice = text;
		NoticeUntil = DateTime.UtcNow.AddSeconds(6);
	}

	public static string Format(TimeSpan t)
	{
		if (t < TimeSpan.Zero) t = TimeSpan.Zero;
		return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
	}

	DateTime _lastSyncCheck;

	/// <summary>
	/// Back to the app with the book paused: a PC may have moved on meanwhile (as the Windows app checks when its
	/// window is activated). Moves there when its position is newer.
	/// </summary>
	public async Task CheckSyncAsync()
	{
		if (Path is not { } path || !IsLoaded || IsOpening || IsPlaying || App.Settings.SyncFolder is not { } folder) return;
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

	// ───────────────────────────── Controls ─────────────────────────────

	public void TogglePlay()
	{
		if (_controller == null || Path == null) return;
		if (_controller.IsPlaying) _controller.Pause();
		else
		{
			// At the end (or in the ending skipped, finished), or from the very start: from the beginning, past the intro
			var book = App.Settings.Book(Path);
			if (_controller.PlaybackState == StateEnded || Position < TimeSpan.FromSeconds(1) || (book.Finished && Position >= EndOf(book, Duration)))
				SeekTo(StartOf(book, Duration));
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
		_controller.SeekTo((long)position.TotalMilliseconds);
		_lastPosition = position;
		FollowSleepChapter(position);
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

	// ───────────────────────────── Sleep timer ─────────────────────────────

	/// <summary>When playback pauses by itself (null = no deadline).</summary>
	public DateTime? SleepAt { get; private set; }
	/// <summary>The chapter whose end pauses playback (-1 = none).</summary>
	public int SleepChapter { get; private set; } = -1;

	public void SetSleepTimer(int minutes)
	{
		CancelSleep();
		if (minutes > 0) SleepAt = DateTime.UtcNow.AddMinutes(minutes);
	}

	public void SetSleepAtChapterEnd()
	{
		CancelSleep();
		SleepChapter = Math.Max(0, ChapterIndexAt(Position));
	}

	public void CancelSleep()
	{
		SleepAt = null;
		SleepChapter = -1;
		if (_controller != null) _controller.Volume = 1f;
	}

	/// <summary>A jump elsewhere (chapter list, seeking): the timer then stops at the end of the chapter landed in.</summary>
	void FollowSleepChapter(TimeSpan target)
	{
		if (SleepChapter >= 0) SleepChapter = Math.Max(0, ChapterIndexAt(target));
	}

	void WatchSleep(TimeSpan position, DateTime now)
	{
		if (_controller == null) return;
		double remaining;
		if (SleepAt is { } at) remaining = (at - now).TotalSeconds;
		else if (SleepChapter >= 0)
		{
			// Chapter ends: the next one's start, or the book's end
			var end = SleepChapter + 1 < Chapters.Count ? Chapters[SleepChapter + 1].Start : Duration;
			remaining = (end - position).TotalSeconds / Math.Max(0.1, Speed);
		}
		else return;
		if (remaining <= 0)
		{
			_controller.Pause();
			CancelSleep();
			SavePosition();
			ShowNotice("Sleep timer: playback paused");
			return;
		}
		_controller.Volume = remaining < SleepFadeSeconds ? (float)(remaining / SleepFadeSeconds) : 1f;
	}

	/// <summary>"Sleep 14:59", "Sleep: chap." (end of the chapter), or "Sleep" when off.</summary>
	public string SleepStatus =>
		SleepAt is { } at ? "Sleep " + Format(at - DateTime.UtcNow)
		: SleepChapter >= 0 ? "Sleep: chap."
		: "Sleep";

	// ───────────────────────────── Bookmarks ─────────────────────────────

	public IReadOnlyList<Bookmark> Bookmarks => Path != null ? App.Settings.Book(Path).Bookmarks : [];

	public bool AddBookmark(TimeSpan at, string note)
	{
		if (Path == null) return false;
		var list = App.Settings.Book(Path).Bookmarks;
		list.Add(new Bookmark { Seconds = at.TotalSeconds, Note = note.Trim(), Created = DateTime.UtcNow });
		list.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));
		App.Settings.Save();
		return true;
	}

	public void RemoveBookmark(Bookmark mark)
	{
		if (Path == null) return;
		App.Settings.Book(Path).Bookmarks.Remove(mark);
		App.Settings.Save();
	}

	// ───────────────────────────── Saving ─────────────────────────────

	/// <summary>Stores the position in the book's state (the same the Windows app keeps) and saves the settings.</summary>
	public void SavePosition()
	{
		// Not with a disconnected player (its position is not the book's), nor halfway through opening another book
		if (!IsLoaded || IsOpening || Path == null) return;
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
