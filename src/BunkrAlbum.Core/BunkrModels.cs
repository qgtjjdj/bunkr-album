namespace BunkrAlbum;

public enum BunkrLinkKind
{
    Album,
    File,
}

public readonly record struct BunkrLink(string Origin, string Section, string IdOrSlug)
{
    public BunkrLinkKind Kind => Section == "a" ? BunkrLinkKind.Album : BunkrLinkKind.File;
}

public sealed record BunkrFile(
    string Id,
    string Name,
    string Original,
    string Slug,
    string Type,
    string Extension,
    long Size,
    string Timestamp,
    string Thumbnail);

public sealed record AlbumInfo(string Title, string SourceUrl, IReadOnlyList<BunkrFile> Files);

public sealed class BunkrException : Exception
{
    public BunkrException(string message) : base(message)
    {
    }

    public BunkrException(string message, Exception inner) : base(message, inner)
    {
    }
}
