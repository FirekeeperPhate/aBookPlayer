using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace aBookPlayer.Droid;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
// "abookplayer://connect?address=…&key=…": the button of the page the PC's QR code opens (File → Share with your phone)
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataScheme = "abookplayer", DataHost = "connect")]
public class MainActivity : MauiAppCompatActivity
{
	const int PickFolderRequest = 4101;
	static TaskCompletionSource<string?>? _pickFolder;

	/// <summary>A PC to connect to, from a link that opened the app; taken by the library page once it is shown.</summary>
	public static (string Address, string Key)? PendingConnect { get; set; }
	public static event Action? ConnectRequested;

	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
		TakeLink(Intent);
	}

	protected override void OnNewIntent(Intent? intent)
	{
		base.OnNewIntent(intent);
		TakeLink(intent);
	}

	static void TakeLink(Intent? intent)
	{
		if (intent?.Action != Intent.ActionView || intent.DataString is not { } link) return;
		if (RemoteLibraryClient.ParseAppLink(link) is not { } pc) return;
		PendingConnect = pc;
		ConnectRequested?.Invoke();
	}

	/// <summary>The system folder picker; returns the chosen folder's path, or null.</summary>
	public static Task<string?> PickFolderAsync()
	{
		_pickFolder?.TrySetResult(null);
		_pickFolder = new TaskCompletionSource<string?>();
		Platform.CurrentActivity!.StartActivityForResult(new Intent(Intent.ActionOpenDocumentTree), PickFolderRequest);
		return _pickFolder.Task;
	}

	protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
	{
		base.OnActivityResult(requestCode, resultCode, data);
		if (requestCode != PickFolderRequest) return;
		_pickFolder?.TrySetResult(resultCode == Result.Ok && data?.Data is { } tree ? StorageAccess.PathOfTree(tree) : null);
		_pickFolder = null;
	}
}
