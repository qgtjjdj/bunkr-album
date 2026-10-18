using System.Text.Json;
using Android.Content;

namespace BunkrAlbum;

static class AppSettings
{
    const string PrefsName = "bunkr";
    const string WifiKey = "wifi_only";

    public static bool WifiOnly(Context context)
        => context.GetSharedPreferences(PrefsName, FileCreationMode.Private)?.GetBoolean(WifiKey, false) ?? false;

    public static void SetWifiOnly(Context context, bool value)
    {
        context.GetSharedPreferences(PrefsName, FileCreationMode.Private)?
            .Edit()?
            .PutBoolean(WifiKey, value)?
            .Apply();
    }
}

static class PartFiles
{
    public static string PathFor(Context context, WorkItem item)
    {
        var root = context.FilesDir?.AbsolutePath ?? throw new BunkrException("Kein App-Speicher verfügbar.");
        var dir = Path.Combine(root, "partials");
        Directory.CreateDirectory(dir);
        var key = item.File.Id + "|" + item.SaveName;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        return Path.Combine(dir, hash + ".part");
    }

    public static void Delete(Context context, WorkItem item)
    {
        try
        {
            var path = PathFor(context, item);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // A locked partial file is cleaned up on the next successful save.
        }
    }
}

static class QueueStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static void Save(Context context, DownloadSession session)
    {
        var path = FilePath(context);
        if (path is null)
            return;

        var stored = new StoredQueue
        {
            UserPaused = session.Paused,
            Items = session.Items.Select(item => new StoredItem
            {
                AlbumTitle = item.AlbumTitle,
                Place = item.Place,
                TreeUri = item.TreeUri,
                Id = item.File.Id,
                Name = item.File.Name,
                Original = item.File.Original,
                Slug = item.File.Slug,
                Type = item.File.Type,
                Extension = item.File.Extension,
                Size = item.File.Size,
                Timestamp = item.File.Timestamp,
                Thumbnail = item.File.Thumbnail,
                SaveName = item.SaveName,
                Phase = item.Phase.ToString(),
                Status = item.Status,
            }).ToList(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(stored, Json));
    }

    public static DownloadSession? Load(Context context)
    {
        var path = FilePath(context);
        if (path is null || !File.Exists(path))
            return null;

        try
        {
            var stored = JsonSerializer.Deserialize<StoredQueue>(File.ReadAllText(path));
            if (stored?.Items is null || stored.Items.Count == 0)
                return null;

            var session = new DownloadSession { Paused = stored.UserPaused, Running = false };
            foreach (var item in stored.Items)
            {
                if (!Enum.TryParse<WorkPhase>(item.Phase, out var phase))
                    phase = WorkPhase.Pending;
                if (phase == WorkPhase.Active)
                    phase = WorkPhase.Pending;
                session.Items.Add(new WorkItem
                {
                    AlbumTitle = item.AlbumTitle,
                    Place = item.Place,
                    TreeUri = item.TreeUri,
                    SaveName = item.SaveName,
                    Phase = phase,
                    Status = string.IsNullOrWhiteSpace(item.Status) ? "Wartet" : item.Status,
                    File = new BunkrFile(
                        item.Id,
                        item.Name,
                        item.Original,
                        item.Slug,
                        item.Type,
                        item.Extension,
                        item.Size,
                        item.Timestamp,
                        item.Thumbnail),
                });
            }

            var open = session.Items.Count(candidate => !candidate.IsDone && candidate.Status != "Abgebrochen");
            session.Summary = session.Paused
                ? (open == 0 ? "Pausiert." : $"Pausiert. {open} offen.")
                : (open == 0 ? "Fertig." : $"{session.SavedCount + session.SkippedCount}/{session.Items.Count} bereit");
            return session.Items.Count == 0 ? null : session;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool HasPending(Context context)
    {
        var session = Load(context);
        return session?.HasPending == true;
    }

    public static void Delete(Context context)
    {
        var path = FilePath(context);
        if (path is not null && File.Exists(path))
            File.Delete(path);
    }

    static string? FilePath(Context context)
    {
        var root = context.FilesDir?.AbsolutePath;
        return root is null ? null : Path.Combine(root, "queue.json");
    }

    sealed class StoredQueue
    {
        public bool UserPaused { get; set; }
        public List<StoredItem> Items { get; set; } = [];
    }

    sealed class StoredItem
    {
        public string AlbumTitle { get; set; } = "";
        public string Place { get; set; } = "";
        public string? TreeUri { get; set; }
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Original { get; set; } = "";
        public string Slug { get; set; } = "";
        public string Type { get; set; } = "";
        public string Extension { get; set; } = "";
        public long Size { get; set; }
        public string Timestamp { get; set; } = "";
        public string Thumbnail { get; set; } = "";
        public string SaveName { get; set; } = "";
        public string Phase { get; set; } = nameof(WorkPhase.Pending);
        public string Status { get; set; } = "Wartet";
    }
}
