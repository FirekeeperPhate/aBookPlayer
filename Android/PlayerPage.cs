namespace aBookPlayer.Droid;

/// <summary>
/// The open book: cover, the chapter and the subtitle being spoken, the position, and the controls (chapters,
/// 10 s back, play/pause, 30 s forward; speed, sleep timer, chapter list, bookmarks). Playback goes on in the
/// background when leaving it.
/// </summary>
sealed class PlayerPage : ContentPage
{
	static readonly double[] Speeds = [0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0];
	static readonly int[] SleepMinutes = [5, 10, 15, 30, 45, 60, 90];
	static readonly int[] SkipSeconds = [0, 5, 10, 15, 20, 30, 45, 60, 90, 120];

	readonly Image _cover = new() { Aspect = Aspect.AspectFit, HeightRequest = 220 };
	readonly Label _title = new() { FontSize = 20, FontAttributes = FontAttributes.Bold, TextColor = Palette.Text, HorizontalTextAlignment = TextAlignment.Center };
	readonly Label _author = new() { FontSize = 14, TextColor = Palette.TextDim, HorizontalTextAlignment = TextAlignment.Center };
	readonly Label _chapter = new() { FontSize = 14, TextColor = Palette.Accent, HorizontalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.TailTruncation };
	readonly Label _notice = new() { FontSize = 13, TextColor = Palette.TextDim, HorizontalTextAlignment = TextAlignment.Center, IsVisible = false };
	readonly Label _subtitle = new() { FontSize = 22, TextColor = Palette.Text, HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center };
	readonly Slider _seek = new() { MinimumTrackColor = Palette.Accent, MaximumTrackColor = Palette.Track, ThumbColor = Palette.Text };
	readonly Label _elapsed = new() { FontSize = 13, TextColor = Palette.TextDim };
	readonly Label _remaining = new() { FontSize = 13, TextColor = Palette.TextDim, HorizontalTextAlignment = TextAlignment.End };
	readonly Button _play = Round("▶︎", 30, 68);
	readonly Button _speed = Flat("1×");
	readonly Button _sleep = Flat("Sleep");
	readonly ToolbarItem _silences = new() { Order = ToolbarItemOrder.Secondary };
	readonly IDispatcherTimer _timer;
	string? _shownBook;
	bool _dragging;

