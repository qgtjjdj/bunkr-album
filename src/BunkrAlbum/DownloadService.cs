using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Environment = System.Environment;
using OperationCanceledException = System.OperationCanceledException;

namespace BunkrAlbum;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public class DownloadService : Service
{
    public const string ActionStart = "com.qgtjjdj.bunkralbum.START";
    public const string ActionCancel = "com.qgtjjdj.bunkralbum.CANCEL";
    const string ChannelId = "downloads";
    const int NotificationId = 71;

    NotificationManager? Notifications => (NotificationManager?)GetSystemService(NotificationService);
    bool _started;
    long _lastNote;
    PowerManager.WakeLock? _wake;
    readonly object _gate = new();

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        CreateChannel();
        if (intent?.Action == ActionCancel)
        {
            DownloadHub.CancelToken();
            lock (_gate)
            {
                if (!_started)
                {
                    StopSelf();
                    return StartCommandResult.NotSticky;
                }
            }

            return StartCommandResult.Sticky;
        }

        EnsureForeground("Download startet …", true);
        lock (_gate)
        {
            if (_started)
                return StartCommandResult.Sticky;
            _started = true;
            _stopped = false;
        }

        _ = RunAsync();
        return StartCommandResult.Sticky;
    }

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        // Swiping the app away must not stop an active download.
    }

    async Task RunAsync()
    {
        AcquireWake();
        var session = DownloadHub.Session;
        if (session is null)
        {
            Shutdown(null);
            return;
        }

        using var client = new BunkrClient();
        var ct = DownloadHub.Token;
        try
        {
            using var gate = new SemaphoreSlim(2);
            var tasks = session.Items.Select(item => DownloadItemAsync(client, session, item, gate, ct));
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                foreach (var item in session.Items)
                {
                    if (item.Status is "Wartet" or "Verbinde …" or "Neuer Versuch …")
                        item.Status = "Abgebrochen";
                }
            }

            session.Running = false;
            session.Summary = ct.IsCancellationRequested
                ? "Abgebrochen."
                : session.Failed == 0
                    ? $"Fertig. {session.Completed} Dateien unter {session.Place}."
                    : $"Fertig. {session.Completed} gespeichert, {session.Failed} fehlgeschlagen.";
            DownloadHub.Publish();
            Shutdown(session.Summary);
        }
        catch (Exception ex)
        {
            session.Running = false;
            session.Summary = ex.Message;
            DownloadHub.Publish();
            Shutdown(ex.Message);
        }
    }

    async Task DownloadItemAsync(
        BunkrClient client,
        DownloadSession session,
        WorkItem item,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var mime = BunkrUrls.GuessMime(item.File);
            Exception? last = null;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                item.Status = attempt == 1 ? "Verbinde …" : "Neuer Versuch …";
                Touch(session, item.File.Original, false);
                try
                {
                    await DownloadRepository.SaveAsync(this, session.TreeUri, item.SaveName, mime, async (stream, token) =>
                    {
                        var progress = new InlineProgress(bytes =>
                        {
                            var now = Environment.TickCount64;
                            item.Status = BunkrUrls.FormatSize(bytes);
                            if (now - item.LastUi <= 400)
                                return;
                            item.LastUi = now;
                            Touch(session, item.File.Original, false);
                        });
                        await client.DownloadAsync(item.File, stream, progress, token);
                    }, cancellationToken);
                    item.Status = "Gespeichert";
                    Interlocked.Increment(ref session.Completed);
                    Touch(session, item.File.Original, true);
                    return;
                }
                catch (OperationCanceledException)
                {
                    item.Status = "Abgebrochen";
                    throw;
                }
                catch (Exception ex) when (attempt < 2)
                {
                    last = ex;
                }
            }

            item.Status = last?.Message ?? "Fehler";
            Interlocked.Increment(ref session.Failed);
            Touch(session, item.File.Original, true);
        }
        finally
        {
            gate.Release();
        }
    }

    void Touch(DownloadSession session, string name, bool force)
    {
        var done = session.Completed + session.Failed;
        session.Summary = $"{done}/{session.Items.Count} · {Trim(name, 42)}";
        DownloadHub.Publish();
        UpdateNote(session, force);
    }

    void UpdateNote(DownloadSession session, bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - _lastNote < 1000)
            return;
        _lastNote = now;
        var text = session.Summary;
        var notification = BuildNotification(text, true, session.Completed + session.Failed, session.Items.Count);
        Notifications?.Notify(NotificationId, notification);
    }

    void EnsureForeground(string text, bool indeterminate)
    {
        var total = DownloadHub.Session?.Items.Count ?? 0;
        var done = DownloadHub.Session is null ? 0 : DownloadHub.Session.Completed + DownloadHub.Session.Failed;
        var notification = BuildNotification(text, true, indeterminate ? 0 : done, indeterminate ? 0 : total);
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
        else
            StartForeground(NotificationId, notification);
    }

    bool _stopped;

    void Shutdown(string? finalText)
    {
        lock (_gate)
        {
            if (_stopped)
                return;
            _stopped = true;
            _started = false;
        }

        if (!string.IsNullOrEmpty(finalText))
        {
            var done = BuildNotification(finalText, false, 0, 0);
            Notifications?.Notify(NotificationId, done);
        }

        ReleaseWake();
        if (OperatingSystem.IsAndroidVersionAtLeast(24))
            StopForeground(StopForegroundFlags.Detach);
        else
            StopForeground(false);
        StopSelf();
    }

    Notification BuildNotification(string text, bool ongoing, int done, int total)
    {
        var open = PendingIntent.GetActivity(
            this,
            1,
            new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var cancel = PendingIntent.GetService(
            this,
            2,
            new Intent(this, typeof(DownloadService)).SetAction(ActionCancel),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new Notification.Builder(this, ChannelId)
            .SetSmallIcon(Android.Resource.Drawable.StatSysDownload)
            .SetContentTitle(DownloadHub.Session?.AlbumTitle ?? "Bunkr Album")
            .SetContentText(text)
            .SetContentIntent(open)
            .SetOnlyAlertOnce(true)
            .SetOngoing(ongoing)
            .SetCategory(Notification.CategoryProgress);

        if (total > 0)
            builder.SetProgress(total, Math.Min(done, total), false);
        if (ongoing)
            builder.AddAction(new Notification.Action.Builder(0, "Abbrechen", cancel).Build());
        return builder.Build();
    }

    void CreateChannel()
    {
        var manager = Notifications;
        if (manager?.GetNotificationChannel(ChannelId) != null)
            return;
        var channel = new NotificationChannel(ChannelId, "Downloads", NotificationImportance.Low)
        {
            Description = "Laufende Album-Downloads"
        };
        manager?.CreateNotificationChannel(channel);
    }

    void AcquireWake()
    {
        if (_wake?.IsHeld == true)
            return;
        var power = (PowerManager?)GetSystemService(PowerService);
        _wake = power?.NewWakeLock(WakeLockFlags.Partial, "bunkr:download");
        _wake?.SetReferenceCounted(false);
        _wake?.Acquire(12L * 60 * 60 * 1000);
    }

    void ReleaseWake()
    {
        if (_wake?.IsHeld == true)
            _wake.Release();
        _wake = null;
    }

    static string Trim(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "…";

    sealed class InlineProgress : IProgress<long>
    {
        readonly Action<long> _report;
        public InlineProgress(Action<long> report) => _report = report;
        public void Report(long value) => _report(value);
    }
}
