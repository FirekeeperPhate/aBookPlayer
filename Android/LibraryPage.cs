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
	readonly ToolbarItem _mobileData = new() { Order = ToolbarItemOrder.Secondary };
	/// <summary>A newer version of this app on a connected PC: tapped, it is downloaded from there and installed.</summary>
	readonly Button _update = new() { BackgroundColor = Palette.Accent, TextColor = Colors.White, CornerRadius = 0, IsVisible = false, FontSize = 14, LineBreakMode = LineBreakMode.WordWrap };
	sealed record UpdateOffer(RemoteServer Server, string Package, Version Version);
	UpdateOffer? _offer;
	bool _updating;

	// Search and filters, as in the Windows app's library: words of the title, author or series; which books; in what order
	readonly SearchBar _search = new()
	{
		Placeholder = "Search title, author or series", TextColor = Palette.Text, PlaceholderColor = Palette.TextDim,
		CancelButtonColor = Palette.TextDim, BackgroundColor = Palette.Back, FontSize = 15,
	};
	readonly Picker _show = new() { Title = "Show", ItemsSource = new[] { "All books", "In progress", "Not started", "Finished" }, TextColor = Palette.Text, TitleColor = Palette.TextDim, BackgroundColor = Palette.Surface, FontSize = 14 };
	readonly Picker _sort = new() { Title = "Order", ItemsSource = new[] { "Recently listened", "By author", "By series" }, TextColor = Palette.Text, TitleColor = Palette.TextDim, BackgroundColor = Palette.Surface, FontSize = 14 };
	readonly Label _noMatch = new() { Text = "No books match.", TextColor = Palette.TextDim, FontSize = 15, HorizontalTextAlignment = TextAlignment.Center, Margin = new Thickness(0, 24), IsVisible = false };
	readonly Grid _filters;
	/// <summary>Every book found at the last refresh, before the search and the filters.</summary>
	List<Listed> _all = [];

	void ShowMobileData() => _mobileData.Text = App.Settings.DownloadOverMobileData ? "Download over mobile data: on" : "Download over mobile data: off";
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
		_mobileData.Command = new Command(() =>
		{
			App.Settings.DownloadOverMobileData = !App.Settings.DownloadOverMobileData;
			App.Settings.Save();
			ShowMobileData();
			if (App.Settings.DownloadOverMobileData) Downloads.Resume(App.Settings.Servers.Select(s => s.Address).ToList());
		});
		ShowMobileData();
		ToolbarItems.Add(_mobileData);

		_list.ItemTemplate = new RowOrGroup();
		_list.SelectionChanged += async (_, e) =>
		{
			var chosen = e.CurrentSelection.FirstOrDefault();
			if (chosen == null) return;
			_list.SelectedItem = null;
			if (chosen is LibraryRow row) await OpenAsync(row.Path);
			// An author's or a series' header: closes or opens its group
			else if (chosen is LibraryGroup group)
			{
				if (!App.Settings.CollapsedLibraryGroups.Remove(group.Key)) App.Settings.CollapsedLibraryGroups.Add(group.Key);
				App.Settings.Save();
				Refill();
			}
		};
		_show.SelectedIndex = Math.Clamp(App.Settings.LibraryShow, 0, 3);
		_sort.SelectedIndex = Math.Clamp(App.Settings.LibrarySortBy, 0, 2);
		_search.TextChanged += (_, _) => Refill();
		_show.SelectedIndexChanged += (_, _) => { App.Settings.LibraryShow = _show.SelectedIndex; App.Settings.Save(); Refill(); };
		_sort.SelectedIndexChanged += (_, _) => { App.Settings.LibrarySortBy = _sort.SelectedIndex; App.Settings.Save(); Refill(); };
		_filters = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 8, Padding = new Thickness(12, 0, 12, 4) };
		_filters.Add(_show, 0);
		_filters.Add(_sort, 1);
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
		// The PC's QR code opened the app (running already): connect
		MainActivity.ConnectRequested += () => MainThread.BeginInvokeOnMainThread(async () => await ConnectFromLinkAsync());
		_update.Clicked += async (_, _) => await UpdateAsync();
		var page = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)] };
		page.Add(_busy, 0, 0);
		page.Add(new VerticalStackLayout { Children = { _update, _status, _search, _filters } }, 0, 1);
		page.Add(_list, 0, 2);
		page.Add(_noMatch, 0, 2);
		page.Add(_prompt, 0, 2);
		page.Add(_continue, 0, 3);
		Content = page;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		// Back from the player (it saves when it goes, possibly after this): the list shows where the book is now
		App.Player.SavePosition();
		// The book playing (or last played), one tap away
		var current = App.Player.IsLoaded ? App.Player.Title : App.Settings.LastBook is { } last && App.Settings.Books.TryGetValue(last, out var b) ? b.Title : null;
		_continue.Text = current != null ? $"▶︎  {current}" : "";
		_continue.IsVisible = current != null && (App.Player.IsLoaded || BookSource.Exists(App.Settings.LastBook!) || RemoteBooks.ServerOf(App.Settings.LastBook!) != null);
		await RefreshAsync();
		// Opened by the link of the PC's QR code
		await ConnectFromLinkAsync();
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
		_list.IsVisible = _search.IsVisible = _filters.IsVisible = true;
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
			// Downloads left unfinished go on, and the positions listened to away from home are told, to the PCs that answered
			var reachable = remote.Where(r => r.Books != null).Select(r => r.Server.Address).ToList();
			Downloads.Resume(reachable);
			RemoteBooks.SendPending(reachable);
			// A PC that did not answer: said above the list (its books are left out until it does)
			var errors = remote.Where(r => r.Error != null).Select(r => r.Error!).ToList();
			_status.Text = string.Join("\n", errors);
			_status.IsVisible = errors.Count > 0;
			ShowUpdate(remote);
			var rows = await Task.Run(() => Scan(folders, books, current, sync, remote));
			_all = rows;
			Refill();
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
		_list.IsVisible = _search.IsVisible = _filters.IsVisible = false;
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
	/// The PCs whose library this phone plays (on the PC: File → Share with your phone): connect to one found on the
	/// network or typed, with its access key, or disconnect one. (Scanning the PC's code with the camera connects too.)
	/// </summary>
	async Task ManagePcsAsync()
	{
		const string type = "Type an address…";
		var servers = App.Settings.Servers.ToList();
		_busy.IsRunning = true;
		var found = await FindPcsAsync();
		_busy.IsRunning = false;
		// The PCs answering, but not the ones already connected with that address
		var offers = found.Where(f => !servers.Any(s => s.Address == f.Address)).Select(f => $"Connect to {f.Pc.Machine} ({f.Address})").ToList();
		var disconnects = servers.Select(s => $"Disconnect {s.Machine} ({s.Address})").ToList();
		var answer = await DisplayActionSheetAsync(found.Count == 0 ? "No PC found on this network" : "PCs on this network",
			"Cancel", null, offers.Append(type).Concat(disconnects).ToArray());
		if (answer == null || answer == "Cancel") return;
		if (disconnects.IndexOf(answer) is var gone and >= 0)
		{
			App.Settings.Servers.Remove(servers[gone]);
			App.Settings.Save();
			await RefreshAsync();
			return;
		}

		string? address;
		if (answer == type)
		{
			address = await DisplayPromptAsync("Connect to a PC",
				"The address shown by aBookPlayer on the PC in File → Share with your phone, for example 192.168.1.20:52780.",
				"Next", "Cancel", placeholder: $"192.168.1.20:{LibraryServer.DefaultPort}", keyboard: Keyboard.Url);
			if (string.IsNullOrWhiteSpace(address)) return;
		}
		else address = found.Where(f => !servers.Any(s => s.Address == f.Address)).ElementAt(offers.IndexOf(answer)).Address;
		var key = await DisplayPromptAsync("Connect to a PC", "The access key shown by aBookPlayer on the PC in File → Share with your phone, " +
			"for example K7PX-M2QA-9TRD.", "Connect", "Cancel", placeholder: "XXXX-XXXX-XXXX", keyboard: Keyboard.Plain);
		if (string.IsNullOrWhiteSpace(key)) return;
		await ConnectAsync(address, key);
	}

	/// <summary>The PCs sharing their library on the networks this phone is on (asked by broadcast, two seconds).</summary>
	static async Task<List<(string Address, RemoteFound Pc)>> FindPcsAsync()
	{
		try
		{
			// Each network's own broadcast address too: some routers do not pass the general one
			var broadcasts = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
				.Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
				.SelectMany(n => n.GetIPProperties().UnicastAddresses)
				.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a.Address))
				.Select(a =>
				{
					var ip = a.Address.GetAddressBytes();
					var mask = a.IPv4Mask.GetAddressBytes();
					return new System.Net.IPAddress(ip.Select((b, i) => (byte)(b | ~mask[i])).ToArray());
				})
				.ToList();
			return await RemoteLibraryClient.DiscoverAsync(TimeSpan.FromSeconds(2), broadcasts);
		}
		catch
		{
			return []; // no network: the address can still be typed
		}
	}

	/// <summary>Connects to a PC (checking the address and the key first), and shows its books.</summary>
	async Task ConnectAsync(string address, string key)
	{
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

	/// <summary>The app was opened by the link of the PC's QR code: connect, once the user says so.</summary>
	async Task ConnectFromLinkAsync()
	{
		if (MainActivity.PendingConnect is not { } pc) return;
		MainActivity.PendingConnect = null;
		if (await DisplayAlertAsync("Connect to a PC", $"Play the books of the PC at {pc.Address} on this phone?", "Connect", "Cancel"))
			await ConnectAsync(pc.Address, pc.Key);
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
		/// <summary>"Saved" (copied to the phone), "45%" (being copied), "Wi-Fi…" (waiting for it), "Failed", or null when it is only on the PC.</summary>
		public string? Copy { get; init; }
	}

	/// <summary>How a PC's book stands on this phone, for its row.</summary>
	static string? CopyText(string path) => Downloads.StatusOf(path) switch
	{
		(Downloads.Status.Complete, _) => "Saved",
		(Downloads.Status.Downloading, var p) => $"{p:P0}",
		(Downloads.Status.Waiting, _) => "Wi-Fi…",
		(Downloads.Status.Failed, _) => "Failed",
		_ => null,
	};

	/// <summary>This app's version (1.11.3).</summary>
	static Version Installed => AppInfo.Version is var v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

	/// <summary>The newest version of this app that a connected PC hands out, if it is newer than this one: offered above the list.</summary>
	void ShowUpdate(List<RemoteListing> remote)
	{
		_offer = remote
			.Select(r => r.Hello?.AppPackage is { } package && AppPackages.VersionOf(package) is { } version ? new UpdateOffer(r.Server, package, version) : null)
			.OfType<UpdateOffer>()
			.Where(o => o.Version > Installed)
			.MaxBy(o => o.Version);
		if (!_updating) _update.Text = _offer != null ? OfferText(_offer) : "";
		_update.IsVisible = _offer != null;
		// The file of an update already installed takes room for nothing
		if (_offer == null && !_updating)
			try { Directory.Delete(Path.Combine(FileSystem.CacheDirectory, "update"), recursive: true); } catch { /* none, or in use */ }
	}

	static string OfferText(UpdateOffer offer) => $"Tap to update to aBookPlayer {offer.Version} from {offer.Server.Machine}";

	/// <summary>Downloads the offered version from its PC, then hands it to Android's installer (which asks to confirm).</summary>
	async Task UpdateAsync()
	{
		if (_offer is not { } offer || _updating) return;
		_updating = true;
		var folder = Path.Combine(FileSystem.CacheDirectory, "update");
		var file = Path.Combine(folder, offer.Package);
		try
		{
			Directory.CreateDirectory(folder);
			// An earlier update's file is not needed any more
			foreach (var old in Directory.GetFiles(folder))
				if (!old.StartsWith(file, StringComparison.Ordinal)) File.Delete(old);
			if (!File.Exists(file))
			{
				int shown = -1;
				await RemoteBooks.Client(offer.Server).DownloadAppAsync(offer.Package, file + ".part", (have, total) =>
				{
					int percent = total > 0 ? (int)(have * 100 / total) : 0;
					if (percent == shown) return;
					shown = percent;
					MainThread.BeginInvokeOnMainThread(() => _update.Text = $"Downloading aBookPlayer {offer.Version}… {percent}%");
				});
				File.Move(file + ".part", file, overwrite: true);
			}
			// The first time, Android asks to allow this app to install apps
			await Launcher.OpenAsync(new OpenFileRequest($"aBookPlayer {offer.Version}", new ReadOnlyFile(file, "application/vnd.android.package-archive")));
		}
		catch (Exception ex)
		{
			await DisplayAlertAsync("Update", RemoteBooks.Explain(ex, offer.Server.Machine), "OK");
		}
		finally
		{
			_updating = false;
			_update.Text = OfferText(offer);
		}
	}

	/// <summary>What the connected PCs answered (their books, and their greeting: the app they hand out), or why one did not.</summary>
	sealed record RemoteListing(RemoteServer Server, List<RemoteBookSummary>? Books, string? Error, RemoteHello? Hello = null);

	/// <summary>The libraries of the connected PCs, asked all at once; a PC that does not answer quickly is left out.</summary>
	static async Task<List<RemoteListing>> ListRemoteAsync()
	{
		var servers = App.Settings.Servers.ToList();
		return (await Task.WhenAll(servers.Select(async server =>
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
			try
			{
				var client = RemoteBooks.Client(server);
				var books = await client.LibraryAsync(timeout.Token);
				RemoteHello? hello = null;
				try { hello = await client.HelloAsync(timeout.Token); } catch { /* only for the app's updates */ }
				return new RemoteListing(server, books, null, hello);
			}
			catch (UnauthorizedAccessException) { return new RemoteListing(server, null, $"{server.Machine} did not accept the access key"); }
			catch (Exception) { return new RemoteListing(server, null, $"{server.Machine} cannot be reached"); }
		}))).ToList();
	}

	/// <summary>The books found in the folders, those listened to and the PCs' ones, most recent first (runs off the UI thread).</summary>
	static List<Listed> Scan(List<string> folders, Dictionary<string, BookState> history, string? current, string? syncFolder, List<RemoteListing> remote)
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
		var listed = remoteEntries.Select(e => e.Path).ToHashSet();

		// The same book in this phone's folders and on a PC (a folder kept in sync with it): listed once, from the
		// phone's files, with where the PC is in it
		var twins = new Dictionary<LibraryEntry, RemoteEntry>();
		var localKeys = new Dictionary<string, LibraryEntry>();
		if (remoteEntries.Count > 0)
			foreach (var local in entries)
				if (LocalKey(local) is { } key) localKeys.TryAdd(key, local);
		// (Not one copied to the phone, or being copied: it stays in the list, where its copy can be deleted)
		remoteEntries.RemoveAll(r => r.Copy == null && r.Summary.SyncKey is { } key && localKeys.TryGetValue(key, out var local) && twins.TryAdd(local, r));

		Parallel.ForEach(remoteEntries, new ParallelOptions { MaxDegreeOfParallelism = 4 }, e =>
			CoverFile(e.Path, () => RemoteBooks.Client(e.Server).CoverAsync(e.Summary.Id).GetAwaiter().GetResult()));
		entries.AddRange(remoteEntries);

		// Books copied to the phone whose PC did not list them (away from home, or the PC off): from the copy
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
		return entries
			.Select(e => new Listed { Path = e.Path, State = e.State, Scanned = e.Scanned, Row = ToRow(e, current, Newest(NewestElsewhere(e, synced), twins.TryGetValue(e, out var twin) ? NewestElsewhere(twin, []) : null)) })
			.ToList();
	}

	/// <summary>
	/// The list as the search and the filters want it: the books whose title, author or series has every word typed,
	/// of the status chosen, in the order chosen; by author or by series, under a header per group (a tap on it
	/// closes or opens the group; all open while searching).
	/// </summary>
	void Refill()
	{
		var words = (_search.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
		LibraryStatus? wanted = _show.SelectedIndex switch
		{
			1 => LibraryStatus.InProgress,
			2 => LibraryStatus.NotStarted,
			3 => LibraryStatus.Finished,
			_ => null,
		};
		var sort = (LibrarySort)Math.Max(0, _sort.SelectedIndex);
		var matching = _all
			.Where(b => wanted == null || b.Row.Shown == wanted)
			.Where(b => words.All(w => b.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase)
			                           || (b.Author?.Contains(w, StringComparison.CurrentCultureIgnoreCase) ?? false)
			                           || (b.SeriesLabel?.Contains(w, StringComparison.CurrentCultureIgnoreCase) ?? false)));
		var sorted = LibraryOrder.Sort(matching, sort);
		var items = LibraryOrder.Group(sorted, sort, words.Length > 0 ? [] : App.Settings.CollapsedLibraryGroups)
			.Select(item => item is Listed listed ? listed.Row : item)
			.ToList();
		_list.ItemsSource = items;
		_noMatch.IsVisible = _all.Count > 0 && sorted.Count == 0;
	}

	static SyncedPosition? Newest(SyncedPosition? a, SyncedPosition? b) => a == null ? b : b == null ? a : b.Updated > a.Updated ? b : a;

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
		var shown = e.Status;
		// A PC listened more recently (or the book was never opened here): where it is there, as opening it continues
		if (synced != null && (e.State == null || synced.Updated > e.State.EffectivePositionUpdated.AddSeconds(2)))
		{
			status = synced.Finished ? $"Finished on {synced.Machine}"
				: synced.Seconds < 1 ? "Not started"
				: $"At {FormatLength(TimeSpan.FromSeconds(synced.Seconds))} on {synced.Machine}";
			progress = synced.Finished ? 1 : length > 0 ? Math.Clamp(synced.Seconds / length, 0, 1) : 0;
			shown = synced.Finished ? LibraryStatus.Finished : synced.Seconds < 1 ? LibraryStatus.NotStarted : LibraryStatus.InProgress;
		}
		var file = CoverPath(e.Path);
		var cover = File.Exists(file) && new FileInfo(file).Length > 0 ? ImageSource.FromFile(file) : null;
		return new LibraryRow(e.Path, e.Title, string.Join("  ·  ", details), status, progress, e.Path == current, cover, (e as RemoteEntry)?.Copy, shown);
	}

	/// <summary>The key a book of this phone's folders is (or would be) synced under: its own, or the ASIN from the export's name, or name and size.</summary>
	static string? LocalKey(LibraryEntry e)
	{
		if (e.State?.SyncKey is { } known) return known;
		// A folder's key adds up the sizes of its files: kept while the book is unchanged (the library is listed
		// again at every return to it)
		var stamp = LibraryDetailsCache.StampOf(e.Path);
		lock (KeyCache)
			if (stamp != null && KeyCache.TryGetValue(e.Path, out var cached) && cached.Stamp == stamp) return cached.Key;
		var name = BookSource.IsFolder(e.Path) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(e.Path)) : Path.GetFileNameWithoutExtension(e.Path);
		var key = BookSync.KeyFor(e.Path, e.State?.Asin ?? AudibleExport.Parse(name).Asin);
		lock (KeyCache)
			if (stamp != null) KeyCache[e.Path] = (stamp, key);
		return key;
	}

	static readonly Dictionary<string, (string Stamp, string? Key)> KeyCache = [];

	/// <summary>The PCs' newest position for this book: under its key, or under the key an older version used.</summary>
	static SyncedPosition? SyncedFor(LibraryEntry e, Dictionary<string, SyncedPosition> synced)
	{
		var key = LocalKey(e);
		if (key != null && synced.TryGetValue(key, out var found)) return found;
		// Saved by an older version, under the name and size (only computed when the key was the ASIN)
		return key != null && key.StartsWith("asin:") && BookSync.LegacyKeyFor(e.Path) is { } old && synced.TryGetValue(old, out found) ? found : null;
	}

	static string FormatLength(TimeSpan t) =>
		t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";

	internal static View MakeRow()
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
/// <param name="Shown">Its status for the filters: this phone's, or the PC's when it listened more recently.</param>
sealed record LibraryRow(string Path, string Title, string Details, string Status, double Progress, bool Current, ImageSource? Cover,
	string? Badge = null, LibraryStatus Shown = LibraryStatus.NotStarted);

