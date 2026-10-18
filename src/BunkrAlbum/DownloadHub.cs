using Android.Content;

namespace BunkrAlbum;

sealed class WorkItem
{
    public required BunkrFile File { get; init; }
    public required string SaveName { get; init; }
    public string Status { get; set; } = "Wartet";
    public long LastUi { get; set; }
}

sealed class DownloadSession
{
    public required string AlbumTitle { get; init; }
    public required string Place { get; init; }
    public string? TreeUri { get; init; }
    public required List<WorkItem> Items { get; init; }
    public int Completed;
    public int Failed;
    public bool Running { get; set; }
    public string Summary { get; set; } = "Download startet …";
}

static class DownloadHub
{
    static readonly object Gate = new();
    static CancellationTokenSource? _cts;

    public static DownloadSession? Session { get; private set; }
    public static event Action? Changed;

    public static bool IsRunning
    {
        get { lock (Gate) return Session?.Running == true; }
    }

    public static CancellationToken Token
    {
        get { lock (Gate) return _cts?.Token ?? CancellationToken.None; }
    }

    public static bool TryBegin(DownloadSession session)
    {
        lock (Gate)
        {
            if (Session?.Running == true)
                return false;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            session.Running = true;
            Session = session;
        }

        Changed?.Invoke();
        return true;
    }

    public static void CancelToken()
    {
        lock (Gate)
            _cts?.Cancel();
    }

    public static void RequestCancel(Context context)
    {
        CancelToken();
        var intent = new Intent(context, typeof(DownloadService));
        intent.SetAction(DownloadService.ActionCancel);
        context.StartService(intent);
    }

    public static void Publish() => Changed?.Invoke();
}
