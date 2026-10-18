namespace BunkrAlbum;

sealed class FileRow
{
    public required BunkrFile File { get; init; }
    public bool Selected { get; set; } = true;
    public string Status { get; set; } = "Ready";
    public string? OpenUri { get; set; }
    public string? SaveName { get; set; }
    public string? TreeUri { get; set; }
    public long LastUi { get; set; }
}
