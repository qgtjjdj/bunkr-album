using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using Android.Views;
using Environment = System.Environment;
using OperationCanceledException = System.OperationCanceledException;

namespace BunkrAlbum;

[Activity(
    Label = "@string/app_name",
    MainLauncher = true,
    Exported = true,
    Theme = "@style/AppTheme",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden,
    LaunchMode = LaunchMode.SingleTop)]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "text/plain")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "https", DataHost = "bunkr.cr", DataPathPrefix = "/a/")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "https", DataHost = "bunkr.cr", DataPathPrefix = "/f/")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "https", DataHost = "*.bunkr.cr", DataPathPrefix = "/a/")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "https", DataHost = "*.bunkr.cr", DataPathPrefix = "/f/")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "https", DataHost = "bunkr.si", DataPathPrefix = "/a/")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "https", DataHost = "bunkr.su", DataPathPrefix = "/a/")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "https", DataHost = "bunkr.sk", DataPathPrefix = "/a/")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "https", DataHost = "bunkr.site", DataPathPrefix = "/a/")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "https", DataHost = "bunkrr.su", DataPathPrefix = "/a/")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "http", DataHost = "bunkr.cr", DataPathPrefix = "/a/")]
public class MainActivity : Activity
{
    const int PickFolderRequest = 41;

