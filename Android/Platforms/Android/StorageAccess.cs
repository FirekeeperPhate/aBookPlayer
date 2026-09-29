using Android.Content;
using Android.OS;
using Android.Provider;
using AndroidX.Core.Content;

namespace aBookPlayer.Droid;

/// <summary>
/// Reading the books (and writing the sync file) by path, in any folder: "access to all files" on Android 11 and
/// later, the storage permissions before.
/// </summary>
static class StorageAccess
{
	public static bool Granted => OperatingSystem.IsAndroidVersionAtLeast(30)
		? Android.OS.Environment.IsExternalStorageManager
		: ContextCompat.CheckSelfPermission(Platform.AppContext, Android.Manifest.Permission.ReadExternalStorage) == Android.Content.PM.Permission.Granted;

	/// <summary>
	/// Asks for it. On Android 11+ this opens the system page where the user turns it on: the answer is known when
	/// the app comes back (check <see cref="Granted"/> again then).
	/// </summary>
	public static async Task<bool> RequestAsync()
	{
		if (Granted) return true;
		if (OperatingSystem.IsAndroidVersionAtLeast(30))
		{
			var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission,
				Android.Net.Uri.Parse("package:" + Platform.AppContext.PackageName));
			try { Platform.CurrentActivity!.StartActivity(intent); }
			catch (ActivityNotFoundException)
			{
				// Some devices only have the general page, with every app listed
				Platform.CurrentActivity!.StartActivity(new Intent(Settings.ActionManageAllFilesAccessPermission));
			}
			return false;
		}
		var read = await Permissions.RequestAsync<Permissions.StorageRead>();
		if (Build.VERSION.SdkInt <= BuildVersionCodes.Q) await Permissions.RequestAsync<Permissions.StorageWrite>();
		return read == PermissionStatus.Granted;
	}

	/// <summary>
	/// The path of a folder chosen with the system picker: its document id is "primary:Audiobooks/Sync" for the
	/// phone's own storage, "1A2B-3C4D:Books" for a memory card.
	/// </summary>
	public static string? PathOfTree(Android.Net.Uri tree)
	{
		try
		{
			var id = DocumentsContract.GetTreeDocumentId(tree);
			if (id == null) return null;
			int colon = id.IndexOf(':');
			var volume = colon >= 0 ? id[..colon] : id;
			var relative = colon >= 0 ? id[(colon + 1)..] : "";
			var root = volume.Equals("primary", StringComparison.OrdinalIgnoreCase)
				? Android.OS.Environment.ExternalStorageDirectory!.AbsolutePath
				: "/storage/" + volume;
			return relative.Length > 0 ? Path.Combine(root, relative) : root;
		}
		catch { return null; }
	}
}
