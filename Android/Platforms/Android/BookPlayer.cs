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
		return _controller = Android.Runtime.Extensions.JavaCast<MediaController>(connected!)!;
	}

	/// <summary>
	/// Opens a book where it was left, reading its details, chapters (for a folder, one per file, placed with the
	/// files' lengths) and subtitles off the UI thread.
	/// </summary>
	public async Task OpenAsync(string path, bool play)
	{
		SavePosition();
		var controller = await ControllerAsync();
		var (info, parts, starts, total, subtitles) = await Task.Run(() => Read(path));

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
		App.Settings.LastBook = path;

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
		if (play) controller.Play();
		App.Settings.Save();

		string PartTitle(int i) => Chapters.LastOrDefault(c => c.Start <= starts[i] + TimeSpan.FromMilliseconds(50))?.Title
			?? System.IO.Path.GetFileNameWithoutExtension(parts[i]);
	}

	static (MediaInfo Info, string[] Parts, TimeSpan[] Starts, TimeSpan Total, SubtitleTrack? Subtitles) Read(string path)
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
		return (info, parts, starts[..^1], starts[^1], subtitles);
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
		else _controller.Play();
		SavePosition();
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
	}
}