    readonly BunkrClient _client = new(AndroidHttp.Create());
    readonly List<FileRow> _rows = [];
    readonly List<FileRow> _visible = [];
    EditText _url = null!;
    TextView _title = null!;
    TextView _status = null!;
    TextView _location = null!;
    CheckBox _selectAll = null!;
    CheckBox _wifi = null!;
    ListView _list = null!;
    LinearLayout _filterRow = null!;
    Button _load = null!;
    Button _cancel = null!;
    Button _pause = null!;
    Button _download = null!;
    Button _locationButton = null!;
    Button _filterAll = null!;
    Button _filterImages = null!;
    Button _filterVideos = null!;
    EditText _search = null!;
    LinearLayout _sortRow = null!;
    Button _sortName = null!;
    Button _sortSize = null!;
    Button _sortType = null!;
    Button _retry = null!;
    Button _parallel1 = null!;
    Button _parallel2 = null!;
    Button _parallel3 = null!;
    Button _parallel4 = null!;
    FileListAdapter? _adapter;
    CancellationTokenSource? _work;
    FileFilter _filter = FileFilter.All;
    SortMode _sort = SortMode.Name;
    string _query = "";
    bool _suppressSelectAll;
    bool _busy;
    long _lastDraw;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);

        _url = RequireView<EditText>(Resource.Id.url_input);
        _title = RequireView<TextView>(Resource.Id.album_title);
        _status = RequireView<TextView>(Resource.Id.status_text);
        _location = RequireView<TextView>(Resource.Id.location_text);
        _selectAll = RequireView<CheckBox>(Resource.Id.select_all);
        _list = RequireView<ListView>(Resource.Id.file_list);
        _load = RequireView<Button>(Resource.Id.load_button);
        _cancel = RequireView<Button>(Resource.Id.cancel_button);
        _pause = RequireView<Button>(Resource.Id.pause_button);
        _download = RequireView<Button>(Resource.Id.download_button);
        _locationButton = RequireView<Button>(Resource.Id.location_button);
        _wifi = RequireView<CheckBox>(Resource.Id.wifi_only);
        _filterRow = RequireView<LinearLayout>(Resource.Id.filter_row);
        _filterAll = RequireView<Button>(Resource.Id.filter_all);
        _filterImages = RequireView<Button>(Resource.Id.filter_images);
        _filterVideos = RequireView<Button>(Resource.Id.filter_videos);
        _search = RequireView<EditText>(Resource.Id.search_input);
        _sortRow = RequireView<LinearLayout>(Resource.Id.sort_row);
        _sortName = RequireView<Button>(Resource.Id.sort_name);
        _sortSize = RequireView<Button>(Resource.Id.sort_size);
        _sortType = RequireView<Button>(Resource.Id.sort_type);
        _retry = RequireView<Button>(Resource.Id.retry_button);
        _parallel1 = RequireView<Button>(Resource.Id.parallel_1);
        _parallel2 = RequireView<Button>(Resource.Id.parallel_2);
        _parallel3 = RequireView<Button>(Resource.Id.parallel_3);
        _parallel4 = RequireView<Button>(Resource.Id.parallel_4);

        _load.Click += async (_, _) => await LoadAlbumAsync();
        _download.Click += (_, _) => StartDownload();
        _pause.Click += (_, _) =>
        {
            if (DownloadHub.IsRunning)
                DownloadHub.Pause(this);
        };
        _cancel.Click += (_, _) =>
        {
            _work?.Cancel();
            if (DownloadHub.IsRunning || DownloadHub.HasPending || DownloadHub.IsPaused)
                DownloadHub.Cancel(this);
        };
        _locationButton.Click += (_, _) => ChooseLocation();
        _filterAll.Click += (_, _) => SetFilter(FileFilter.All);
        _filterImages.Click += (_, _) => SetFilter(FileFilter.Images);
        _filterVideos.Click += (_, _) => SetFilter(FileFilter.Videos);
        _sortName.Click += (_, _) => SetSort(SortMode.Name);
        _sortSize.Click += (_, _) => SetSort(SortMode.Size);
        _sortType.Click += (_, _) => SetSort(SortMode.Type);
        _parallel1.Click += (_, _) => SetParallel(1);
        _parallel2.Click += (_, _) => SetParallel(2);
        _parallel3.Click += (_, _) => SetParallel(3);
        _parallel4.Click += (_, _) => SetParallel(4);
        _retry.Click += (_, _) => RetryFailed();
        _search.TextChanged += (_, _) =>
        {
            _query = _search.Text?.Trim() ?? "";
            BindRows();
        };
        _list.ItemClick += (_, e) => OpenRow(e.Position);
        _wifi.Checked = AppSettings.WifiOnly(this);
        _wifi.CheckedChange += (_, e) => AppSettings.SetWifiOnly(this, e.IsChecked);
        _selectAll.CheckedChange += (_, e) =>
        {
            if (_suppressSelectAll)
                return;
            foreach (var row in _visible)
                row.Selected = e.IsChecked;
            _adapter?.NotifyDataSetChanged();
            UpdateDownloadButton();
        };
        DownloadHub.EnsureLoaded(this);
        PaintParallel();
        UpdateLocationLabel();
        DownloadHub.Changed += OnHubChanged;
        ApplySession();
        AcceptLink(Intent);
        MaybeResume();
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent is not null)
            Intent = intent;
        AcceptLink(intent);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PickFolderRequest || resultCode != Result.Ok || data?.Data is null)
            return;

        var uri = data.Data;
        try
        {
            ContentResolver?.TakePersistableUriPermission(
                uri,
                ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        }
        catch (Exception)
        {
            Toast.MakeText(this, "The folder could not be remembered.", ToastLength.Long)?.Show();
            return;
        }

        StorageLocation.Save(this, uri.ToString() ?? "");
        UpdateLocationLabel();
        _status.Text = "Saving to " + StorageLocation.Describe(uri.ToString());
    }

    protected override void OnResume()
    {
        base.OnResume();
        ApplySession();
    }

    protected override void OnDestroy()
    {
        DownloadHub.Changed -= OnHubChanged;
        _work?.Cancel();
        _client.Dispose();
        base.OnDestroy();
    }

    void ChooseLocation()
    {
        if (_busy)
            return;

        if (StorageLocation.GetTreeUri(this) is null)
        {
            PickFolder();
            return;
        }

        new AlertDialog.Builder(this)?
            .SetTitle("Save location")?
            .SetItems(
                ["Choose another folder", "Default: Download/Bunkr"],
                (_, e) =>
                {
                    if (e.Which == 0)
                        PickFolder();
                    else
                        ResetLocation();
                })?
            .Show();
    }

    void PickFolder()
    {
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission
            | ActivityFlags.GrantWriteUriPermission
            | ActivityFlags.GrantPersistableUriPermission
            | ActivityFlags.GrantPrefixUriPermission);
        intent.PutExtra(
            "android.provider.extra.INITIAL_URI",
            Android.Net.Uri.Parse("content://com.android.externalstorage.documents/document/primary:Download"));
        try
        {
            StartActivityForResult(intent, PickFolderRequest);
        }
        catch (Exception)
        {
            Toast.MakeText(this, "The folder picker is not available.", ToastLength.Long)?.Show();
        }
    }

    void ResetLocation()
    {
        StorageLocation.Clear(this);
        UpdateLocationLabel();
        _status.Text = "Saving to " + StorageLocation.DefaultLabel;
    }

    void UpdateLocationLabel()
    {
        _location.Text = StorageLocation.Describe(StorageLocation.GetTreeUri(this));
    }

    bool TryGetWritableTree(out string? treeUri)
    {
        treeUri = StorageLocation.GetTreeUri(this);
        if (string.IsNullOrEmpty(treeUri))
            return true;
        if (StorageLocation.HasWriteAccess(this, treeUri))
            return true;

        StorageLocation.Clear(this);
        UpdateLocationLabel();
        Toast.MakeText(this, "Access to the folder is gone. Choose it again.", ToastLength.Long)?.Show();
        treeUri = null;
        return false;
    }

    async Task LoadAlbumAsync()
    {
        if (_busy)
            return;

        var input = _url.Text?.Trim() ?? "";
        _work = new CancellationTokenSource();
        var ct = _work.Token;
        SetBusy(true);
        _rows.Clear();
        BindRows();
        _title.Text = "";
        _status.Text = "Reading album…";
        _selectAll.Visibility = Android.Views.ViewStates.Gone;

        try
        {
            var album = await _client.OpenAsync(input, ct);
            _rows.AddRange(album.Files.Select(file => new FileRow { File = file }));
            _title.Text = album.Title;
            _status.Text = album.Files.Count == 1 ? "1 file" : album.Files.Count + " files";
            _suppressSelectAll = true;
            _selectAll.Checked = true;
            _suppressSelectAll = false;
            _selectAll.Visibility = Android.Views.ViewStates.Visible;
            BindRows();
            if (DownloadHub.IsRunning)
                ApplySession();
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            Toast.MakeText(this, ex.Message, ToastLength.Long)?.Show();
        }
        finally
        {
            SetBusy(false);
        }
    }

    void StartDownload()
    {
        AskForNotifications();
        var selected = _rows.Where(row => row.Selected).ToList();
        if (selected.Count == 0)
        {
            if (DownloadHub.HasPending && !DownloadHub.IsRunning)
            {
                LaunchQueue();
                return;
            }

            Toast.MakeText(this, "No file selected.", ToastLength.Short)?.Show();
            return;
        }

        if (!TryGetWritableTree(out var treeUri))
            return;

        var place = StorageLocation.Describe(treeUri);
        var album = string.IsNullOrWhiteSpace(_title.Text) ? "Bunkr Album" : _title.Text!;
        var items = new List<WorkItem>();
        foreach (var row in selected)
        {
            var index = _rows.IndexOf(row) + 1;
            row.Status = "Waiting";
            items.Add(new WorkItem
            {
                File = row.File,
                SaveName = $"{index:000}_{BunkrUrls.SanitizeFileName(row.File.Original)}",
                AlbumTitle = album,
                Place = place,
                TreeUri = treeUri,
            });
        }

        RefreshRowsForce();
        var alive = DownloadHub.Enqueue(this, items);
        if (!alive)
            LaunchQueue();
        ApplySession();
    }

    void LaunchQueue()
    {
        AskForNotifications();
        var intent = new Intent(this, typeof(global::BunkrAlbum.DownloadService));
        intent.SetAction(global::BunkrAlbum.DownloadService.ActionResume);
        StartForegroundService(intent);
    }

    void MaybeResume()
    {
        if (DownloadHub.HasPending && !DownloadHub.IsPaused && !DownloadHub.IsRunning)
            LaunchQueue();
    }

    void AskForNotifications()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions([Android.Manifest.Permission.PostNotifications], 51);
        }
    }

    void AcceptLink(Intent? intent)
    {
        var raw = intent?.Action == Intent.ActionSend
            ? intent.GetStringExtra(Intent.ExtraText) ?? intent.GetStringExtra(Intent.ExtraSubject)
            : intent?.DataString;
        var url = BunkrUrls.ExtractUrl(raw);
        if (url is null || _busy)
            return;
        _url.Text = url;
        _ = LoadAlbumAsync();
    }

    void OnHubChanged()
    {
        RunOnUiThread(() =>
        {
            if (!IsDestroyed)
                ApplySession();
        });
    }

    void ApplySession()
    {
        var session = DownloadHub.Session;
        if (session is null)
            return;

        if (_rows.Count == 0 && !_busy)
        {
            _title.Text = session.Title;
            _rows.AddRange(session.Items.Select(item => new FileRow
            {
                File = item.File,
                Status = item.Status,
                Selected = true,
                OpenUri = item.OpenUri,
                SaveName = item.SaveName,
                TreeUri = item.TreeUri,
            }));
            _suppressSelectAll = true;
            _selectAll.Checked = true;
            _suppressSelectAll = false;
            _selectAll.Visibility = Android.Views.ViewStates.Visible;
            BindRows();
        }

        var changed = false;
        foreach (var row in _rows)
        {
            var item = session.Items.FirstOrDefault(candidate => candidate.File.Id == row.File.Id);
            if (item is null)
                continue;
            row.SaveName = item.SaveName;
            row.TreeUri = item.TreeUri;
            if (!string.IsNullOrEmpty(item.OpenUri) && row.OpenUri != item.OpenUri)
            {
                row.OpenUri = item.OpenUri;
                changed = true;
            }

            if (row.Status == item.Status)
                continue;
            row.Status = item.Status;
            changed = true;
        }

        if (changed)
            RefreshRowsForce();
        _status.Text = session.Summary;
        UpdateDownloadButton();
    }

    void BindRows()
    {
        _visible.Clear();
        IEnumerable<FileRow> rows = _rows.Where(Matches);
        rows = _sort switch
        {
            SortMode.Size => rows.OrderByDescending(row => row.File.Size).ThenBy(row => row.File.Original, StringComparer.OrdinalIgnoreCase),
            SortMode.Type => rows.OrderBy(row => row.File.Extension, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.File.Original, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(row => row.File.Original, StringComparer.OrdinalIgnoreCase),
        };
        _visible.AddRange(rows);

        var show = _rows.Count > 0 ? Android.Views.ViewStates.Visible : Android.Views.ViewStates.Gone;
        _selectAll.Visibility = show;
        _filterRow.Visibility = show;
        _search.Visibility = show;
        _sortRow.Visibility = show;
        if (_adapter is null)
        {
            _adapter = new FileListAdapter(this, _visible, SyncSelectAll);
            _list.Adapter = _adapter;
        }
        else
        {
            _adapter.NotifyDataSetChanged();
        }

        PaintFilters();
        PaintSort();
        UpdateDownloadButton();
    }

    void SetSort(SortMode sort)
    {
        _sort = sort;
        BindRows();
    }

    void SetParallel(int value)
    {
        AppSettings.SetParallel(this, value);
        PaintParallel();
    }

    void RetryFailed()
    {
        AskForNotifications();
        var alive = DownloadHub.RequeueFailed(this);
        if (!alive)
            LaunchQueue();
        ApplySession();
    }

    void OpenRow(int position)
    {
        if (position < 0 || position >= _visible.Count)
            return;
        var row = _visible[position];
        if (row.Status is not ("Saved" or "Already saved"))
            return;
        var uriText = row.OpenUri;
        if (string.IsNullOrEmpty(uriText) && !string.IsNullOrEmpty(row.SaveName))
            uriText = DownloadRepository.FindUri(this, row.TreeUri, row.SaveName);
        if (string.IsNullOrEmpty(uriText))
        {
            Toast.MakeText(this, "Cannot open this file.", ToastLength.Short)?.Show();
            return;
        }

        var parsed = Android.Net.Uri.Parse(uriText.StartsWith('/') ? "file://" + uriText : uriText);
        if (parsed is null)
            return;
        var view = new Intent(Intent.ActionView);
        view.SetDataAndType(parsed, BunkrUrls.GuessMime(row.File));
        view.AddFlags(ActivityFlags.GrantReadUriPermission);
        try
        {
            StartActivity(view);
        }
        catch (Exception)
        {
            Toast.MakeText(this, "Cannot open this file.", ToastLength.Short)?.Show();
        }
    }

    void SetFilter(FileFilter filter)
    {
        _filter = filter;
        BindRows();
        SyncSelectAll();
    }

    bool Matches(FileRow row)
    {
        if (_filter == FileFilter.Images && !BunkrUrls.IsImage(row.File))
            return false;
        if (_filter == FileFilter.Videos && !BunkrUrls.IsVideo(row.File))
            return false;
        return _query.Length == 0 || row.File.Original.Contains(_query, StringComparison.OrdinalIgnoreCase);
    }

    void PaintFilters()
    {
        Paint(_filterAll, _filter == FileFilter.All);
        Paint(_filterImages, _filter == FileFilter.Images);
        Paint(_filterVideos, _filter == FileFilter.Videos);
    }

    void PaintSort()
    {
        Paint(_sortName, _sort == SortMode.Name);
        Paint(_sortSize, _sort == SortMode.Size);
        Paint(_sortType, _sort == SortMode.Type);
    }

    void PaintParallel()
    {
        var value = AppSettings.Parallel(this);
        Paint(_parallel1, value == 1);
        Paint(_parallel2, value == 2);
        Paint(_parallel3, value == 3);
        Paint(_parallel4, value == 4);
    }

    void Paint(Button button, bool active)
    {
        if (Resources is null)
            return;
        button.SetTextColor(Resources.GetColor(active ? Resource.Color.accent : Resource.Color.muted, Theme));
    }

    void SyncSelectAll()
    {
        _suppressSelectAll = true;
        _selectAll.Checked = _visible.Count > 0 && _visible.All(row => row.Selected);
        _suppressSelectAll = false;
        UpdateDownloadButton();
    }

    void UpdateDownloadButton()
    {
        var count = _rows.Count(row => row.Selected);
        var running = DownloadHub.IsRunning;
        var pending = DownloadHub.HasPending;
        if (running && count > 0)
            _download.Text = $"Add to queue ({count})";
        else if (!running && pending && count == 0)
            _download.Text = "Resume";
        else if (!running && pending)
            _download.Text = $"Resume ({count})";
        else
            _download.Text = count > 0 ? $"Download ({count})" : "Download";

        _download.Enabled = !_busy && (count > 0 || (pending && !running));
        _cancel.Enabled = _busy || running || pending;
        _pause.Enabled = running;
        _locationButton.Enabled = !_busy;
        var failed = DownloadHub.FailedCount;
        _retry.Visibility = failed > 0 ? Android.Views.ViewStates.Visible : Android.Views.ViewStates.Gone;
        _retry.Enabled = !_busy && failed > 0 && !running;
        _retry.Text = failed > 0 ? $"Retry failed ({failed})" : "Retry failed";
    }

    void RefreshRows()
    {
        var now = Environment.TickCount64;
        if (now - _lastDraw < 200)
            return;
        _lastDraw = now;
        _adapter?.NotifyDataSetChanged();
    }

    void RefreshRowsForce()
    {
        _lastDraw = Environment.TickCount64;
        _adapter?.NotifyDataSetChanged();
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        _load.Enabled = !busy;
        _url.Enabled = !busy;
        _selectAll.Enabled = !busy;
        UpdateDownloadButton();
    }

    T RequireView<T>(int id) where T : Android.Views.View
        => FindViewById<T>(id) ?? throw new InvalidOperationException("Missing view: " + id);
}

enum FileFilter
{
    All,
    Images,
    Videos,
}

enum SortMode
{
    Name,
    Size,
    Type,
}
