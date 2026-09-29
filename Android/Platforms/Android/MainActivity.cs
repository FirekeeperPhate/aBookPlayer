using Android.App;
using Android.Content;
using Android.Content.PM;

namespace aBookPlayer.Droid;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
	const int PickFolderRequest = 4101;
	static TaskCompletionSource<string?>? _pickFolder;

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
