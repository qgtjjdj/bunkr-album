namespace BunkrAlbum;

sealed class FileRow
{
    public required BunkrFile File { get; init; }
    public bool Selected { get; set; } = true;
    public string Status { get; set; } = "Ready";
    public long LastUi { get; set; }
}
