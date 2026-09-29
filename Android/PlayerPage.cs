namespace aBookPlayer.Droid;

/// <summary>
/// The open book: cover, the chapter and the subtitle being spoken, the position, and the controls (chapters,
/// 10 s back, play/pause, 30 s forward, speed, chapter list). Playback goes on in the background when leaving it.
/// </summary>
sealed class PlayerPage : ContentPage
{
	static readonly double[] Speeds = [0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0];

	readonly Image _cover = new() { Aspect = Aspect.AspectFit, HeightRequest = 220 };
	readonly Label _title = new() { FontSize = 20, FontAttributes = FontAttributes.Bold, TextColor = Palette.Text, HorizontalTextAlignment = TextAlignment.Center };
	readonly Label _author = new() { FontSize = 14, TextColor = Palette.TextDim, HorizontalTextAlignment = TextAlignment.Center };
	readonly Label _chapter = new() { FontSize = 14, TextColor = Palette.Accent, HorizontalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.TailTruncation };
	readonly Label _subtitle = new() { FontSize = 22, TextColor = Palette.Text, HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center };
	readonly Slider _seek = new() { MinimumTrackColor = Palette.Accent, MaximumTrackColor = Palette.Track, ThumbColor = Palette.Text };
	readonly Label _elapsed = new() { FontSize = 13, TextColor = Palette.TextDim };
	readonly Label _remaining = new() { FontSize = 13, TextColor = Palette.TextDim, HorizontalTextAlignment = TextAlignment.End };
	readonly Button _play = Round("▶︎", 30, 68);
	readonly Button _speed = Flat("1×");
	readonly IDispatcherTimer _timer;
	string? _shownBook;
	bool _dragging;
	DateTime _lastTick = DateTime.UtcNow;

	public PlayerPage()
	{
		Title = "Now playing";
		BackgroundColor = Palette.Back;
		var back10 = Round("−10", 17, 52);
		var fwd30 = Round("+30", 17, 52);
		var previous = Round("|◀︎", 16, 44);
		var next = Round("▶︎|", 16, 44);
		var chapters = Flat("Chapters");

		_play.Clicked += (_, _) => { App.Player.TogglePlay(); Update(); };
		back10.Clicked += (_, _) => App.Player.SkipBy(TimeSpan.FromSeconds(-10));
		fwd30.Clicked += (_, _) => App.Player.SkipBy(TimeSpan.FromSeconds(30));
		previous.Clicked += (_, _) => App.Player.PreviousChapter();
		next.Clicked += (_, _) => App.Player.NextChapter();
		_speed.Clicked += (_, _) => NextSpeed();
		chapters.Clicked += async (_, _) => await ChooseChapterAsync();
		_seek.DragStarted += (_, _) => _dragging = true;
		_seek.DragCompleted += (_, _) =>
		{
			_dragging = false;
			App.Player.SeekTo(TimeSpan.FromSeconds(_seek.Value));
		};

		var times = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)] };
		times.Add(_elapsed, 0);
		times.Add(_remaining, 1);
		// Spread over the width, so they fit narrow phones (320 dp) as well as wide ones
		var transport = new FlexLayout
		{
			Direction = Microsoft.Maui.Layouts.FlexDirection.Row, JustifyContent = Microsoft.Maui.Layouts.FlexJustify.SpaceEvenly,
			AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center, Children = { previous, back10, _play, fwd30, next },
		};
		var extras = new HorizontalStackLayout { Spacing = 24, HorizontalOptions = LayoutOptions.Center, Children = { _speed, chapters } };

		// The subtitle takes the room left in the middle; the controls stay at the bottom, within thumb's reach
		var layout = new Grid
		{
			RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)],
			RowSpacing = 6, Padding = new Thickness(20, 12, 20, 20),
		};
		layout.Add(_cover, 0, 0);
		layout.Add(_title, 0, 1);
		layout.Add(_author, 0, 2);
		layout.Add(_chapter, 0, 3);
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

	static Button Round(string text, double size, double diameter) => new()
	{
		Text = text, FontSize = size, TextColor = Palette.Text, BackgroundColor = Palette.Surface, Padding = 0,
		WidthRequest = diameter, HeightRequest = diameter, CornerRadius = (int)(diameter / 2),
	};

	static Button Flat(string text) => new() { Text = text, FontSize = 15, TextColor = Palette.Text, BackgroundColor = Colors.Transparent };

	protected override void OnAppearing()
	{
		base.OnAppearing();
		_lastTick = DateTime.UtcNow;
		_timer.Start();
		Update();
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
		var now = DateTime.UtcNow;
		player.Tick(now - _lastTick);
		_lastTick = now;
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
		}
		var position = player.Position;
		int chapter = player.ChapterIndexAt(position);
		_chapter.Text = chapter >= 0 ? $"Chapter {chapter + 1} of {player.Chapters.Count} · {player.Chapters[chapter].Title}" : "";
		_subtitle.Text = player.Subtitles?.TextAt(position) ?? "";
		if (!_dragging) _seek.Value = Math.Min(position.TotalSeconds, _seek.Maximum);
		_elapsed.Text = Format(_dragging ? TimeSpan.FromSeconds(_seek.Value) : position);
		_remaining.Text = "−" + Format(player.Duration - position);
		_play.Text = player.IsPlaying ? "❚❚" : "▶︎";
		_speed.Text = $"{player.Speed:0.##}×";
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

	async Task ChooseChapterAsync()
	{
		var chapters = App.Player.Chapters;
		if (chapters.Count == 0) return;
		var titles = chapters.Select((c, i) => $"{i + 1:00}  {c.Title}  ({Format(c.Start)})").ToArray();
		var chosen = await DisplayActionSheetAsync("Chapters", "Cancel", null, titles);
		int index = Array.IndexOf(titles, chosen);
		if (index >= 0) App.Player.SeekTo(chapters[index].Start);
	}

	static string Format(TimeSpan t)
	{
		if (t < TimeSpan.Zero) t = TimeSpan.Zero;
		return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
	}
}
