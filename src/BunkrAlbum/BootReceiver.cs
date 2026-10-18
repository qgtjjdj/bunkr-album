using Android.App;
using Android.Content;
using Android.Content.PM;

namespace BunkrAlbum;

[BroadcastReceiver(Enabled = true, Exported = true)]
[IntentFilter(new[] { Intent.ActionBootCompleted })]
public class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action != Intent.ActionBootCompleted)
            return;
        if (!QueueStore.HasPending(context))
            return;

        DownloadNotes.EnsureChannel(context);
        var open = PendingIntent.GetActivity(
            context,
            8,
            new Intent(context, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var notification = new Notification.Builder(context, DownloadNotes.ChannelId)
            .SetSmallIcon(Android.Resource.Drawable.StatSysDownload)
            .SetContentTitle("Bunkr Album")
            .SetContentText("Download interrupted. Tap to resume.")
            .SetContentIntent(open)
            .SetAutoCancel(true)
            .Build();
        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        manager?.Notify(72, notification);
    }
}