/// <summary>A book found by the last refresh (what the search, the filters and the order look at), with its row.</summary>
sealed class Listed : LibraryEntry
{
	public required LibraryRow Row { get; init; }
}

/// <summary>The library list's items: books, and the headers of authors or series (by author, by series).</summary>
sealed class RowOrGroup : DataTemplateSelector
{
	readonly DataTemplate _row = new(LibraryPage.MakeRow);
	readonly DataTemplate _group = new(MakeGroup);

	protected override DataTemplate OnSelectTemplate(object item, BindableObject container) => item is LibraryGroup ? _group : _row;

	/// <summary>"▾ Brandon Sanderson" and "5 books · 2 finished"; ▸ when closed.</summary>
	static View MakeGroup()
	{
		var name = new Label { FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Palette.Text, LineBreakMode = LineBreakMode.TailTruncation };
		name.SetBinding(Label.TextProperty, static (LibraryGroup g) => g, converter: new FuncConverter<LibraryGroup, string>(g => (g.Collapsed ? "▸  " : "▾  ") + g.Name));
		var count = new Label { FontSize = 13, TextColor = Palette.TextDim, VerticalTextAlignment = TextAlignment.Center };
		count.SetBinding(Label.TextProperty, static (LibraryGroup g) => g, converter: new FuncConverter<LibraryGroup, string>(g =>
			(g.Books == 1 ? "1 book" : $"{g.Books} books") + (g.Finished > 0 ? $" · {g.Finished} finished" : "")));
		var row = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], Padding = new Thickness(16, 14, 16, 8), BackgroundColor = Palette.Panel };
		row.Add(name, 0);
		row.Add(count, 1);
		return row;
	}
}
