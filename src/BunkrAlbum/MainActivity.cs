using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
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
public class MainActivity : Activity
{
    const int PickFolderRequest = 41;

    readonly BunkrClient _client = new();
    readonly List<FileRow> _rows = [];
    EditText _url = null!;
    TextView _title = null!;
    TextView _status = null!;
    TextView _location = null!;
    CheckBox _selectAll = null!;
    ListView _list = null!;
    Button _load = null!;
    Button _cancel = null!;
    Button _download = null!;
    Button _locationButton = null!;
    FileListAdapter? _adapter;
    CancellationTokenSource? _work;
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
        _download = RequireView<Button>(Resource.Id.download_button);
        _locationButton = RequireView<Button>(Resource.Id.location_button);

        _load.Click += async (_, _) => await LoadAlbumAsync();
        _download.Click += (_, _) => StartDownload();
        _cancel.Click += (_, _) =>
        {
            _work?.Cancel();
            if (DownloadHub.IsRunning)
                DownloadHub.RequestCancel(this);
        };
        _locationButton.Click += (_, _) => ChooseLocation();
        _selectAll.CheckedChange += (_, e) =>
        {
            if (_suppressSelectAll)
                return;
            foreach (var row in _rows)
                row.Selected = e.IsChecked;
            _adapter?.NotifyDataSetChanged();
            UpdateDownloadButton();
        };
        UpdateLocationLabel();
        DownloadHub.Changed += OnHubChanged;
        ApplySession();
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
            Toast.MakeText(this, "Der Ordner konnte nicht gemerkt werden.", ToastLength.Long)?.Show();
            return;
        }

        StorageLocation.Save(this, uri.ToString() ?? "");
        UpdateLocationLabel();
        _status.Text = "Speichert nach " + StorageLocation.Describe(uri.ToString());
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
        if (_busy || DownloadHub.IsRunning)
            return;

        if (StorageLocation.GetTreeUri(this) is null)
        {
            PickFolder();
            return;
        }

        new AlertDialog.Builder(this)?
            .SetTitle("Speicherort")?
            .SetItems(
                ["Anderen Ordner wählen", "Standard: Download/Bunkr"],
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
            Toast.MakeText(this, "Die Ordnerauswahl ist nicht verfügbar.", ToastLength.Long)?.Show();
        }
    }

    void ResetLocation()
    {
        StorageLocation.Clear(this);
        UpdateLocationLabel();
        _status.Text = "Speichert nach " + StorageLocation.DefaultLabel;
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
        Toast.MakeText(this, "Zugriff auf den Ordner ist weg. Bitte neu wählen.", ToastLength.Long)?.Show();
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
        _status.Text = "Album wird gelesen …";
        _selectAll.Visibility = Android.Views.ViewStates.Gone;

        try
        {
            var album = await _client.OpenAsync(input, ct);
            _rows.AddRange(album.Files.Select(file => new FileRow { File = file }));
            _title.Text = album.Title;
            _status.Text = album.Files.Count + " Dateien";
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
            _status.Text = "Abgebrochen.";
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
        if (DownloadHub.IsRunning)
        {
            Toast.MakeText(this, "Ein Download läuft schon.", ToastLength.Short)?.Show();
            return;
        }

        var selected = _rows.Where(row => row.Selected).ToList();
        if (selected.Count == 0)
        {
            Toast.MakeText(this, "Keine Datei ausgewählt.", ToastLength.Short)?.Show();
            return;
        }

        if (!TryGetWritableTree(out var treeUri))
            return;

        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions([Android.Manifest.Permission.PostNotifications], 51);
        }

        var items = selected.Select(row =>
        {
            var index = _rows.IndexOf(row) + 1;
            row.Status = "Wartet";
            return new WorkItem
            {
                File = row.File,
                SaveName = $"{index:000}_{BunkrUrls.SanitizeFileName(row.File.Original)}",
            };
        }).ToList();

        var session = new DownloadSession
        {
            AlbumTitle = string.IsNullOrWhiteSpace(_title.Text) ? "Bunkr Album" : _title.Text!,
            Place = StorageLocation.Describe(treeUri),
            TreeUri = treeUri,
            Items = items,
            Summary = $"0/{items.Count} startet …",
        };
        if (!DownloadHub.TryBegin(session))
            return;

        RefreshRowsForce();
        var intent = new Intent(this, typeof(global::BunkrAlbum.DownloadService));
        intent.SetAction(global::BunkrAlbum.DownloadService.ActionStart);
        StartForegroundService(intent);
        ApplySession();
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
            _title.Text = session.AlbumTitle;
            _rows.AddRange(session.Items.Select(item => new FileRow
            {
                File = item.File,
                Status = item.Status,
                Selected = true,
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
            if (item is null || row.Status == item.Status)
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
        _adapter = new FileListAdapter(this, _rows, SyncSelectAll);
        _list.Adapter = _adapter;
        UpdateDownloadButton();
    }

    void SyncSelectAll()
    {
        _suppressSelectAll = true;
        _selectAll.Checked = _rows.Count > 0 && _rows.All(row => row.Selected);
        _suppressSelectAll = false;
        UpdateDownloadButton();
    }

    void UpdateDownloadButton()
    {
        var count = _rows.Count(row => row.Selected);
        var downloading = DownloadHub.IsRunning;
        _download.Enabled = !_busy && !downloading && count > 0;
        _download.Text = count > 0 ? $"Herunterladen ({count})" : "Herunterladen";
        _cancel.Enabled = _busy || downloading;
        _locationButton.Enabled = !_busy && !downloading;
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
        => FindViewById<T>(id) ?? throw new InvalidOperationException("View fehlt: " + id);
}
