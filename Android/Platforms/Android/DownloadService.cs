using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidApp = Android.App.Application;

namespace aBookPlayer.Droid;

/// <summary>
/// Keeps the app alive while books are copied to the phone (Android may close an app in the background, and the
/// copy with it), showing how far they are in a notification. Stops by itself when nothing is left to copy.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public class DownloadService : Service
{
	const int NotificationId = 4102;
	const string Channel = "downloads";
	Timer? _timer;

	/// <summary>Starts the service (or keeps it going) while a download runs.</summary>
	public static void Start()
	{
		var context = AndroidApp.Context;
		var intent = new Intent(context, typeof(DownloadService));
		try
		{
			if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(intent);
			else context.StartService(intent);
		}
		catch (Exception)
		{
			// Not allowed from the background (Android 12+): the copy still goes on while the app lives
		}
	}

	public override IBinder? OnBind(Intent? intent) => null;

	public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
	{
		CreateChannel();
		var notification = Build(Downloads.Summary());
		try
		{
			if (OperatingSystem.IsAndroidVersionAtLeast(29)) StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
			else StartForeground(NotificationId, notification);
		}
		catch (Exception)
		{
			// Not allowed now (the app went to the background meanwhile): the copy goes on while the app lives
			StopSelf();
			return StartCommandResult.NotSticky;
		}
		_timer ??= new Timer(_ => Update(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
		return StartCommandResult.NotSticky;
	}

	/// <summary>Android 15 allows a few hours a day of this kind of service: then it stops (the copy goes on while the app lives).</summary>
	public override void OnTimeout(int startId, ForegroundService fgsType) => Stop();

	void Update()
	{
		var summary = Downloads.Summary();
		if (summary.Count == 0)
		{
			Stop();
			return;
		}
		NotificationManagerCompat.From(this)!.Notify(NotificationId, Build(summary));
	}

	void Stop()
	{
		_timer?.Dispose();
		_timer = null;
		if (OperatingSystem.IsAndroidVersionAtLeast(24)) StopForeground(StopForegroundFlags.Remove);
		StopSelf();
	}

	public override void OnDestroy()
	{
		_timer?.Dispose();
		_timer = null;
		base.OnDestroy();
	}

	/// <summary>"Downloading The Long Night · 41%" (or "Downloading 3 books"), with its bar; tapping it opens the app.</summary>
	Notification Build((int Count, string? Title, double Progress) summary)
	{
		var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop),
			PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
		return new NotificationCompat.Builder(this, Channel)
			.SetSmallIcon(Resource.Drawable.ic_notification)!
			.SetContentTitle(summary.Count > 1 ? $"Downloading {summary.Count} books" : $"Downloading {summary.Title}")!
			.SetContentText($"To listen away from home · {summary.Progress:P0}")!
			.SetProgress(100, (int)(summary.Progress * 100), false)!
			.SetOngoing(true)!
			.SetOnlyAlertOnce(true)!
			.SetSilent(true)!
			.SetContentIntent(open)!
			.Build()!;
	}

	void CreateChannel()
	{
		if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
		var channel = new NotificationChannel(Channel, "Downloads", NotificationImportance.Low) { Description = "Books being copied to this phone" };
		((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(channel);
	}
}