	public PlayerPage()
	{
		Title = "Now playing";
		BackgroundColor = Palette.Back;
		var back10 = Round("−10", 17, 52);
		var fwd30 = Round("+30", 17, 52);
		var previous = Round("|◀︎", 16, 44);
		var next = Round("▶︎|", 16, 44);
		var chapters = Flat("Chapters");
		var bookmarks = Flat("Bookmarks");

		_play.Clicked += (_, _) => { App.Player.TogglePlay(); Update(); };
		back10.Clicked += (_, _) => App.Player.SkipBy(TimeSpan.FromSeconds(-10));
		fwd30.Clicked += (_, _) => App.Player.SkipBy(TimeSpan.FromSeconds(30));
		previous.Clicked += (_, _) => App.Player.PreviousChapter();
		next.Clicked += (_, _) => App.Player.NextChapter();
		_speed.Clicked += (_, _) => NextSpeed();
		_sleep.Clicked += async (_, _) => await ChooseSleepAsync();
		chapters.Clicked += async (_, _) => await ChooseChapterAsync();
		bookmarks.Clicked += async (_, _) => await ShowBookmarksAsync();
		_seek.DragStarted += (_, _) => _dragging = true;
		_seek.DragCompleted += (_, _) =>
		{
			_dragging = false;
			App.Player.SeekTo(TimeSpan.FromSeconds(_seek.Value));
		};

		_silences.Command = new Command(() => { App.Player.SkipSilences = !App.Player.SkipSilences; ShowSilences(); });
		_download.Command = new Command(async () => await DownloadAsync());
		ShowSilences();
		ToolbarItems.Add(_silences);
		ToolbarItems.Add(new ToolbarItem { Text = "Skip intro and ending…", Order = ToolbarItemOrder.Secondary, Command = new Command(async () => await ChooseSkipsAsync()) });

		var times = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)] };
		times.Add(_elapsed, 0);
		times.Add(_remaining, 1);
		// Spread over the width, so they fit narrow phones (320 dp) as well as wide ones
		var transport = Spread(previous, back10, _play, fwd30, next);
		transport.HeightRequest = 72; // the play button's height: the row does not measure it by itself
		// Equal columns (a FlexLayout of text buttons gets its height wrong and overlaps the row above)
		var extras = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)] };
		extras.Add(_speed, 0);
		extras.Add(_sleep, 1);
		extras.Add(chapters, 2);
		extras.Add(bookmarks, 3);

		// The subtitle takes the room left in the middle; the controls stay at the bottom, within thumb's reach
		var layout = new Grid
		{
			RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)],
			RowSpacing = 6, Padding = new Thickness(20, 12, 20, 20),
		};
		layout.Add(_cover, 0, 0);
		layout.Add(_title, 0, 1);
		layout.Add(_author, 0, 2);
		layout.Add(new VerticalStackLayout { Spacing = 4, Children = { _chapter, _notice } }, 0, 3);
		layout.Add(_subtitle, 0, 4);
		layout.Add(_seek, 0, 5);
		layout.Add(times, 0, 6);
		layout.Add(transport, 0, 7);
		layout.Add(extras, 0, 8);
		Content = layout;

		_timer = Dispatcher.CreateTimer();
		_timer.Interval = TimeSpan.FromMilliseconds(250);
		_timer.Tick += (_, _) => Update();
	}

	static FlexLayout Spread(params View[] views)
	{
		var flex = new FlexLayout
		{
			Direction = Microsoft.Maui.Layouts.FlexDirection.Row, JustifyContent = Microsoft.Maui.Layouts.FlexJustify.SpaceEvenly,
			AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center,
		};
		foreach (var v in views) flex.Children.Add(v);
		return flex;
	}

	static Button Round(string text, double size, double diameter) => new()
	{
		Text = text, FontSize = size, TextColor = Palette.Text, BackgroundColor = Palette.Surface, Padding = 0,
		WidthRequest = diameter, HeightRequest = diameter, CornerRadius = (int)(diameter / 2),
	};

	static Button Flat(string text) => new() { Text = text, FontSize = 14, TextColor = Palette.Text, BackgroundColor = Colors.Transparent, Padding = 0, LineBreakMode = LineBreakMode.NoWrap };

	void ShowSilences() => _silences.Text = App.Player.SkipSilences ? "Skip silences: on" : "Skip silences: off";

	readonly ToolbarItem _download = new() { Order = ToolbarItemOrder.Secondary };
	Downloads.Status _downloadStatus;
	DateTime _downloadChecked;

	/// <summary>The menu's download item for a PC's book: what it does now (asked every couple of seconds while it downloads).</summary>
	void ShowDownload(string path)
	{
		if (!RemoteBooks.IsRemote(path) || DateTime.UtcNow - _downloadChecked < TimeSpan.FromSeconds(2)) return;
		_downloadChecked = DateTime.UtcNow;
		var (status, progress) = Downloads.StatusOf(path);
		_downloadStatus = status;
		_download.Text = status switch
		{
			Downloads.Status.Downloading => $"Downloading to this phone… {progress:P0}",
			Downloads.Status.Waiting => $"Waiting for Wi-Fi to download ({progress:P0})",
			Downloads.Status.Complete => "Remove the copy on this phone",
			Downloads.Status.Failed => "Download failed: try again",
			_ => "Download to this phone",
		};
	}

	/// <summary>Copies the book to the phone (to listen away from home), stops the copy, or deletes it.</summary>
	async Task DownloadAsync()
	{
		if (App.Player.Path is not { } path || !RemoteBooks.IsRemote(path)) return;
		switch (_downloadStatus)
		{
			case Downloads.Status.Downloading or Downloads.Status.Waiting:
				if (await DisplayAlertAsync("Download", "Stop copying the book to this phone?", "Stop", "Go on")) Downloads.Remove(path);
				break;
			case Downloads.Status.Complete:
				if (await DisplayAlertAsync("Download", "Delete the book's copy on this phone? It stays on the PC, and plays streaming from there " +
					"the next time it is opened.", "Delete", "Keep")) Downloads.Remove(path);
				break;
			default:
				if (RemoteBooks.ServerOf(path) is not { } server)
				{
					await DisplayAlertAsync("Download", "This book's PC is no longer connected (Library → ⋮ → Connect to a PC).", "OK");
					return;
				}
				try
				{
					// Its progress is shown in a notification (Android 13+ asks for them once)
					if (OperatingSystem.IsAndroidVersionAtLeast(33)) await Permissions.RequestAsync<Permissions.PostNotifications>();
					await Downloads.StartAsync(path, server);
					App.Player.ShowNotice("Downloading: the phone's copy plays from the next time the book is opened");
				}
				catch (Exception ex)
				{
					await DisplayAlertAsync("Download", RemoteBooks.Explain(ex, server.Machine), "OK");
				}
				break;
		}
		_downloadChecked = DateTime.MinValue;
		ShowDownload(path);
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		// The player lost the book (its service was stopped meanwhile): load it again where it was
		if (!App.Player.IsLoaded && !App.Player.IsOpening && App.Player.Path is { } path) _ = ReopenAsync(path);
		_timer.Start();
		Update();
	}

	async Task ReopenAsync(string path)
	{
		try { await App.Player.OpenAsync(path, play: false); }
		catch (Exception ex) { await DisplayAlertAsync("aBookPlayer", "Could not open the book:\n" + ex.Message, "OK"); }
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_timer.Stop();
		App.Player.SavePosition();
	}

	void Update()
	{
		var player = App.Player;
		if (!player.IsLoaded)
		{
			_title.Text = "Opening…";
			return;
		}
		if (_shownBook != player.Path)
		{
			// A new book: its fixed details once
			_shownBook = player.Path;
			_title.Text = player.Title;
			_author.Text = player.Author ?? "";
			_cover.Source = player.Cover is { } bytes ? ImageSource.FromStream(() => new MemoryStream(bytes)) : null;
			_cover.IsVisible = player.Cover != null;
			_seek.Maximum = Math.Max(1, player.Duration.TotalSeconds);
			// A PC's book can be copied to the phone: the menu offers it
			bool remote = RemoteBooks.IsRemote(player.Path!);
			if (remote && !ToolbarItems.Contains(_download)) ToolbarItems.Insert(0, _download);
			else if (!remote) ToolbarItems.Remove(_download);
			_downloadChecked = DateTime.MinValue;
		}
		ShowDownload(player.Path!);
		var position = player.Position;
		int chapter = player.ChapterIndexAt(position);
		_chapter.Text = chapter >= 0 ? $"Chapter {chapter + 1} of {player.Chapters.Count} · {player.Chapters[chapter].Title}" : "";
		_subtitle.Text = player.Subtitles?.TextAt(position) ?? "";
		// "Continuing from 1:02:15, where you stopped on MYPC", for a few seconds
		_notice.IsVisible = player.Notice != null && DateTime.UtcNow < player.NoticeUntil;
		if (_notice.IsVisible) _notice.Text = player.Notice;
		// Finding the place in the book (a long MP3 is read up to it first)
		else if (player.IsLoadingAudio)
		{
			_notice.Text = "Loading…";
			_notice.IsVisible = true;
		}
		if (!_dragging) _seek.Value = Math.Min(position.TotalSeconds, _seek.Maximum);
		_elapsed.Text = BookPlayer.Format(_dragging ? TimeSpan.FromSeconds(_seek.Value) : position);
		_remaining.Text = "−" + BookPlayer.Format(player.Duration - position);
		_play.Text = player.IsPlaying ? "❚❚" : "▶︎";
		_speed.Text = $"{player.Speed:0.##}×";
		_sleep.Text = player.SleepStatus;
		_sleep.TextColor = player.SleepAt != null || player.SleepChapter >= 0 ? Palette.Accent : Palette.Text;
	}

	void NextSpeed()
	{
		double current = App.Player.Speed;
		int i = Array.FindIndex(Speeds, s => s > current + 0.01);
		App.Player.Speed = i >= 0 ? Speeds[i] : Speeds[0];
		App.Settings.PlaybackSpeed = App.Player.Speed;
		App.Settings.Save();
		Update();
	}

	async Task ChooseSleepAsync()
	{
		const string off = "Off", chapterEnd = "End of chapter";
		var options = SleepMinutes.Select(m => $"{m} minutes").Append(chapterEnd).ToList();
		if (App.Player.SleepAt != null || App.Player.SleepChapter >= 0) options.Insert(0, off);
		var chosen = await DisplayActionSheetAsync("Sleep timer", "Cancel", null, options.ToArray());
		if (chosen == null || chosen == "Cancel") return;
		if (chosen == off) App.Player.CancelSleep();
		else if (chosen == chapterEnd) App.Player.SetSleepAtChapterEnd();
		else App.Player.SetSleepTimer(int.Parse(chosen.Split(' ')[0]));
		Update();
	}

	async Task ChooseChapterAsync()
	{
		var chapters = App.Player.Chapters;
		if (chapters.Count == 0) return;
		var titles = chapters.Select((c, i) => $"{i + 1:00}  {c.Title}  ({BookPlayer.Format(c.Start)})").ToArray();
		var chosen = await DisplayActionSheetAsync("Chapters", "Cancel", null, titles);
		int index = Array.IndexOf(titles, chosen);
		if (index >= 0) App.Player.SeekTo(chapters[index].Start);
	}

	/// <summary>The book's bookmarks (the same the Windows app keeps): add one here, go to one, or delete it.</summary>
	async Task ShowBookmarksAsync()
	{
		var player = App.Player;
		if (!player.IsLoaded) return;
		var at = player.Position;
		var add = $"+ Add bookmark at {BookPlayer.Format(at)}";
		var marks = player.Bookmarks.ToList();
		var lines = marks.Select((m, i) => $"{i + 1}. {Describe(m)}").ToList();
		var chosen = await DisplayActionSheetAsync("Bookmarks", "Cancel", null, lines.Prepend(add).ToArray());
		if (chosen == null || chosen == "Cancel") return;
		if (chosen == add)
		{
			var note = await DisplayPromptAsync("Add bookmark", $"A note for {BookPlayer.Format(at)} (optional)", "Add", "Cancel", maxLength: 200);
			// Where it was asked for, not where playback got to while typing the note
			if (note != null && player.AddBookmark(at, note)) player.ShowNotice($"Bookmark added at {BookPlayer.Format(at)}");
			return;
		}
		int index = lines.IndexOf(chosen);
		if (index < 0) return;
		const string go = "Go there", delete = "Delete";
		var action = await DisplayActionSheetAsync(Describe(marks[index]), "Cancel", delete, go);
		if (action == go) player.SeekTo(TimeSpan.FromSeconds(marks[index].Seconds));
		else if (action == delete) player.RemoveBookmark(marks[index]);
	}

	/// <summary>"12:34 · Chapter 3 · the note".</summary>
	static string Describe(Bookmark m)
	{
		var at = TimeSpan.FromSeconds(m.Seconds);
		int chapter = App.Player.ChapterIndexAt(at);
		var parts = new List<string> { BookPlayer.Format(at) };
		if (chapter >= 0) parts.Add(App.Player.Chapters[chapter].Title);
		if (!string.IsNullOrWhiteSpace(m.Note)) parts.Add(m.Note);
		return string.Join("  ·  ", parts);
	}

	/// <summary>Seconds skipped at the start (intro, "This is Audible") and at the end (credits): this book's, or every book's.</summary>
	async Task ChooseSkipsAsync()
	{
		if (App.Player.Path is not { } path) return;
		var book = App.Settings.Book(path);
		static string Seconds(double? s) => s is > 0 ? $"{s:0} s" : "off";
		static string Own(double? s, double fallback) => s != null ? Seconds(s) : $"as every book ({Seconds(fallback)})";
		var introBook = $"Start of this book: {Own(book.SkipIntroSeconds, App.Settings.DefaultSkipIntroSeconds)}";
		var outroBook = $"End of this book: {Own(book.SkipOutroSeconds, App.Settings.DefaultSkipOutroSeconds)}";
		var introAll = $"Start of every book: {Seconds(App.Settings.DefaultSkipIntroSeconds)}";
		var outroAll = $"End of every book: {Seconds(App.Settings.DefaultSkipOutroSeconds)}";
		var which = await DisplayActionSheetAsync("Skip intro and ending", "Cancel", null, introBook, outroBook, introAll, outroAll);
		if (which == null || which == "Cancel") return;
		bool forBook = which == introBook || which == outroBook;
		const string same = "As every book";
		var choices = SkipSeconds.Select(s => s == 0 ? "Off" : $"{s} s").ToList();
		if (forBook) choices.Insert(0, same);
		var chosen = await DisplayActionSheetAsync(which.Split(':')[0], "Cancel", null, choices.ToArray());
		if (chosen == null || chosen == "Cancel") return;
		double? value = chosen == same ? null : chosen == "Off" ? 0 : double.Parse(chosen.Split(' ')[0]);
		if (which == introBook) book.SkipIntroSeconds = value;
		else if (which == outroBook) book.SkipOutroSeconds = value;
		else if (which == introAll) App.Settings.DefaultSkipIntroSeconds = value ?? 0;
		else App.Settings.DefaultSkipOutroSeconds = value ?? 0;
		App.Settings.Save();
	}
}
