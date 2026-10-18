using Android.Content;
using Android.Provider;

namespace BunkrAlbum;

static class DownloadRepository
{
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
        var resolver = context.ContentResolver ?? throw new BunkrException("Kein Zugriff auf den Speicher.");
        var tree = Android.Net.Uri.Parse(treeUriString) ?? throw new BunkrException("Der Ordner ist ungültig.");
        var docId = DocumentsContract.GetTreeDocumentId(tree)
            ?? throw new BunkrException("Der Ordner ist ungültig. Bitte neu wählen.");
        var parent = DocumentsContract.BuildDocumentUriUsingTree(tree, docId)
            ?? throw new BunkrException("Der Ordner ist ungültig. Bitte neu wählen.");
        var type = string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime;
        var fileUri = DocumentsContract.CreateDocument(resolver, parent, type, displayName)
            ?? throw new BunkrException("In diesem Ordner kann keine Datei angelegt werden.");

        try
        {
            await using var stream = resolver.OpenOutputStream(fileUri)
                ?? throw new BunkrException("Datei konnte nicht angelegt werden.");
            await write(stream, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return StorageLocation.Describe(treeUriString) + "/" + displayName;
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
        var resolver = context.ContentResolver ?? throw new BunkrException("Kein Zugriff auf den Speicher.");
        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, displayName);
        values.Put(MediaStore.IMediaColumns.MimeType, mime);
        values.Put(MediaStore.IMediaColumns.RelativePath, Android.OS.Environment.DirectoryDownloads + "/Bunkr");
        values.Put(MediaStore.IMediaColumns.IsPending, 1);

        var uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri, values)
            ?? throw new BunkrException("Download-Ordner ist nicht verfügbar.");
        try
        {
            await using var stream = resolver.OpenOutputStream(uri)
                ?? throw new BunkrException("Datei konnte nicht angelegt werden.");
            await write(stream, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            var done = new ContentValues();
            done.Put(MediaStore.IMediaColumns.IsPending, 0);
            resolver.Update(uri, done, null, null);
            return StorageLocation.DefaultLabel + "/" + displayName;
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
            ?? throw new BunkrException("Kein App-Speicher verfügbar.");
        var folder = new Java.IO.File(root, "Bunkr");
        if (!folder.Exists() && !folder.Mkdirs())
            throw new BunkrException("Ordner konnte nicht angelegt werden.");

        var target = new Java.IO.File(folder, displayName);
        await using var stream = System.IO.File.Create(target.AbsolutePath!);
        await write(stream, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return target.AbsolutePath ?? displayName;
    }
}
