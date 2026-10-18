using Android.App;
using Android.Views;
using Android.Widget;

namespace BunkrAlbum;

sealed class FileListAdapter : BaseAdapter<FileRow>
{
    readonly Activity _activity;
    readonly IReadOnlyList<FileRow> _rows;
    readonly CheckListener _checks;

    public FileListAdapter(Activity activity, IReadOnlyList<FileRow> rows, Action onSelectionChanged)
    {
        _activity = activity;
        _rows = rows;
        _checks = new CheckListener(rows, onSelectionChanged);
    }

    public override int Count => _rows.Count;

    public override FileRow this[int position] => _rows[position];

    public override long GetItemId(int position) => position;

    public override View GetView(int position, View? convertView, ViewGroup? parent)
    {
        var view = convertView ?? _activity.LayoutInflater.Inflate(Resource.Layout.item_file, parent, false)!;
        var row = _rows[position];
        var name = view.FindViewById<TextView>(Resource.Id.file_name)!;
        var meta = view.FindViewById<TextView>(Resource.Id.file_meta)!;
        var check = view.FindViewById<CheckBox>(Resource.Id.file_check)!;

        name.Text = row.File.Original;
        var kind = string.IsNullOrWhiteSpace(row.File.Extension) ? row.File.Type : row.File.Extension;
        var size = BunkrUrls.FormatSize(row.File.Size);
        meta.Text = string.IsNullOrWhiteSpace(row.Status) ? $"{kind} · {size}" : $"{kind} · {size} · {row.Status}";

        check.SetOnCheckedChangeListener(null);
        check.Checked = row.Selected;
        check.Tag = new Java.Lang.Integer(position);
        check.SetOnCheckedChangeListener(_checks);
        return view;
    }

    sealed class CheckListener : Java.Lang.Object, CompoundButton.IOnCheckedChangeListener
    {
        readonly IReadOnlyList<FileRow> _rows;
        readonly Action _changed;

        public CheckListener(IReadOnlyList<FileRow> rows, Action changed)
        {
            _rows = rows;
            _changed = changed;
        }

        public void OnCheckedChanged(CompoundButton? buttonView, bool isChecked)
        {
            if (buttonView?.Tag is not Java.Lang.Integer index)
                return;
            var position = index.IntValue();
            if (position < 0 || position >= _rows.Count)
                return;
            if (_rows[position].Selected == isChecked)
                return;
            _rows[position].Selected = isChecked;
            _changed();
        }
    }
}
