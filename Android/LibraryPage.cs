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
	/// <summary>A connected PC that did not answer.</summary>
	readonly Label _status = new() { FontSize = 13, TextColor = Palette.TextDim, Padding = new Thickness(16, 6), IsVisible = false };
	readonly Button _continue = new() { BackgroundColor = Palette.Surface, TextColor = Palette.Text, CornerRadius = 0, IsVisible = false, LineBreakMode = LineBreakMode.TailTruncation };
	Func<Task>? _actionHandler;
	bool _scanning;

	public LibraryPage()
	{
		Title = "Library";
		BackgroundColor = Palette.Back;
		ToolbarItems.Add(new ToolbarItem { Text = "Add folder", Command = new Command(async () => await AddFolderAsync()) });
		ToolbarItems.Add(new ToolbarItem { Text = "Refresh", Command = new Command(async () => await RefreshAsync()) });
		ToolbarItems.Add(new ToolbarItem { Text = "Connect to a PC", Order = ToolbarItemOrder.Secondary, Command = new Command(async () => await ManagePcsAsync()) });
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
		var page = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)] };
		page.Add(_busy, 0, 0);
		page.Add(_status, 0, 1);
		page.Add(_list, 0, 2);
		page.Add(_prompt, 0, 2);
		page.Add(_continue, 0, 3);
		Content = page;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		// The book playing (or last played), one tap away
		var current = App.Player.IsLoaded ? App.Player.Title : App.Settings.LastBook is { } last && App.Settings.Books.TryGetValue(last, out var b) ? b.Title : null;
		_continue.Text = current != null ? $"▶︎  {current}" : "";
		_continue.IsVisible = current != null && (App.Player.IsLoaded || BookSource.Exists(App.Settings.LastBook!) || RemoteBooks.ServerOf(App.Settings.LastBook!) != null);
		await RefreshAsync();
	}

	/// <summary>What the page needs first (access to the files, a folder), else the books.</summary>
	async Task RefreshAsync()
	{
		// Books streamed from a PC need no access to this phone's files
		bool remoteOnly = App.Settings.Servers.Count > 0 && App.Settings.LibraryFolders.Count == 0;
		if (!StorageAccess.Granted && !remoteOnly)
		{
			Prompt("aBookPlayer reads your audiobooks, their subtitles and the positions synced with your PC from a folder on this phone. Allow it to access your files.",
				"Allow access", async () => { await StorageAccess.RequestAsync(); await RefreshAsync(); });
			return;
		}
		if (App.Settings.LibraryFolders.Count == 0 && App.Settings.Servers.Count == 0)
		{
			Prompt("Choose the folder with your audiobooks — for example the one kept in sync with your PC by Syncthing or FolderSync — " +
				"or play the books of your PC over the network (⋮ → Connect to a PC).",
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
			var folders = StorageAccess.Granted ? App.Settings.LibraryFolders.ToList() : [];
			var books = App.Settings.Books.ToDictionary(b => b.Key, b => b.Value);
			var current = App.Player.Path;
			var sync = StorageAccess.Granted ? App.Settings.SyncFolder : null;
			var remote = await ListRemoteAsync();
			// Downloads left unfinished go on, from the PCs that answered
			Downloads.Resume(remote.Where(r => r.Books != null).Select(r => r.Server.Address).ToList());
			// A PC that did not answer: said above the list (its books are left out until it does)
			var errors = remote.Where(r => r.Error != null).Select(r => r.Error!).ToList();
			_status.Text = string.Join("\n", errors);
			_status.IsVisible = errors.Count > 0;
			var rows = await Task.Run(() => Scan(folders, books, current, sync, remote));
			_list.ItemsSource = rows;
			if (rows.Count == 0 && errors.Count == 0)
				Prompt("No audiobooks found in the library folders.", "Add another folder", AddFolderAsync);
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

	/// <summary>
	/// The PCs whose library this phone plays (on the PC: File → Share with your phone): connect to one with its
	/// address and access key, or disconnect one.
	/// </summary>
	async Task ManagePcsAsync()
	{
		const string connect = "Connect to a PC…";
		var servers = App.Settings.Servers.ToList();
		const string title = "Connect to a PC";
		if (servers.Count > 0)
		{
			var disconnects = servers.Select(s => $"Disconnect {s.Machine} ({s.Address})").ToList();
			var answer = await DisplayActionSheetAsync("PCs", "Cancel", null, disconnects.Prepend(connect).ToArray());
			if (answer == null || answer == "Cancel") return;
			if (answer != connect)
			{
				App.Settings.Servers.Remove(servers[disconnects.IndexOf(answer)]);
				App.Settings.Save();
				await RefreshAsync();
				return;
			}
		}

		var address = await DisplayPromptAsync(title,
			"The address shown by aBookPlayer on the PC in File → Share with your phone, for example 192.168.1.20:52780.",
			"Next", "Cancel", placeholder: $"192.168.1.20:{LibraryServer.DefaultPort}", keyboard: Keyboard.Url);
		if (string.IsNullOrWhiteSpace(address)) return;
		var key = await DisplayPromptAsync(title, "The access key shown there, for example K7PX-M2QA-9TRD.", "Connect", "Cancel",
			placeholder: "XXXX-XXXX-XXXX", keyboard: Keyboard.Plain);
		if (string.IsNullOrWhiteSpace(key)) return;

		var server = new RemoteServer { Key = key.Trim() };
		_busy.IsRunning = true;
		try
		{
			server.Address = RemoteLibraryClient.NormalizeAddress(address);
			using var client = new RemoteLibraryClient(server.Address, server.Key);
			var hello = await client.HelloAsync();
			server.Machine = hello.Machine;
		}
		catch (Exception ex)
		{
			await DisplayAlertAsync("Connect to a PC", RemoteBooks.Explain(ex, "The PC"), "OK");
			return;
		}
		finally
		{
			_busy.IsRunning = false;
		}
		// The same PC again (a new key, say): replaced
		App.Settings.Servers.RemoveAll(s => s.Address == server.Address);
		App.Settings.Servers.Add(server);
		App.Settings.Save();
		await DisplayAlertAsync("Connect to a PC", $"Connected to {server.Machine}: its books are in the library, marked \"on {server.Machine}\". " +
			"They play while aBookPlayer is open on the PC and this phone is on the same network.", "OK");
		await RefreshAsync();
	}

	bool _opening;

	async Task OpenAsync(string path)
	{
		// A second tap while the book is being read (it takes a moment for a big folder): once is enough
		if (_opening || App.Player.IsOpening) return;
		_opening = true;
		try
		{
			// Opened again if the player lost it (its service stopped meanwhile), or to play the phone's copy of a
			// PC's book streamed until it was downloaded
			bool toCopy = RemoteBooks.IsRemote(path) && !App.Player.PlaysCopy && Downloads.StatusOf(path).Status == Downloads.Status.Complete;
			if (path != App.Player.Path || !App.Player.IsLoaded || toCopy) await App.Player.OpenAsync(path, play: true);
			if (Navigation.NavigationStack.LastOrDefault() is not PlayerPage) await Navigation.PushAsync(new PlayerPage());
		}
		catch (Exception ex)
		{
			await DisplayAlertAsync("aBookPlayer", "Could not open the book:\n" + ex.Message, "OK");
		}
		finally
		{
			_opening = false;
		}
	}

	/// <summary>A book of a PC's library, with what the PC said about it.</summary>
	sealed class RemoteEntry : LibraryEntry
	{
		public required RemoteServer Server { get; init; }
		public required RemoteBookSummary Summary { get; init; }
		/// <summary>"Saved" (copied to the phone), "45%" (being copied), "Failed", or null when it is only on the PC.</summary>
		public string? Copy { get; init; }
	}

	/// <summary>How a PC's book stands on this phone, for its row.</summary>
	static string? CopyText(string path) => Downloads.StatusOf(path) switch
	{
		(Downloads.Status.Complete, _) => "Saved",
		(Downloads.Status.Downloading, var p) => $"{p:P0}",
		(Downloads.Status.Failed, _) => "Failed",
		_ => null,
	};

	/// <summary>What the connected PCs answered (their books), or why one did not.</summary>
	sealed record RemoteListing(RemoteServer Server, List<RemoteBookSummary>? Books, string? Error);

	/// <summary>The libraries of the connected PCs, asked all at once; a PC that does not answer quickly is left out.</summary>
	static async Task<List<RemoteListing>> ListRemoteAsync()
	{
		var servers = App.Settings.Servers.ToList();
		return (await Task.WhenAll(servers.Select(async server =>
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
			try { return new RemoteListing(server, await RemoteBooks.Client(server).LibraryAsync(timeout.Token), null); }
			catch (UnauthorizedAccessException) { return new RemoteListing(server, null, $"{server.Machine} did not accept the access key"); }
			catch (Exception) { return new RemoteListing(server, null, $"{server.Machine} cannot be reached"); }
		}))).ToList();
	}

	/// <summary>The books found in the folders, those listened to and the PCs' ones, most recent first (runs off the UI thread).</summary>
	static List<LibraryRow> Scan(List<string> folders, Dictionary<string, BookState> history, string? current, string? syncFolder, List<RemoteListing> remote)
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

		// The PCs' books: their details as the PC read them, their covers downloaded once (a few at a time)
		var remoteEntries = remote.Where(r => r.Books != null).SelectMany(r => r.Books!.Select(b =>
		{
			var path = RemoteBooks.PathOf(r.Server.Address, b.Id);
			return new RemoteEntry
			{
				Path = path, State = history.GetValueOrDefault(path), Scanned = new BookDetails(b.Title, b.Author, b.Series, b.Number),
				Server = r.Server, Summary = b, Copy = CopyText(path),
			};
		})).ToList();
		Parallel.ForEach(remoteEntries, new ParallelOptions { MaxDegreeOfParallelism = 4 }, e =>
			CoverFile(e.Path, () => RemoteBooks.Client(e.Server).CoverAsync(e.Summary.Id).GetAwaiter().GetResult()));
		entries.AddRange(remoteEntries);

		// Books copied to the phone whose PC did not list them (away from home, or the PC off): from the copy
		var listed = remoteEntries.Select(e => e.Path).ToHashSet();
		foreach (var copy in Downloads.All().Where(c => !listed.Contains(c.Path)))
		{
			var b = copy.Book;
			var server = RemoteBooks.ServerOf(copy.Path) ?? new RemoteServer { Address = RemoteBooks.Parse(copy.Path)!.Value.Address, Machine = copy.Machine };
			entries.Add(new RemoteEntry
			{
				Path = copy.Path, State = history.GetValueOrDefault(copy.Path), Scanned = new BookDetails(b.Title, b.Author, b.Series, b.Number),
				Server = server, Copy = "Saved",
				// Where the PC was when it was copied is old news: not shown
				Summary = new RemoteBookSummary(b.Id, b.Title, b.Author, b.Series, b.Number, b.Parts.Sum(p => p.Seconds), 0, default, false, b.SyncKey),
			});
			CoverFile(copy.Path, () => copy.Cover != null ? File.ReadAllBytes(copy.Cover) : null);
		}

		// Where the PCs are in each book (read once for all of them)
		var synced = syncFolder != null ? BookSync.ReadAll(syncFolder) : [];
		return LibraryOrder.Sort(entries, LibrarySort.Recent).Select(e => ToRow(e, current, NewestElsewhere(e, synced))).ToList();
	}

	/// <summary>The newest position of the other devices: through the shared folder, or said by the book's PC.</summary>
	static SyncedPosition? NewestElsewhere(LibraryEntry e, Dictionary<string, SyncedPosition> synced)
	{
		if (e is not RemoteEntry remote) return synced.Count > 0 ? SyncedFor(e, synced) : null;
		var summary = remote.Summary;
		SyncedPosition? best = summary.PositionUpdated != default
			? new SyncedPosition(summary.PositionSeconds, DateTime.SpecifyKind(summary.PositionUpdated, DateTimeKind.Utc), summary.Finished, remote.Server.Machine)
			: null;
		if (summary.SyncKey is { } key && synced.TryGetValue(key, out var shared) && (best == null || shared.Updated > best.Updated)) best = shared;
		return best;
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
		double length = e.State is { DurationSeconds: > 0 } s ? s.DurationSeconds : (e as RemoteEntry)?.Summary.DurationSeconds ?? 0;
		if (length > 0) details.Add(FormatLength(TimeSpan.FromSeconds(length)));
		if (e is RemoteEntry remote) details.Add("on " + remote.Server.Machine);
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
			status = synced.Finished ? $"Finished on {synced.Machine}"
				: synced.Seconds < 1 ? "Not started"
				: $"At {FormatLength(TimeSpan.FromSeconds(synced.Seconds))} on {synced.Machine}";
			progress = synced.Finished ? 1 : length > 0 ? Math.Clamp(synced.Seconds / length, 0, 1) : 0;
		}
		var file = CoverPath(e.Path);
		var cover = File.Exists(file) && new FileInfo(file).Length > 0 ? ImageSource.FromFile(file) : null;
		return new LibraryRow(e.Path, e.Title, string.Join("  ·  ", details), status, progress, e.Path == current, cover, (e as RemoteEntry)?.Copy);
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
		// A PC's book copied to the phone (or being copied): a strip across the cover's foot
		var badge = new Label
		{
			FontSize = 11, TextColor = Colors.White, BackgroundColor = Palette.Accent, HorizontalTextAlignment = TextAlignment.Center,
			VerticalOptions = LayoutOptions.End, Padding = new Thickness(0, 1),
		};
		badge.SetBinding(Label.TextProperty, static (LibraryRow r) => r.Badge);
		badge.SetBinding(IsVisibleProperty, static (LibraryRow r) => r.Badge, converter: new FuncConverter<string?, bool>(b => b != null));
		var coverBox = new Grid { WidthRequest = 64, HeightRequest = 64, Children = { placeholder, cover, badge } };
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
/// <param name="Badge">On the cover: how a PC's book stands on this phone ("Saved", "45%"), or null.</param>
sealed record LibraryRow(string Path, string Title, string Details, string Status, double Progress, bool Current, ImageSource? Cover, string? Badge = null);
