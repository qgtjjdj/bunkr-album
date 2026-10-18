using Android.Content;
using Android.Provider;

namespace BunkrAlbum;

static class DownloadRepository
{
    public static long? ExistingSize(Context context, string? treeUri, string displayName)
    {
        try
        {
            if (!string.IsNullOrEmpty(treeUri))
                return TreeSize(context, treeUri, displayName);
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
                return MediaStoreSize(context, displayName);
            return AppFolderSize(context, displayName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    static long? TreeSize(Context context, string treeUri, string displayName)
    {
        var resolver = context.ContentResolver;
        var tree = Android.Net.Uri.Parse(treeUri);
        if (resolver is null || tree is null)
            return null;
        var docId = DocumentsContract.GetTreeDocumentId(tree);
        if (string.IsNullOrEmpty(docId))
            return null;
        var children = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, docId);
        if (children is null)
            return null;

        using var cursor = resolver.Query(
            children,
            [DocumentsContract.Document.ColumnDisplayName, DocumentsContract.Document.ColumnSize],
            null,
            null,
            null);
        if (cursor is null)
            return null;

        var nameIndex = cursor.GetColumnIndex(DocumentsContract.Document.ColumnDisplayName);
        var sizeIndex = cursor.GetColumnIndex(DocumentsContract.Document.ColumnSize);
        long? found = null;
        while (cursor.MoveToNext())
        {
            if (nameIndex < 0 || cursor.GetString(nameIndex) != displayName)
                continue;
            var size = sizeIndex < 0 || cursor.IsNull(sizeIndex) ? 0 : cursor.GetLong(sizeIndex);
            if (found is null || size > found)
                found = size;
        }

        return found;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android29.0")]
    static long? MediaStoreSize(Context context, string displayName)
    {
        var resolver = context.ContentResolver;
        if (resolver is null)
            return null;

        foreach (var relative in new[] { "Download/Bunkr/", "Download/Bunkr" })
        {
            using var cursor = resolver.Query(
                MediaStore.Downloads.ExternalContentUri,
                [MediaStore.IMediaColumns.Size],
                MediaStore.IMediaColumns.DisplayName + "=? AND " + MediaStore.IMediaColumns.RelativePath + "=?",
                [displayName, relative],
                null);
            if (cursor is null || !cursor.MoveToFirst() || cursor.IsNull(0))
                continue;
            return cursor.GetLong(0);
        }

        return null;
    }

    static long? AppFolderSize(Context context, string displayName)
    {
        var root = context.GetExternalFilesDir(Android.OS.Environment.DirectoryDownloads) ?? context.FilesDir;
        if (root is null)
            return null;
        var target = new Java.IO.File(new Java.IO.File(root, "Bunkr"), displayName);
        return target.IsFile ? target.Length() : null;
    }

    public static string? FindUri(Context context, string? treeUri, string displayName)
    {
        try
        {
            if (!string.IsNullOrEmpty(treeUri))
                return FindTreeUri(context, treeUri, displayName);
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
                return FindMediaStoreUri(context, displayName);
            return FindAppFolderUri(context, displayName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    static string? FindTreeUri(Context context, string treeUri, string displayName)
    {
        var resolver = context.ContentResolver;
        var tree = Android.Net.Uri.Parse(treeUri);
        if (resolver is null || tree is null)
            return null;
        var rootId = DocumentsContract.GetTreeDocumentId(tree);
        if (string.IsNullOrEmpty(rootId))
            return null;
        var children = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, rootId);
        if (children is null)
            return null;
        using var cursor = resolver.Query(
            children,
            [DocumentsContract.Document.ColumnDisplayName, DocumentsContract.Document.ColumnDocumentId],
            null,
            null,
            null);
        if (cursor is null)
            return null;
        var nameIndex = cursor.GetColumnIndex(DocumentsContract.Document.ColumnDisplayName);
        var idIndex = cursor.GetColumnIndex(DocumentsContract.Document.ColumnDocumentId);
        while (cursor.MoveToNext())
        {
            if (nameIndex < 0 || idIndex < 0 || cursor.GetString(nameIndex) != displayName)
                continue;
            var id = cursor.GetString(idIndex);
            if (!string.IsNullOrEmpty(id))
                return DocumentsContract.BuildDocumentUriUsingTree(tree, id)?.ToString();
        }

        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android29.0")]
    static string? FindMediaStoreUri(Context context, string displayName)
    {
        var resolver = context.ContentResolver;
        if (resolver is null)
            return null;
        foreach (var relative in new[] { "Download/Bunkr/", "Download/Bunkr" })
        {
            using var cursor = resolver.Query(
                MediaStore.Downloads.ExternalContentUri,
                ["_id"],
                MediaStore.IMediaColumns.DisplayName + "=? AND " + MediaStore.IMediaColumns.RelativePath + "=?",
                [displayName, relative],
                null);
            if (cursor is null || !cursor.MoveToFirst() || cursor.IsNull(0))
                continue;
            return ContentUris.WithAppendedId(MediaStore.Downloads.ExternalContentUri, cursor.GetLong(0))?.ToString();
        }

        return null;
    }

    static string? FindAppFolderUri(Context context, string displayName)
    {
        var root = context.GetExternalFilesDir(Android.OS.Environment.DirectoryDownloads) ?? context.FilesDir;
        if (root is null)
            return null;
        var target = new Java.IO.File(new Java.IO.File(root, "Bunkr"), displayName);
        return target.IsFile ? target.AbsolutePath : null;
    }

    public static async Task<string> SaveAsync(
        Context context,
        string? treeUri,
        string displayName,
        string mime,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(treeUri))
            return await SaveTreeAsync(context, treeUri, displayName, mime, write, cancellationToken);
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            return await SaveMediaStoreAsync(context, displayName, mime, write, cancellationToken);
        return await SaveAppFolderAsync(context, displayName, write, cancellationToken);
    }

    static async Task<string> SaveTreeAsync(
        Context context,
        string treeUriString,
        string displayName,
        string mime,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        var resolver = context.ContentResolver ?? throw new BunkrException("No access to storage.");
        var tree = Android.Net.Uri.Parse(treeUriString) ?? throw new BunkrException("The folder is invalid.");
        var docId = DocumentsContract.GetTreeDocumentId(tree)
            ?? throw new BunkrException("The folder is invalid. Choose it again.");
        var parent = DocumentsContract.BuildDocumentUriUsingTree(tree, docId)
            ?? throw new BunkrException("The folder is invalid. Choose it again.");
        var type = string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime;
        var fileUri = DocumentsContract.CreateDocument(resolver, parent, type, displayName)
            ?? throw new BunkrException("A file cannot be created in this folder.");

        try
        {
            await using var stream = resolver.OpenOutputStream(fileUri)
                ?? throw new BunkrException("The file could not be created.");
            await write(stream, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return fileUri.ToString() ?? displayName;
        }
        catch
        {
            try
            {
                DocumentsContract.DeleteDocument(resolver, fileUri);
            }
            catch (Exception)
            {
                // The provider may already have removed the partial file.
            }

            throw;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android29.0")]
    static async Task<string> SaveMediaStoreAsync(
        Context context,
        string displayName,
        string mime,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        var resolver = context.ContentResolver ?? throw new BunkrException("No access to storage.");
        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, displayName);
        values.Put(MediaStore.IMediaColumns.MimeType, mime);
        values.Put(MediaStore.IMediaColumns.RelativePath, Android.OS.Environment.DirectoryDownloads + "/Bunkr");
        values.Put(MediaStore.IMediaColumns.IsPending, 1);

        var uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri, values)
            ?? throw new BunkrException("The Download folder is not available.");
        try
        {
            await using var stream = resolver.OpenOutputStream(uri)
                ?? throw new BunkrException("The file could not be created.");
            await write(stream, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            var done = new ContentValues();
            done.Put(MediaStore.IMediaColumns.IsPending, 0);
            resolver.Update(uri, done, null, null);
            return uri.ToString() ?? displayName;
        }
        catch
        {
            resolver.Delete(uri, null, null);
            throw;
        }
    }

    static async Task<string> SaveAppFolderAsync(
        Context context,
        string displayName,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        var root = context.GetExternalFilesDir(Android.OS.Environment.DirectoryDownloads) ?? context.FilesDir
            ?? throw new BunkrException("No app storage is available.");
        var folder = new Java.IO.File(root, "Bunkr");
        if (!folder.Exists() && !folder.Mkdirs())
            throw new BunkrException("The folder could not be created.");

        var target = new Java.IO.File(folder, displayName);
        await using var stream = System.IO.File.Create(target.AbsolutePath!);
        await write(stream, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return target.AbsolutePath ?? displayName;
    }
}
