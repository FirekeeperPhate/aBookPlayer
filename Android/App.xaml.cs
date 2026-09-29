namespace aBookPlayer.Droid;

public partial class App : Application
{
	/// <summary>Shared by the pages: the settings (with each book's state) and the player.</summary>
	internal static MobileSettings Settings { get; } = MobileSettings.Load();
	internal static BookPlayer Player { get; } = new();

	/// <summary>False while the app is in the background (a book may end there: its successor is offered on return).</summary>
	static bool _inFront = true;

	public App()
	{
		InitializeComponent();
		UserAppTheme = AppTheme.Dark;
		// Details of books never opened are read once and kept here
		LibraryDetailsCache.FilePath = Path.Combine(FileSystem.AppDataDirectory, "details.json");
		// How the PCs see this phone ("continuing where you stopped on Pixel 8"), and its sync file's name
		BookSync.MachineName = string.IsNullOrWhiteSpace(DeviceInfo.Current.Name) ? DeviceInfo.Current.Model : DeviceInfo.Current.Name;
		Player.NextFound += () => { if (_inFront) _ = OfferNextAsync(); };
		// Back on Wi-Fi: the downloads waiting for it go on
		Connectivity.Current.ConnectivityChanged += (_, _) =>
		{
			if (Downloads.MayDownloadNow()) MainThread.BeginInvokeOnMainThread(() => Downloads.Resume(Settings.Servers.Select(s => s.Address).ToList()));
		};
	}

	protected override Window CreateWindow(IActivationState? activationState) =>
		new(new NavigationPage(new LibraryPage()) { BarBackgroundColor = Palette.Panel, BarTextColor = Palette.Text });

	/// <summary>Back to the app (from the system settings page that grants access to the files, for example).</summary>
	internal static event Action? Resumed;

	protected override void OnResume()
	{
		_inFront = true;
		Resumed?.Invoke();
		// A book ended meanwhile: go on with the series?
		if (Player.PendingNext != null) _ = OfferNextAsync();
		// A PC may have moved on in the book meanwhile
		else _ = Player.CheckSyncAsync();
	}

	protected override void OnSleep()
	{
		_inFront = false;
		// Leaving the app (it may be killed later): keep the position
		Player.SavePosition();
		Settings.Save();
	}

	/// <summary>"You finished X. Continue with Y (Series, Book 2)?", once; yes opens and plays it.</summary>
	async Task OfferNextAsync()
	{
		if (Player.PendingNext is not var (finished, next)) return;
		Player.PendingNext = null;
		if (Windows.FirstOrDefault()?.Page is not NavigationPage navigation || navigation.CurrentPage is not { } page) return;
		var label = AudibleExport.SeriesLabel(next.Series, next.Number);
		if (!await page.DisplayAlertAsync("Finished", $"You finished \"{finished}\".\n\nContinue with \"{next.Title}\" ({label})?", "Continue", "Not now"))
			return;
		// Playback started again meanwhile (from the notification): leave it be
		if (Player.IsPlaying) return;
		try
		{
			await Player.OpenAsync(next.Path, play: true);
			if (navigation.CurrentPage is not PlayerPage) await navigation.PushAsync(new PlayerPage());
		}
		catch (Exception ex)
		{
			await page.DisplayAlertAsync("aBookPlayer", "Could not open the book:\n" + ex.Message, "OK");
		}
	}
}
