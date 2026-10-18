using Android.Content;

namespace BunkrAlbum;

enum WorkPhase
{
    Pending,
    Active,
    Saved,
    Skipped,
    Failed,
}

enum StopKind
{
    None,
    Pause,
    Cancel,
}

sealed class WorkItem
{
    public required BunkrFile File { get; init; }
    public required string SaveName { get; init; }
    public required string AlbumTitle { get; init; }
    public required string Place { get; init; }
    public string? TreeUri { get; init; }
    public WorkPhase Phase { get; set; } = WorkPhase.Pending;
    public string Status { get; set; } = "Waiting";
    public long LastUi { get; set; }

    public bool IsDone => Phase is WorkPhase.Saved or WorkPhase.Skipped;
}

sealed class DownloadSession
{
    public List<WorkItem> Items { get; } = [];
    public bool Running { get; set; }
    public bool ServiceAlive { get; set; }
    public bool Paused { get; set; }
    public string Summary { get; set; } = "Download starting…";

    public string Title
    {
        get
        {
            var titles = Items.Select(item => item.AlbumTitle).Distinct().ToList();
            if (titles.Count == 0)
                return "Bunkr Album";
            return titles.Count == 1 ? titles[0] : "Queue · " + titles.Count + " albums";
        }
    }

    public int SavedCount => Items.Count(item => item.Phase == WorkPhase.Saved);
    public int SkippedCount => Items.Count(item => item.Phase == WorkPhase.Skipped);
    public int FailedCount => Items.Count(item => item.Phase == WorkPhase.Failed);
    public bool HasPending => Items.Any(item => !item.IsDone && item.Status != "Cancelled");
}

static class DownloadHub
{
    static readonly object Gate = new();
    static readonly SemaphoreSlim Pulse = new(0);
    static CancellationTokenSource? _cts;
    static StopKind _stop;
    static bool _loaded;

    public static DownloadSession? Session { get; private set; }
    public static event Action? Changed;

    public static bool IsRunning
    {
        get { lock (Gate) return Session?.Running == true; }
    }

    public static bool IsPaused
    {
        get { lock (Gate) return Session?.Paused == true && Session.Running != true; }
    }

    public static bool HasPending
    {
        get { lock (Gate) return Session?.HasPending == true; }
    }

    public static CancellationToken Token
    {
        get { lock (Gate) return _cts?.Token ?? CancellationToken.None; }
    }

    public static void EnsureLoaded(Context context)
    {
        lock (Gate)
        {
            if (_loaded)
                return;
            _loaded = true;
            Session = QueueStore.Load(context);
        }
    }

    public static bool Enqueue(Context context, IReadOnlyList<WorkItem> items)
    {
        bool alreadyRunning;
        lock (Gate)
        {
            Session ??= new DownloadSession();
            foreach (var item in items)
            {
                var known = Session.Items.Any(existing =>
                    existing.File.Id == item.File.Id &&
                    existing.SaveName == item.SaveName &&
                    existing.Status != "Cancelled");
                if (!known)
                    Session.Items.Add(item);
            }

            Session.Paused = false;
            _stop = StopKind.None;
            RenewToken();
            alreadyRunning = Session.ServiceAlive;
            Session.Running = true;
            QueueStore.Save(context, Session);
        }

        Kick();
        Changed?.Invoke();
        return alreadyRunning;
    }

    public static void MarkAlive()
    {
        lock (Gate)
        {
            if (Session is null)
                return;
            Session.ServiceAlive = true;
            Session.Running = true;
            Session.Paused = false;
        }
    }

    public static void PrepareResume(Context context)
    {
        lock (Gate)
        {
            if (Session is null)
                return;
            foreach (var item in Session.Items)
            {
                if (item.Phase == WorkPhase.Active)
                    item.Phase = WorkPhase.Pending;
                if (item.Phase == WorkPhase.Failed && item.Status != "Cancelled")
                    item.Phase = WorkPhase.Pending;
            }

            Session.Paused = false;
            _stop = StopKind.None;
            RenewToken();
            QueueStore.Save(context, Session);
        }

        Changed?.Invoke();
    }

    public static WorkItem? Claim()
    {
        lock (Gate)
        {
            var item = Session?.Items.FirstOrDefault(candidate => candidate.Phase == WorkPhase.Pending);
            if (item is null)
                return null;
            item.Phase = WorkPhase.Active;
            return item;
        }
    }

    public static void Pause(Context context)
    {
        lock (Gate)
        {
            _stop = StopKind.Pause;
            _cts?.Cancel();
            if (Session is { Running: false })
            {
                Session.Paused = true;
                QueueStore.Save(context, Session);
            }
        }

        Changed?.Invoke();
    }

    public static void Cancel(Context context)
    {
        bool running;
        lock (Gate)
        {
            _stop = StopKind.Cancel;
            _cts?.Cancel();
            running = Session?.Running == true;
        }

        if (!running)
            FinishCancelled(context);
    }

    public static StopKind ConsumeStop()
    {
        lock (Gate)
        {
            var stop = _stop;
            _stop = StopKind.None;
            return stop;
        }
    }

    public static bool PauseRequested
    {
        get { lock (Gate) return _stop == StopKind.Pause; }
    }

    public static void FinishPaused(Context context)
    {
        lock (Gate)
        {
            if (Session is null)
                return;
            Session.Running = false;
            Session.ServiceAlive = false;
            Session.Paused = true;
            var open = Session.Items.Count(item => !item.IsDone && item.Status != "Cancelled");
            Session.Summary = open == 0 ? "Paused." : $"Paused. {open} left.";
            QueueStore.Save(context, Session);
        }

        Changed?.Invoke();
    }

    public static void FinishCancelled(Context context)
    {
        lock (Gate)
        {
            if (Session is not null)
            {
                foreach (var item in Session.Items)
                {
                    if (item.IsDone || item.Status == "Cancelled")
                        continue;
                    item.Phase = WorkPhase.Failed;
                    item.Status = "Cancelled";
                    PartFiles.Delete(context, item);
                }

                Session.Running = false;
                Session.ServiceAlive = false;
                Session.Paused = false;
                Session.Summary = "Cancelled.";
            }

            QueueStore.Delete(context);
            _stop = StopKind.None;
        }

        Changed?.Invoke();
    }

    public static void FinishDone(Context context, string summary)
    {
        lock (Gate)
        {
            if (Session is not null)
            {
                Session.Running = false;
                Session.ServiceAlive = false;
                Session.Paused = false;
                Session.Summary = summary;
            }

            QueueStore.Delete(context);
            _stop = StopKind.None;
        }

        Changed?.Invoke();
    }

    public static void SetSummary(string summary)
    {
        lock (Gate)
        {
            if (Session is not null)
                Session.Summary = summary;
        }

        Changed?.Invoke();
    }

    public static void Publish(Context? context, bool persist)
    {
        if (persist && context is not null)
        {
            lock (Gate)
            {
                if (Session is not null)
                    QueueStore.Save(context, Session);
            }
        }

        Changed?.Invoke();
    }

    public static Task<bool> WaitForWork(TimeSpan timeout, CancellationToken cancellationToken)
        => Pulse.WaitAsync(timeout, cancellationToken);

    static void Kick()
    {
        try
        {
            if (Pulse.CurrentCount == 0)
                Pulse.Release();
        }
        catch (SemaphoreFullException)
        {
            // A worker is already being woken.
        }
    }

    static void RenewToken()
    {
        if (_cts is { IsCancellationRequested: false })
            return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
    }
}
