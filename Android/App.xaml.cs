namespace aBookPlayer.Droid;

public partial class App : Application
{
	/// <summary>Shared by the pages: the settings (with each book's state) and the player.</summary>
	internal static MobileSettings Settings { get; } = MobileSettings.Load();
	internal static BookPlayer Player { get; } = new();

	public App()
	{
		InitializeComponent();
		UserAppTheme = AppTheme.Dark;
		// Details of books never opened are read once and kept here
		LibraryDetailsCache.FilePath = Path.Combine(FileSystem.AppDataDirectory, "details.json");
	}

	protected override Window CreateWindow(IActivationState? activationState) =>
		new(new NavigationPage(new LibraryPage()) { BarBackgroundColor = Palette.Panel, BarTextColor = Palette.Text });

	/// <summary>Back to the app (from the system settings page that grants access to the files, for example).</summary>
	internal static event Action? Resumed;

	protected override void OnResume() => Resumed?.Invoke();

	protected override void OnSleep()
	{
		// Leaving the app (it may be killed later): keep the position
		Player.SavePosition();
		Settings.Save();
	}
}
