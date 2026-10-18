using Android.App;
using Android.Graphics;
using Android.Widget;

namespace BunkrAlbum;

sealed class ThumbnailLoader
{
    const int Capacity = 80;
    readonly Activity _activity;
    readonly HttpClient _http;
    readonly SemaphoreSlim _slots = new(3);
    readonly Dictionary<string, Bitmap> _cache = new();
    readonly Queue<string> _order = new();
    readonly object _gate = new();

    public ThumbnailLoader(Activity activity)
    {
        _activity = activity;
        _http = new HttpClient(AndroidHttp.Create());
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BunkrClient.UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://bunkr.cr/");
    }

    public void Show(ImageView view, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            view.Tag = null;
            view.SetImageDrawable(null);
            return;
        }

        view.Tag = new Java.Lang.String(url);
        if (TryGet(url, out var cached) && cached is not null)
        {
            view.SetImageBitmap(cached);
            return;
        }

        view.SetImageDrawable(null);
        _ = LoadAsync(view, url);
    }

    async Task LoadAsync(ImageView view, string url)
    {
        var taken = false;
        try
        {
            await _slots.WaitAsync();
            taken = true;
            if (!TryGet(url, out var bitmap))
            {
                var bytes = await _http.GetByteArrayAsync(url);
                bitmap = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
                if (bitmap is null)
                    return;
                Remember(url, bitmap);
            }

            if (bitmap is null)
                return;
            var image = bitmap;
            _activity.RunOnUiThread(() =>
            {
                if (view.Tag?.ToString() == url)
                    view.SetImageBitmap(image);
            });
        }
        catch (Exception)
        {
            // A missing preview should not block the list.
        }
        finally
        {
            if (taken)
                _slots.Release();
        }
    }

    bool TryGet(string url, out Bitmap? bitmap)
    {
        lock (_gate)
            return _cache.TryGetValue(url, out bitmap);
    }

    void Remember(string url, Bitmap bitmap)
    {
        lock (_gate)
        {
            if (_cache.ContainsKey(url))
                return;
            _cache[url] = bitmap;
            _order.Enqueue(url);
            while (_order.Count > Capacity)
                _cache.Remove(_order.Dequeue());
        }
    }
}
