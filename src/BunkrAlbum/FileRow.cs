namespace BunkrAlbum;

sealed class FileRow
{
    public required BunkrFile File { get; init; }
    public bool Selected { get; set; } = true;
    public string Status { get; set; } = "Bereit";
    public long LastUi { get; set; }
}
