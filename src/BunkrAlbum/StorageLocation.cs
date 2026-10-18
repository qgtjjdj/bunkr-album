using Android.Content;
using Android.Provider;

namespace BunkrAlbum;

static class StorageLocation
{
    const string PrefsName = "bunkr";
    const string TreeKey = "tree_uri";
    public const string DefaultLabel = "Download/Bunkr";

    public static string? GetTreeUri(Context context)
        => context.GetSharedPreferences(PrefsName, FileCreationMode.Private)?.GetString(TreeKey, null);

    public static void Save(Context context, string uri)
    {
        context.GetSharedPreferences(PrefsName, FileCreationMode.Private)?
            .Edit()?
            .PutString(TreeKey, uri)?
            .Apply();
    }

    public static void Clear(Context context)
    {
        var current = GetTreeUri(context);
        if (!string.IsNullOrEmpty(current))
        {
            try
            {
                var uri = Android.Net.Uri.Parse(current);
                if (uri is not null)
                {
                    context.ContentResolver?.ReleasePersistableUriPermission(
                        uri,
                        ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
                }
            }
            catch (Exception)
            {
                // Permission was already gone.
            }
        }

        context.GetSharedPreferences(PrefsName, FileCreationMode.Private)?
            .Edit()?
            .Remove(TreeKey)?
            .Apply();
    }

    public static bool HasWriteAccess(Context context, string treeUri)
    {
        var stored = Android.Net.Uri.Parse(treeUri);
        var permissions = context.ContentResolver?.PersistedUriPermissions;
        if (stored is null || permissions is null)
            return false;

        foreach (var permission in permissions)
        {
            if (permission.IsWritePermission && stored.Equals(permission.Uri))
                return true;
        }

        return false;
    }

    public static string Describe(string? treeUri)
    {
        if (string.IsNullOrWhiteSpace(treeUri))
            return DefaultLabel;

        try
        {
            var uri = Android.Net.Uri.Parse(treeUri);
            var docId = uri is null ? null : DocumentsContract.GetTreeDocumentId(uri);
            if (string.IsNullOrEmpty(docId))
                return "Selected folder";

            var colon = docId.IndexOf(':');
            var path = colon >= 0 ? docId[(colon + 1)..] : docId;
            path = Android.Net.Uri.Decode(path) ?? path;
            return string.IsNullOrWhiteSpace(path) ? "Selected folder" : path;
        }
        catch (Exception)
        {
            return "Selected folder";
        }
    }
}
