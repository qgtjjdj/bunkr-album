using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Environment = System.Environment;
using OperationCanceledException = System.OperationCanceledException;

namespace BunkrAlbum;

static class DownloadNotes
{
    public const string ChannelId = "downloads";

    public static void EnsureChannel(Context context)
    {
        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        if (manager?.GetNotificationChannel(ChannelId) != null)
            return;
        var channel = new NotificationChannel(ChannelId, "Downloads", NotificationImportance.Low)
        {
            Description = "Laufende Album-Downloads"
        };
        manager?.CreateNotificationChannel(channel);
    }
}

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public class DownloadService : Service
{
    public const string ActionStart = "com.qgtjjdj.bunkralbum.START";
    public const string ActionResume = "com.qgtjjdj.bunkralbum.RESUME";
    public const string ActionPause = "com.qgtjjdj.bunkralbum.PAUSE";
    public const string ActionCancel = "com.qgtjjdj.bunkralbum.CANCEL";
    const int NotificationId = 71;

    bool _started;
    bool _stopped;
    long _lastNote;
    long _windowBytes;
    long _windowStart;
    double _speed;
    PowerManager.WakeLock? _wake;
    readonly object _gate = new();

    NotificationManager? Notifications => (NotificationManager?)GetSystemService(NotificationService);

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        DownloadNotes.EnsureChannel(this);
        DownloadHub.EnsureLoaded(this);
        var action = intent?.Action;

        if (action == ActionCancel)
        {
            DownloadHub.Cancel(this);
            if (!Alive)
                StopSelf();
            return Alive ? StartCommandResult.Sticky : StartCommandResult.NotSticky;
        }

        if (action == ActionPause)
        {
            DownloadHub.Pause(this);
            if (!Alive)
            {
                DownloadHub.FinishPaused(this);
                ShowFinal(DownloadHub.Session?.Summary ?? "Pausiert.", paused: true);
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            return StartCommandResult.Sticky;
        }

        if (intent is null && DownloadHub.IsPaused)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        if (!DownloadHub.HasPending && DownloadHub.Session is not null && action != ActionStart)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        var label = DownloadHub.Session?.Summary ?? "Download startet …";
        EnsureForeground(label);
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

    bool Alive
    {
        get { lock (_gate) return _started; }
    }

    public override void OnTaskRemoved(Intent? rootIntent)
    {
    }

    async Task RunAsync()
    {
        AcquireWake();
        DownloadHub.PrepareResume(this);
        DownloadHub.MarkAlive();
        using var client = new BunkrClient();
        var pending = new List<Task>();
        try
        {
            using var gate = new SemaphoreSlim(2);
            while (true)
            {
                var token = DownloadHub.Token;
                token.ThrowIfCancellationRequested();
                if (AppSettings.WifiOnly(this) && !OnWifi(this))
                {
                    var session = DownloadHub.Session;
                    var done = session is null ? 0 : session.SavedCount + session.SkippedCount;
                    var total = session?.Items.Count ?? 0;
                    DownloadHub.SetSummary($"Wartet auf WLAN · {done}/{total}");
                    UpdateNote(DownloadHub.Session, true);
                    await Task.Delay(2000, token);
                    continue;
                }

                var item = DownloadHub.Claim();
                if (item is null)
                {
                    await HarvestAsync(pending);
                    if (pending.Count > 0)
                    {
                        await Task.WhenAny(pending);
                        continue;
                    }

                    if (!await DownloadHub.WaitForWork(TimeSpan.FromSeconds(2), token))
                        break;
                    continue;
                }

                pending.Add(TransferAsync(client, item, gate, token));
            }

            await HarvestAsync(pending);
            if (pending.Count > 0)
                await Task.WhenAll(pending);
            Complete(StopKind.None);
        }
        catch (Exception) when (DownloadHub.PauseRequested || DownloadHub.Token.IsCancellationRequested)
        {
            try
            {
                if (pending.Count > 0)
                    await Task.WhenAll(pending);
            }
            catch (Exception)
            {
                // Workers end with the same cancellation.
            }

            var stop = DownloadHub.ConsumeStop();
            Complete(stop == StopKind.None ? StopKind.Pause : stop);
        }
        catch (Exception ex)
        {
            DownloadHub.FinishDone(this, ex.Message);
            Shutdown(ex.Message, paused: false);
        }
    }

    static async Task HarvestAsync(List<Task> pending)
    {
        var finished = pending.Where(task => task.IsCompleted).ToList();
        foreach (var task in finished)
            pending.Remove(task);
        if (finished.Count > 0)
            await Task.WhenAll(finished);
    }

    void Complete(StopKind stop)
    {
        if (stop == StopKind.Cancel)
        {
            DownloadHub.FinishCancelled(this);
            Shutdown("Abgebrochen.", paused: false);
            return;
        }

        if (stop == StopKind.Pause || DownloadHub.PauseRequested)
        {
            DownloadHub.FinishPaused(this);
            Shutdown(DownloadHub.Session?.Summary ?? "Pausiert.", paused: true);
            return;
        }

        var session = DownloadHub.Session;
        var saved = session?.SavedCount ?? 0;
        var skipped = session?.SkippedCount ?? 0;
        var failed = session?.FailedCount ?? 0;
        var place = session?.Items.LastOrDefault(item => item.IsDone)?.Place ?? StorageLocation.DefaultLabel;
        var summary = failed == 0
            ? $"Fertig. {saved + skipped} Dateien unter {place}."
            : $"Fertig. {saved + skipped} gespeichert, {failed} fehlgeschlagen.";
        DownloadHub.FinishDone(this, summary);
        Shutdown(summary, paused: false);
    }

    async Task TransferAsync(BunkrClient client, WorkItem item, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await TransferAsync(client, item, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            var pausing = DownloadHub.PauseRequested;
            item.Phase = pausing ? WorkPhase.Pending : WorkPhase.Failed;
            item.Status = pausing ? "Pausiert" : "Abgebrochen";
            if (!pausing)
                PartFiles.Delete(this, item);
            DownloadHub.Publish(this, persist: true);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    async Task TransferAsync(BunkrClient client, WorkItem item, CancellationToken cancellationToken)
    {
        var existing = DownloadRepository.ExistingSize(this, item.TreeUri, item.SaveName);
        if (existing is > 0 && (item.File.Size <= 0 || existing.Value >= item.File.Size))
        {
            item.Phase = WorkPhase.Skipped;
            item.Status = "Schon da";
            PartFiles.Delete(this, item);
            Touch(item, true);
            return;
        }

        var part = PartFiles.PathFor(this, item);
        Exception? last = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            item.Status = attempt == 1 ? "Verbinde …" : "Neuer Versuch …";
            Touch(item, true);
            try
            {
                var offset = File.Exists(part) ? new FileInfo(part).Length : 0;
                if (item.File.Size <= 0 || offset < item.File.Size)
                {
                    await using var stream = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                    offset = stream.Length;
                    stream.Seek(offset, SeekOrigin.Begin);
                    var lastReport = offset;
                    await client.DownloadAsync(item.File, stream, new InlineProgress(bytes =>
                    {
                        var delta = bytes - lastReport;
                        if (delta > 0)
                            AddSpeed(delta);
                        lastReport = bytes;
                        item.Status = BunkrUrls.FormatSize(bytes);
                        var now = Environment.TickCount64;
                        if (now - item.LastUi <= 400)
                            return;
                        item.LastUi = now;
                        Touch(item, false);
                    }), cancellationToken, offset);
                    await stream.FlushAsync(cancellationToken);
                }

                await DownloadRepository.SaveAsync(this, item.TreeUri, item.SaveName, BunkrUrls.GuessMime(item.File), async (destination, token) =>
                {
                    await using var source = File.OpenRead(part);
                    var buffer = new byte[80 * 1024];
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, token);
                        if (read == 0)
                            break;
                        await destination.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                }, cancellationToken);
                PartFiles.Delete(this, item);
                item.Phase = WorkPhase.Saved;
                item.Status = "Gespeichert";
                Touch(item, true);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < 2)
            {
                last = ex;
            }
        }

        item.Phase = WorkPhase.Failed;
        item.Status = last?.Message ?? "Fehler";
        Touch(item, true);
    }

    void Touch(WorkItem item, bool force)
    {
        var session = DownloadHub.Session;
        if (session is null)
            return;
        var done = session.SavedCount + session.SkippedCount;
        var speed = _speed >= 1 ? " · " + BunkrUrls.FormatSize((long)_speed) + "/s" : "";
        session.Summary = $"{done}/{session.Items.Count}{speed} · {Trim(item.File.Original, 36)}";
        DownloadHub.Publish(this, force);
        UpdateNote(session, force);
    }

    void AddSpeed(long bytes)
    {
        var now = Environment.TickCount64;
        if (_windowStart == 0)
            _windowStart = now;
        _windowBytes += bytes;
        var elapsed = now - _windowStart;
        if (elapsed < 1000)
            return;
        _speed = _windowBytes * 1000d / elapsed;
        _windowBytes = 0;
        _windowStart = now;
    }

    void UpdateNote(DownloadSession? session, bool force)
    {
        if (session is null)
            return;
        var now = Environment.TickCount64;
        if (!force && now - _lastNote < 1000)
            return;
        _lastNote = now;
        var done = session.SavedCount + session.SkippedCount + session.FailedCount;
        Notifications?.Notify(NotificationId, BuildNotification(session.Summary, true, done, session.Items.Count, paused: false));
    }

    void EnsureForeground(string text)
    {
        var session = DownloadHub.Session;
        var total = session?.Items.Count ?? 0;
        var done = session is null ? 0 : session.SavedCount + session.SkippedCount + session.FailedCount;
        var notification = BuildNotification(text, true, done, total, paused: false);
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
        else
            StartForeground(NotificationId, notification);
    }

    void ShowFinal(string text, bool paused)
    {
        Notifications?.Notify(NotificationId, BuildNotification(text, false, 0, 0, paused));
    }

    void Shutdown(string text, bool paused)
    {
        lock (_gate)
        {
            if (_stopped)
                return;
            _stopped = true;
            _started = false;
        }

        ShowFinal(text, paused);
        ReleaseWake();
        if (OperatingSystem.IsAndroidVersionAtLeast(24))
            StopForeground(StopForegroundFlags.Detach);
        else
            StopForeground(false);
        StopSelf();
    }

    Notification BuildNotification(string text, bool ongoing, int done, int total, bool paused)
    {
        var open = PendingIntent.GetActivity(
            this,
            1,
            new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var cancel = ServiceIntent(ActionCancel, 2);
        var builder = new Notification.Builder(this, DownloadNotes.ChannelId)
            .SetSmallIcon(Android.Resource.Drawable.StatSysDownload)
            .SetContentTitle(DownloadHub.Session?.Title ?? "Bunkr Album")
            .SetContentText(text)
            .SetContentIntent(open)
            .SetOnlyAlertOnce(true)
            .SetOngoing(ongoing)
            .SetCategory(Notification.CategoryProgress);
        if (total > 0)
            builder.SetProgress(total, Math.Min(done, total), false);
        if (ongoing)
            AddAction(builder, "Pause", ServiceIntent(ActionPause, 3));
        else if (paused)
            AddAction(builder, "Fortsetzen", ServiceIntent(ActionResume, 4));
        if (ongoing || paused)
            AddAction(builder, "Abbrechen", cancel);
        return builder.Build();
    }

    PendingIntent ServiceIntent(string action, int request)
    {
        var intent = new Intent(this, typeof(DownloadService)).SetAction(action);
        var flags = PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable;
        if (action == ActionResume)
            return PendingIntent.GetForegroundService(this, request, intent, flags)!;
        return PendingIntent.GetService(this, request, intent, flags)!;
    }

    static void AddAction(Notification.Builder builder, string title, PendingIntent intent)
    {
#pragma warning disable CA1422
        builder.AddAction(new Notification.Action(0, title, intent));
#pragma warning restore CA1422
    }

    static bool OnWifi(Context context)
    {
        var manager = (ConnectivityManager?)context.GetSystemService(ConnectivityService);
        var network = manager?.ActiveNetwork;
        var caps = network is null ? null : manager?.GetNetworkCapabilities(network);
        return caps is not null &&
               (caps.HasTransport(TransportType.Wifi) || caps.HasTransport(TransportType.Ethernet));
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
