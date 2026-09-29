using Android.App;
using Android.Content;
using Android.Content.PM;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.Session;

namespace aBookPlayer.Droid;

/// <summary>
/// The player, alive in the background: Media3's ExoPlayer in a media session. Media3 shows the notification with
/// its controls, the lock screen and the headset buttons; the player pauses when headphones are unplugged and gives
/// the audio way to calls and navigation (audio focus).
/// </summary>
[Service(Exported = true, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
[IntentFilter(["androidx.media3.session.MediaSessionService"])]
public class PlaybackService : MediaSessionService
{
    MediaSession? _session;

    /// <summary>The player itself, for what a controller cannot do (skipping silences); null while the service is not running.</summary>
    public static IExoPlayer? Player { get; private set; }

    public override void OnCreate()
    {
        base.OnCreate();
        var speech = new AudioAttributes.Builder()
            .SetUsage(C.UsageMedia)!
            .SetContentType(C.AudioContentTypeSpeech)!
            .Build()!;
        var player = new ExoPlayerBuilder(this)
            .SetAudioAttributes(speech, true)! // handles audio focus
            .SetHandleAudioBecomingNoisy(true)!
            .SetWakeMode(C.WakeModeLocal)!
            .SetSeekBackIncrementMs(10_000)!
            .SetSeekForwardIncrementMs(30_000)!
            .Build()!;
        Player = player;

        // Tapping the notification opens the app
        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        _session = new MediaSession.Builder(this, player).SetSessionActivity(open!)!.Build();

        // The app's emblem in the status bar, instead of Media3's generic note
        var notification = new DefaultMediaNotificationProvider.Builder(this).Build()!;
        notification.SetSmallIcon(Resource.Drawable.ic_notification);
        SetMediaNotificationProvider(notification);
    }

    public override MediaSession? OnGetSession(MediaSession.ControllerInfo? controllerInfo) => _session;

    /// <summary>The app swiped away from the recent apps: keep playing if it plays, else stop the service.</summary>
    public override void OnTaskRemoved(Intent? rootIntent)
    {
        var player = _session?.Player;
        if (player == null || !player.PlayWhenReady || player.MediaItemCount == 0) StopSelf();
    }

    public override void OnDestroy()
    {
        _session?.Player?.Release();
        _session?.Release();
        _session = null;
        Player = null;
        base.OnDestroy();
    }
}
