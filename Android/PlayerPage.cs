namespace aBookPlayer.Droid;

/// <summary>
/// The open book: cover, the chapter and the subtitle being spoken, the position, and the controls (chapters,
/// 10 s back, play/pause, 30 s forward; speed, sleep timer, chapter list, bookmarks). Playback goes on in the
/// background when leaving it.
/// </summary>
sealed class PlayerPage : ContentPage
{
	// The speed panel's choices at a tap (as the Windows app's), and its slider's range and step
	static readonly double[] SpeedPresets = [0.75, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0];
	const double MinSpeed = 0.5, MaxSpeed = 2.5, SpeedStep = 0.05;
	static readonly int[] SleepMinutes = [5, 10, 15, 30, 45, 60, 90];
	static readonly int[] SkipSeconds = [0, 5, 10, 15, 20, 30, 45, 60, 90, 120];
	// The subtitles' size (the letters of a sentence that would not fit get smaller, down to the least)
	const double MinSubtitleSize = 14, LeastSubtitleSize = 16, MostSubtitleSize = 44;

	// The book in one small row at the top (its cover, its title, its author): the room is the subtitles'
	readonly Image _cover = new() { Aspect = Aspect.AspectFill, WidthRequest = 40, HeightRequest = 40 };
	readonly Border _coverFrame = new() { StrokeThickness = 0, WidthRequest = 40, HeightRequest = 40, IsVisible = false, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 } };
	readonly Label _title = new() { FontSize = 15, FontAttributes = FontAttributes.Bold, TextColor = Palette.Text, VerticalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1 };
	readonly Label _author = new() { FontSize = 13, TextColor = Palette.TextDim, VerticalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1 };
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

	// Panels over the player: the chapters, sliding in from the right, and the speed, from the bottom; the shade
	// behind them closes them at a tap
	enum Panel { None, Chapters, Speed, Text }
	Panel _open;
	readonly BoxView _shade = new() { Color = Colors.Black, Opacity = 0, IsVisible = false };
	readonly Grid _chaptersPanel = new() { BackgroundColor = Palette.Panel, HorizontalOptions = LayoutOptions.End, RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)] };
	readonly CollectionView _chapterList = new() { SelectionMode = SelectionMode.None };
	readonly Label _noChapters = new() { Text = "This book has no chapters.", TextColor = Palette.TextDim, FontSize = 15, Padding = new Thickness(20), IsVisible = false };
	int _listedChapter = -2;
	readonly Border _speedPanel = new() { BackgroundColor = Palette.Panel, StrokeThickness = 0, VerticalOptions = LayoutOptions.End, Padding = new Thickness(20, 16, 20, 24) };
	readonly Label _speedValue = new() { FontSize = 30, FontAttributes = FontAttributes.Bold, TextColor = Palette.Text, HorizontalTextAlignment = TextAlignment.Center };
	readonly Slider _speedSlider = new() { Minimum = MinSpeed, Maximum = MaxSpeed, MinimumTrackColor = Palette.Accent, MaximumTrackColor = Palette.Track, ThumbColor = Palette.Text };
	readonly List<Button> _speedChips = [];
	readonly Border _textPanel = new() { BackgroundColor = Palette.Panel, StrokeThickness = 0, VerticalOptions = LayoutOptions.End, Padding = new Thickness(20, 16, 20, 24) };
	readonly Label _textSample = new() { Text = "The subtitles are shown this big.", TextColor = Palette.Text, HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center, HeightRequest = 120, MaxLines = 2, LineBreakMode = LineBreakMode.TailTruncation };
	readonly Slider _textSlider = new() { Maximum = MostSubtitleSize, Minimum = LeastSubtitleSize, MinimumTrackColor = Palette.Accent, MaximumTrackColor = Palette.Track, ThumbColor = Palette.Text };

	/// <summary>A chapter in the panel: the one being played is shown in blue.</summary>
	internal sealed record ChapterRow(int Index, string Number, string Title, string Start, bool Current);

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
		_speed.Clicked += async (_, _) => await OpenAsync(Panel.Speed);
		_sleep.Clicked += async (_, _) => await ChooseSleepAsync();
		chapters.Clicked += async (_, _) => await OpenAsync(Panel.Chapters);
		// "Chapter 3 of 12 · The Storm" opens the list too
		_chapter.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await OpenAsync(Panel.Chapters)) });
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
		ToolbarItems.Add(new ToolbarItem { Text = "Subtitles size…", Order = ToolbarItemOrder.Secondary, Command = new Command(async () => await OpenAsync(Panel.Text)) });

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
			RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)],
			RowSpacing = 6, Padding = new Thickness(20, 10, 20, 20),
		};
		// The title takes the room the author leaves (who has at most two fifths of the row: see SizeChanged)
		_coverFrame.Content = _cover;
		var book = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], HeightRequest = 40 };
		// (Their own margins, not the columns' spacing: a book without a cover starts at the edge)
		_coverFrame.Margin = new Thickness(0, 0, 10, 0);
		_author.Margin = new Thickness(10, 0, 0, 0);
		book.Add(_coverFrame, 0);
		book.Add(_title, 1);
		book.Add(_author, 2);
		layout.Add(book, 0, 0);
		layout.Add(new VerticalStackLayout { Spacing = 4, Children = { _chapter, _notice } }, 0, 1);
		layout.Add(_subtitle, 0, 2);
		layout.Add(_seek, 0, 3);
		layout.Add(times, 0, 4);
		layout.Add(transport, 0, 5);
		layout.Add(extras, 0, 6);

		// A swipe to the left brings in the chapters (from the right edge, as a drawer); one to the right goes back to
		// the library, or closes the chapters when they are open
		layout.GestureRecognizers.Add(new SwipeGestureRecognizer { Direction = SwipeDirection.Left, Command = new Command(async () => await OpenAsync(Panel.Chapters)) });
		layout.GestureRecognizers.Add(new SwipeGestureRecognizer { Direction = SwipeDirection.Right, Command = new Command(async () => await Navigation.PopAsync()) });
		_shade.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await CloseAsync()) });
		_shade.GestureRecognizers.Add(new SwipeGestureRecognizer { Direction = SwipeDirection.Right, Command = new Command(async () => await CloseAsync()) });
		BuildChaptersPanel();
		BuildSpeedPanel();
		BuildTextPanel();
		var root = new Grid();
		root.Add(layout);
		root.Add(_shade);
		root.Add(_chaptersPanel);
		root.Add(_speedPanel);
		root.Add(_textPanel);
		// Hidden off the screen until opened (at the page's size, known once laid out)
		_chaptersPanel.TranslationX = 10_000;
		_speedPanel.TranslationY = 10_000;
		_textPanel.TranslationY = 10_000;
		SizeChanged += (_, _) =>
		{
			_chaptersPanel.WidthRequest = Math.Min(380, Width * 0.85);
			if (_open != Panel.Chapters) _chaptersPanel.TranslationX = _chaptersPanel.WidthRequest;
			if (_open != Panel.Speed) _speedPanel.TranslationY = Height;
			if (_open != Panel.Text) _textPanel.TranslationY = Height;
			_author.MaximumWidthRequest = Math.Max(60, Width * 0.4);
		};
		Content = root;

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
			_coverFrame.IsVisible = player.Cover != null;
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
		// Playback moved on to another chapter while the list is open: the blue one follows
		if (_open == Panel.Chapters && chapter != _listedChapter) ShowChapters(scroll: false);
		ShowSubtitle(player.Subtitles?.TextAt(position) ?? "");
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
		_speed.Text = FormatSpeed(player.Speed);
		_speed.TextColor = Math.Abs(player.Speed - 1) < 0.001 ? Palette.Text : Palette.Accent;
		_sleep.Text = player.SleepStatus;
		_sleep.TextColor = player.SleepAt != null || player.SleepChapter >= 0 ? Palette.Accent : Palette.Text;
	}

	// ───────────────────────────── Chapters and speed panels ─────────────────────────────

	void BuildChaptersPanel()
	{
		var header = new Label { Text = "Chapters", FontSize = 20, FontAttributes = FontAttributes.Bold, TextColor = Palette.Text, Padding = new Thickness(20, 18, 20, 10) };
		_chapterList.ItemTemplate = new DataTemplate(() =>
		{
			var number = new Label { FontSize = 14, TextColor = Palette.TextDim, VerticalTextAlignment = TextAlignment.Center, WidthRequest = 30 };
			number.SetBinding(Label.TextProperty, static (ChapterRow r) => r.Number);
			var title = new Label { FontSize = 16, VerticalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 2 };
			title.SetBinding(Label.TextProperty, static (ChapterRow r) => r.Title);
			title.SetBinding(Label.TextColorProperty, static (ChapterRow r) => r.Current, converter: new FuncConverter<bool, Color>(c => c ? Palette.Accent : Palette.Text));
			title.SetBinding(Label.FontAttributesProperty, static (ChapterRow r) => r.Current, converter: new FuncConverter<bool, FontAttributes>(c => c ? FontAttributes.Bold : FontAttributes.None));
			var start = new Label { FontSize = 13, TextColor = Palette.TextDim, VerticalTextAlignment = TextAlignment.Center };
			start.SetBinding(Label.TextProperty, static (ChapterRow r) => r.Start);
			// The chapter being played: a blue bar at its left, and its title in blue
			var mark = new BoxView { Color = Palette.Accent, WidthRequest = 4 };
			mark.SetBinding(IsVisibleProperty, static (ChapterRow r) => r.Current);
			var row = new Grid { ColumnDefinitions = [new(4), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 12, Padding = new Thickness(0, 12, 20, 12) };
			row.Add(mark, 0);
			row.Add(number, 1);
			row.Add(title, 2);
			row.Add(start, 3);
			// A tap goes to the chapter (not the list's selection, which a swipe to close would set off too)
			var tap = new TapGestureRecognizer();
			tap.Tapped += async (_, _) =>
			{
				if (row.BindingContext is not ChapterRow chosen || _open != Panel.Chapters) return;
				if (chosen.Index < App.Player.Chapters.Count) App.Player.SeekTo(App.Player.Chapters[chosen.Index].Start);
				await CloseAsync();
			};
			row.GestureRecognizers.Add(tap);
			return row;
		});
		// Swiped back to the right: closed (the list takes the touches over its rows, so it listens too)
		_chaptersPanel.GestureRecognizers.Add(new SwipeGestureRecognizer { Direction = SwipeDirection.Right, Command = new Command(async () => await CloseAsync()) });
		_chapterList.GestureRecognizers.Add(new SwipeGestureRecognizer { Direction = SwipeDirection.Right, Command = new Command(async () => await CloseAsync()) });
		_chaptersPanel.Add(header, 0, 0);
		_chaptersPanel.Add(_chapterList, 0, 1);
		_chaptersPanel.Add(_noChapters, 0, 1);
	}

	/// <summary>The chapters, the one being played marked; listed again when playback moves to another.</summary>
	void ShowChapters(bool scroll)
	{
		var player = App.Player;
		int current = player.ChapterIndexAt(player.Position);
		_listedChapter = current;
		var rows = player.Chapters.Select((c, i) => new ChapterRow(i, $"{i + 1}", c.Title, BookPlayer.Format(c.Start), i == current)).ToList();
		_chapterList.ItemsSource = rows;
		_noChapters.IsVisible = rows.Count == 0;
		if (scroll && current >= 0) _chapterList.ScrollTo(current, position: ScrollToPosition.Center, animate: false);
	}

	void BuildSpeedPanel()
	{
		var title = new Label { Text = "Speed", FontSize = 16, TextColor = Palette.TextDim, HorizontalTextAlignment = TextAlignment.Center };
		var slower = RoundButton("−");
		var faster = RoundButton("+");
		slower.Clicked += (_, _) => SetSpeed(App.Player.Speed - SpeedStep);
		faster.Clicked += (_, _) => SetSpeed(App.Player.Speed + SpeedStep);
		_speedSlider.ValueChanged += (_, e) =>
		{
			// In steps of 0.05, as it is dragged
			double stepped = Math.Round(e.NewValue / SpeedStep) * SpeedStep;
			if (Math.Abs(stepped - App.Player.Speed) > 0.001) SetSpeed(stepped, moveSlider: false);
		};
		var slider = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 10 };
		slider.Add(slower, 0);
		slider.Add(_speedSlider, 1);
		slider.Add(faster, 2);
		var chips = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, JustifyContent = Microsoft.Maui.Layouts.FlexJustify.Center };
		foreach (var preset in SpeedPresets)
		{
			var chip = new Button
			{
				Text = FormatSpeed(preset), FontSize = 15, TextColor = Palette.Text, BackgroundColor = Palette.Surface, CornerRadius = 18,
				HeightRequest = 38, Padding = new Thickness(14, 0), Margin = new Thickness(4), CommandParameter = preset,
			};
			chip.Clicked += (_, _) => SetSpeed(preset);
			_speedChips.Add(chip);
			chips.Children.Add(chip);
		}
		var done = new Button { Text = "Done", FontSize = 15, TextColor = Colors.White, BackgroundColor = Palette.Accent, CornerRadius = 8, Margin = new Thickness(0, 8, 0, 0) };
		done.Clicked += async (_, _) => await CloseAsync();
		_speedPanel.StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(16, 16, 0, 0) };
		_speedPanel.Content = new VerticalStackLayout { Spacing = 12, Children = { title, _speedValue, slider, chips, done } };

		static Button RoundButton(string text) => new()
		{
			Text = text, FontSize = 22, TextColor = Palette.Text, BackgroundColor = Palette.Surface, Padding = 0,
			WidthRequest = 44, HeightRequest = 44, CornerRadius = 22,
		};
	}

	string? _fitted;

	/// <summary>
	/// The sentence being spoken, in the size chosen (⋮ → Subtitles size), or in the largest smaller one in which it
	/// fits between the chapter and the seek bar.
	/// </summary>
	void ShowSubtitle(string line)
	{
		double width = _subtitle.Width, height = _subtitle.Height, wanted = App.Settings.SubtitleSize;
		// (Measured again only for another sentence, another size, or the screen turned)
		string fitted = $"{wanted}|{width:0}|{height:0}|{line}";
		if (fitted == _fitted) return;
		_fitted = fitted;
		_subtitle.Text = line;
		double size = wanted;
		if (line.Length > 0 && width > 0 && height > 0)
		{
			for (; size > MinSubtitleSize; size--)
			{
				_subtitle.FontSize = size;
				if (_subtitle.Measure(width, double.PositiveInfinity).Height <= height) break;
			}
		}
		_subtitle.FontSize = size;
	}

	void BuildTextPanel()
	{
		var title = new Label { Text = "Subtitles size", FontSize = 16, TextColor = Palette.TextDim, HorizontalTextAlignment = TextAlignment.Center };
		var smaller = Circle("A−", 17);
		var larger = Circle("A+", 17);
		smaller.Clicked += (_, _) => SetTextSize(App.Settings.SubtitleSize - 1);
		larger.Clicked += (_, _) => SetTextSize(App.Settings.SubtitleSize + 1);
		_textSlider.ValueChanged += (_, e) =>
		{
			double stepped = Math.Round(e.NewValue);
			if (Math.Abs(stepped - App.Settings.SubtitleSize) > 0.01) SetTextSize(stepped, moveSlider: false);
		};
		var slider = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 10 };
		slider.Add(smaller, 0);
		slider.Add(_textSlider, 1);
		slider.Add(larger, 2);
		var standard = new Button
		{
			Text = "Standard size", FontSize = 15, TextColor = Palette.Text, BackgroundColor = Palette.Surface, CornerRadius = 18, HeightRequest = 38,
			Padding = new Thickness(14, 0), HorizontalOptions = LayoutOptions.Center,
		};
		standard.Clicked += (_, _) => SetTextSize(MobileSettings.StandardSubtitleSize);
		var done = new Button { Text = "Done", FontSize = 15, TextColor = Colors.White, BackgroundColor = Palette.Accent, CornerRadius = 8, Margin = new Thickness(0, 8, 0, 0) };
		done.Clicked += async (_, _) => await CloseAsync();
		_textPanel.StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(16, 16, 0, 0) };
		_textPanel.Content = new VerticalStackLayout { Spacing = 12, Children = { title, _textSample, slider, standard, done } };
	}

	static Button Circle(string text, double size) => new()
	{
		Text = text, FontSize = size, TextColor = Palette.Text, BackgroundColor = Palette.Surface, Padding = 0,
		WidthRequest = 44, HeightRequest = 44, CornerRadius = 22,
	};

	/// <summary>The subtitles' size, for every book; a sentence too long for it is shown smaller (see <see cref="ShowSubtitle"/>).</summary>
	void SetTextSize(double size, bool moveSlider = true)
	{
		App.Settings.SubtitleSize = Math.Clamp(Math.Round(size), LeastSubtitleSize, MostSubtitleSize);
		ShowTextSize(moveSlider);
		Update();
	}

	void ShowTextSize(bool moveSlider = true)
	{
		double size = Math.Clamp(App.Settings.SubtitleSize, LeastSubtitleSize, MostSubtitleSize);
		_textSample.FontSize = size;
		if (moveSlider) _textSlider.Value = size;
	}

	static string FormatSpeed(double speed) => $"{speed:0.##}×";

	/// <summary>The book's speed (also the default for books not played yet), shown in the panel.</summary>
	void SetSpeed(double speed, bool moveSlider = true)
	{
		speed = Math.Clamp(Math.Round(speed / SpeedStep) * SpeedStep, MinSpeed, MaxSpeed);
		App.Player.Speed = speed;
		App.Settings.PlaybackSpeed = speed;
		ShowSpeed(moveSlider);
		Update();
	}

	void ShowSpeed(bool moveSlider = true)
	{
		double speed = App.Player.Speed;
		_speedValue.Text = FormatSpeed(speed);
		if (moveSlider) _speedSlider.Value = speed;
		foreach (var chip in _speedChips)
			chip.BackgroundColor = Math.Abs((double)chip.CommandParameter - speed) < 0.001 ? Palette.Accent : Palette.Surface;
	}

	async Task OpenAsync(Panel panel)
	{
		if (_open == panel || !App.Player.IsLoaded) return;
		if (_open != Panel.None) await CloseAsync();
		_open = panel;
		_shade.IsVisible = true;
		var shade = _shade.FadeToAsync(0.5, 180);
		if (panel == Panel.Chapters)
		{
			ShowChapters(scroll: true);
			await Task.WhenAll(shade, _chaptersPanel.TranslateToAsync(0, 0, 220, Easing.CubicOut));
		}
		else if (panel == Panel.Speed)
		{
			ShowSpeed();
			await Task.WhenAll(shade, _speedPanel.TranslateToAsync(0, 0, 220, Easing.CubicOut));
		}
		else
		{
			ShowTextSize();
			await Task.WhenAll(shade, _textPanel.TranslateToAsync(0, 0, 220, Easing.CubicOut));
		}
	}

	async Task CloseAsync()
	{
		if (_open == Panel.None) return;
		var panel = _open;
		_open = Panel.None;
		var shade = _shade.FadeToAsync(0, 180);
		if (panel == Panel.Chapters) await Task.WhenAll(shade, _chaptersPanel.TranslateToAsync(_chaptersPanel.Width, 0, 200, Easing.CubicIn));
		else
		{
			App.Settings.Save();
			await Task.WhenAll(shade, (panel == Panel.Speed ? _speedPanel : _textPanel).TranslateToAsync(0, Height, 200, Easing.CubicIn));
		}
		if (_open == Panel.None) _shade.IsVisible = false;
	}

	/// <summary>
	/// With gesture navigation a swipe from either edge is Android's "back": in the middle of the right edge it is
	/// left to the page, for the chapters (as apps with a drawer do; Android allows 200 dp of an edge).
	/// </summary>
	protected override void OnHandlerChanged()
	{
		base.OnHandlerChanged();
		if (Handler?.PlatformView is not Android.Views.View view || !OperatingSystem.IsAndroidVersionAtLeast(29)) return;
		view.LayoutChange += (_, _) =>
		{
			float dp = view.Resources?.DisplayMetrics?.Density ?? 1;
			int width = view.Width, height = view.Height, band = (int)(200 * dp), edge = (int)(40 * dp);
			int top = Math.Max(0, (height - band) / 2);
			if (OperatingSystem.IsAndroidVersionAtLeast(29)) view.SystemGestureExclusionRects = [new Android.Graphics.Rect(width - edge, top, width, top + band)];
		};
	}

	/// <summary>Back (the system's button or gesture) closes an open panel first.</summary>
	protected override bool OnBackButtonPressed()
	{
		if (_open == Panel.None) return base.OnBackButtonPressed();
		_ = CloseAsync();
		return true;
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
