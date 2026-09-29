using System.Security.Cryptography;
using System.Text;

namespace aBookPlayer.Droid;

/// <summary>
/// The books in the library folders and those listened to: cover, title, author and series, and how far along each
/// one is. Tapping a book opens it in the player.
/// </summary>
sealed class LibraryPage : ContentPage
{


	readonly CollectionView _list = new() { SelectionMode = SelectionMode.Single, Margin = new Thickness(0, 4) };
	readonly Label _message = new() { TextColor = Palette.TextDim, FontSize = 16, HorizontalTextAlignment = TextAlignment.Center };
	readonly Button _action = new() { BackgroundColor = Palette.Accent, TextColor = Colors.White, CornerRadius = 8, HorizontalOptions = LayoutOptions.Center };
	readonly VerticalStackLayout _prompt;
	readonly ActivityIndicator _busy = new() { Color = Palette.Accent, IsRunning = false, HeightRequest = 4 };
	readonly Button _continue = new() { BackgroundColor = Palette.Surface, TextColor = Palette.Text, CornerRadius = 0, IsVisible = false, LineBreakMode = LineBreakMode.TailTruncation };
	Func<Task>? _actionHandler;
	bool _scanning;

	public LibraryPage()
	{
		Title = "Library";
		BackgroundColor = Palette.Back;
		ToolbarItems.Add(new ToolbarItem { Text = "Add folder", Command = new Command(async () => await AddFolderAsync()) });
		ToolbarItems.Add(new ToolbarItem { Text = "Refresh", Command = new Command(async () => await RefreshAsync()) });
		ToolbarItems.Add(new ToolbarItem { Text = "Sync", Order = ToolbarItemOrder.Secondary, Command = new Command(async () => await ChooseSyncFolderAsync()) });

		_list.ItemTemplate = new DataTemplate(MakeRow);
		_list.SelectionChanged += async (_, e) =>
		{
			if (e.CurrentSelection.FirstOrDefault() is not LibraryRow row) return;
			_list.SelectedItem = null;
			await OpenAsync(row.Path);
		};
		_action.Clicked += async (_, _) => { if (_actionHandler != null) await _actionHandler(); };
		// The book playing, or the last one (after a restart it is not loaded yet: open it where it was left)
		_continue.Clicked += async (_, _) =>
		{
			if (App.Player.IsLoaded) await Navigation.PushAsync(new PlayerPage());
			else if (App.Settings.LastBook is { } last) await OpenAsync(last);
		};

		_prompt = new VerticalStackLayout { Spacing = 18, Padding = new Thickness(32), VerticalOptions = LayoutOptions.Center, Children = { _message, _action }, IsVisible = false };
		// Access to the files is granted on a system page: look again when the app comes back from it
		App.Resumed += () => { if (_prompt.IsVisible) MainThread.BeginInvokeOnMainThread(async () => await RefreshAsync()); };
		var page = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)] };
		page.Add(_busy, 0, 0);
		page.Add(_list, 0, 1);
		page.Add(_prompt, 0, 1);
		page.Add(_continue, 0, 2);
		Content = page;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		// The book playing (or last played), one tap away
		var current = App.Player.IsLoaded ? App.Player.Title : App.Settings.LastBook is { } last && App.Settings.Books.TryGetValue(last, out var b) ? b.Title : null;
		_continue.Text = current != null ? $"▶︎  {current}" : "";
		_continue.IsVisible = current != null && (App.Player.IsLoaded || BookSource.Exists(App.Settings.LastBook!));
		await RefreshAsync();
	}

	/// <summary>What the page needs first (access to the files, a folder), else the books.</summary>
	async Task RefreshAsync()
	{
		if (!StorageAccess.Granted)
		{
			Prompt("aBookPlayer reads your audiobooks, their subtitles and the positions synced with your PC from a folder on this phone. Allow it to access your files.",
				"Allow access", async () => { await StorageAccess.RequestAsync(); await RefreshAsync(); });
			return;
		}
		if (App.Settings.LibraryFolders.Count == 0)
		{
			Prompt("Choose the folder with your audiobooks — for example the one kept in sync with your PC by Syncthing or FolderSync.",
				"Choose folder", AddFolderAsync);
			return;
		}
		_prompt.IsVisible = false;
		_list.IsVisible = true;
		if (_scanning) return;
		_scanning = true;
		_busy.IsRunning = true;
		try
		{
			var folders = App.Settings.LibraryFolders.ToList();
			var books = App.Settings.Books.ToDictionary(b => b.Key, b => b.Value);
			var current = App.Player.Path;
			var sync = App.Settings.SyncFolder;
			var rows = await Task.Run(() => Scan(folders, books, current, sync));
			_list.ItemsSource = rows;
			if (rows.Count == 0) Prompt("No audiobooks found in the library folders.", "Add another folder", AddFolderAsync);
		}
		catch (Exception ex)
		{
			await DisplayAlertAsync("Library", "Could not read the library folders:\n" + ex.Message, "OK");
		}
		finally
		{
			_scanning = false;
			_busy.IsRunning = false;
		}
	}

	void Prompt(string message, string action, Func<Task> handler)
	{
		_message.Text = message;
		_action.Text = action;
		_actionHandler = handler;
		_prompt.IsVisible = true;
		_list.IsVisible = false;
	}

	async Task AddFolderAsync()
	{
		if (!StorageAccess.Granted && !await StorageAccess.RequestAsync()) return;
		var folder = await MainActivity.PickFolderAsync();
		if (folder == null) return;
		if (!Directory.Exists(folder))
		{
			await DisplayAlertAsync("Library", $"\"{folder}\" cannot be read.", "OK");
			return;
		}
		if (!App.Settings.LibraryFolders.Contains(folder)) App.Settings.LibraryFolders.Add(folder);
		App.Settings.Save();
		await RefreshAsync();
	}

	/// <summary>
	/// The folder shared with the PCs (the Windows app's File → Sync between PCs): positions are read from and
	/// written to its "aBookPlayer sync" subfolder. Usually the library folder itself, if that is the synced one.
	/// </summary>
	async Task ChooseSyncFolderAsync()
	{
		const string choose = "Choose another folder…", off = "Turn off";
		var current = App.Settings.SyncFolder;
		var options = App.Settings.LibraryFolders.Where(f => f != current).Select(f => "Use " + f).ToList();
		options.Add(choose);
		if (current != null) options.Add(off);
		// Short: an action sheet shows two lines of title at most
		var title = current != null ? "Synced with your PCs through " + Path.GetFileName(current) : "Folder shared with your PCs";
		var answer = await DisplayActionSheetAsync(title, "Cancel", null, options.ToArray());
		if (answer == null || answer == "Cancel") return;
		string? folder = answer == off ? null
			: answer == choose ? await MainActivity.PickFolderAsync()
			: answer["Use ".Length..];
		if (answer == choose && folder == null) return;
		App.Settings.SyncFolder = folder;
		App.Settings.Save();
		// Books opened before get their key, so they are synced too (it needs their files: off the UI thread)
		if (folder != null) await Task.Run(() =>
		{
			foreach (var (path, book) in App.Settings.Books.ToList())
				if (book.SyncKey == null && BookSource.Exists(path)) book.SyncKey = BookSync.KeyFor(path, book.Asin);
		});
		App.Settings.Save();
		if (folder != null) _ = BookSync.Publish(folder, App.Settings.Books.Values);
		await DisplayAlertAsync("Sync", folder != null
			? $"Positions are now synced through \"{folder}\".\n\nOn your PCs, choose the same folder (kept in sync with this phone) in aBookPlayer's File → Sync between PCs."
			: "Sync is off.", "OK");
	}

	async Task OpenAsync(string path)
	{
		try
		{
			if (path != App.Player.Path) await App.Player.OpenAsync(path, play: true);
			await Navigation.PushAsync(new PlayerPage());
		}
		catch (Exception ex)
		{
			await DisplayAlertAsync("aBookPlayer", "Could not open the book:\n" + ex.Message, "OK");
		}
	}

	/// <summary>The books found in the folders plus those listened to, most recent first (runs off the UI thread).</summary>
	static List<LibraryRow> Scan(List<string> folders, Dictionary<string, BookState> history, string? current, string? syncFolder)
	{
		var paths = LibraryScanner.Scan(folders, CancellationToken.None).Select(Path.GetFullPath).ToHashSet();
		foreach (var known in history.Keys)
			if (BookSource.Exists(known)) paths.Add(known);
		var entries = paths.Select(p => new LibraryEntry { Path = p, State = history.GetValueOrDefault(p) }).ToList();
		foreach (var entry in entries)
		{
			var (details, cover) = Details(entry.Path);
			if (entry.State == null) entry.Scanned = details;
			CoverFile(entry.Path, cover);
		}
		LibraryDetailsCache.Save();
		// Where the PCs are in each book (read once for all of them)
		var synced = syncFolder != null ? BookSync.ReadAll(syncFolder) : [];
		return LibraryOrder.Sort(entries, LibrarySort.Recent).Select(e => ToRow(e, current, synced.Count > 0 ? SyncedFor(e, synced) : null)).ToList();
	}

	/// <summary>The book's details and cover: read from its files once, then from the caches.</summary>
	static (BookDetails? Details, Func<byte[]?> Cover) Details(string path)
	{
		var stamp = LibraryDetailsCache.StampOf(path);
		if (stamp != null && LibraryDetailsCache.Get(path, stamp) is { } known) return (known, () => BookSource.ReadDetails(path).Cover);
		try
		{
			var (details, cover) = BookSource.ReadDetails(path);
			if (stamp != null) LibraryDetailsCache.Put(path, stamp, details);
			return (details, () => cover);
		}
		catch { return (null, () => null); }
	}

	static string CoversFolder => Path.Combine(FileSystem.CacheDirectory, "covers");

	static string CoverPath(string book) =>
		Path.Combine(CoversFolder, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(book)))[..20] + ".img");

	/// <summary>Keeps the cover as a file (an empty one when there is none), so it is read from the book only once.</summary>
	static void CoverFile(string book, Func<byte[]?> read)
	{
		var file = CoverPath(book);
		if (File.Exists(file)) return;
		try
		{
			Directory.CreateDirectory(CoversFolder);
			File.WriteAllBytes(file, read() ?? []);
		}
		catch { /* only a cache */ }
	}

	static LibraryRow ToRow(LibraryEntry e, string? current, SyncedPosition? synced)
	{
		var details = new List<string>();
		if (!string.IsNullOrWhiteSpace(e.Author)) details.Add(e.Author);
		if (e.SeriesLabel is { } series) details.Add(series);
		if (e.State is { DurationSeconds: > 0 } s) details.Add(FormatLength(TimeSpan.FromSeconds(s.DurationSeconds)));
		var status = e.Status switch
		{
			LibraryStatus.Finished => "Finished",
			LibraryStatus.NotStarted => "Not started",
			_ when e.Progress is { } p => $"{p:P0} · {FormatLength(TimeSpan.FromSeconds(e.State!.DurationSeconds * (1 - p)))} left",
			_ => "In progress",
		};
		double progress = e.Status == LibraryStatus.Finished ? 1 : e.Progress ?? 0;
		// A PC listened more recently (or the book was never opened here): where it is there, as opening it continues
		if (synced != null && (e.State == null || synced.Updated > e.State.EffectivePositionUpdated.AddSeconds(2)))
		{
			double duration = e.State?.DurationSeconds ?? 0;
			status = synced.Finished ? $"Finished on {synced.Machine}"
				: $"At {FormatLength(TimeSpan.FromSeconds(synced.Seconds))} on {synced.Machine}";
			progress = synced.Finished ? 1 : duration > 0 ? Math.Clamp(synced.Seconds / duration, 0, 1) : 0;
		}
		var file = CoverPath(e.Path);
		var cover = File.Exists(file) && new FileInfo(file).Length > 0 ? ImageSource.FromFile(file) : null;
		return new LibraryRow(e.Path, e.Title, string.Join("  ·  ", details), status, progress, e.Path == current, cover);
	}

	/// <summary>The PCs' newest position for this book: under its key, or the key it would get (ASIN from the export's name, or name and size).</summary>
	static SyncedPosition? SyncedFor(LibraryEntry e, Dictionary<string, SyncedPosition> synced)
	{
		var name = BookSource.IsFolder(e.Path) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(e.Path)) : Path.GetFileNameWithoutExtension(e.Path);
		var key = e.State?.SyncKey ?? BookSync.KeyFor(e.Path, e.State?.Asin ?? AudibleExport.Parse(name).Asin);
		if (key != null && synced.TryGetValue(key, out var found)) return found;
		// Saved by an older version, under the name and size (only computed when the key was the ASIN)
		return key != null && key.StartsWith("asin:") && BookSync.LegacyKeyFor(e.Path) is { } old && synced.TryGetValue(old, out found) ? found : null;
	}

	static string FormatLength(TimeSpan t) =>
		t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";

	static View MakeRow()
	{
		var cover = new Image { WidthRequest = 64, HeightRequest = 64, Aspect = Aspect.AspectFill };
		cover.SetBinding(Image.SourceProperty, static (LibraryRow r) => r.Cover);
		// A note on the empty square of a book without a cover (hidden under the picture when there is one)
		var placeholder = new Label
		{
			Text = "♪", FontSize = 26, TextColor = Palette.TextDim, BackgroundColor = Palette.Surface,
			HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center,
		};
		var coverBox = new Grid { WidthRequest = 64, HeightRequest = 64, Children = { placeholder, cover } };
		var title = new Label { FontAttributes = FontAttributes.Bold, FontSize = 16, TextColor = Palette.Text, LineBreakMode = LineBreakMode.TailTruncation };
		title.SetBinding(Label.TextProperty, static (LibraryRow r) => r.Title);
		title.SetBinding(Label.TextColorProperty, static (LibraryRow r) => r.Current, converter: new FuncConverter<bool, Color>(c => c ? Palette.Accent : Palette.Text));
		var details = new Label { FontSize = 13, TextColor = Palette.TextDim, LineBreakMode = LineBreakMode.TailTruncation };
		details.SetBinding(Label.TextProperty, static (LibraryRow r) => r.Details);
		var progress = new ProgressBar { ProgressColor = Palette.Accent, WidthRequest = 90, HeightRequest = 4, VerticalOptions = LayoutOptions.Center };
		progress.SetBinding(ProgressBar.ProgressProperty, static (LibraryRow r) => r.Progress);
		var status = new Label { FontSize = 13, TextColor = Palette.TextDim, LineBreakMode = LineBreakMode.TailTruncation };
		status.SetBinding(Label.TextProperty, static (LibraryRow r) => r.Status);
		var text = new VerticalStackLayout
		{
			Spacing = 3, VerticalOptions = LayoutOptions.Center,
			Children = { title, details, new HorizontalStackLayout { Spacing = 10, Children = { progress, status } } },
		};
		var row = new Grid { ColumnDefinitions = [new(64), new(GridLength.Star)], ColumnSpacing = 14, Padding = new Thickness(14, 8) };
		row.Add(coverBox, 0);
		row.Add(text, 1);
		return row;
	}
}

/// <summary>A one-way value converter from a lambda (for the few bindings that need one).</summary>
sealed class FuncConverter<TIn, TOut>(Func<TIn, TOut> convert) : IValueConverter
{
	public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
		value is TIn v ? convert(v) : default;
	public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
		throw new NotSupportedException();
}

/// <summary>A book as the library list shows it.</summary>
sealed record LibraryRow(string Path, string Title, string Details, string Status, double Progress, bool Current, ImageSource? Cover);
