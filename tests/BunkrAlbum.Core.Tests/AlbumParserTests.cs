using Xunit;
using BunkrAlbum;

namespace BunkrAlbum.Core.Tests;

public class AlbumParserTests
{
    const string AlbumHtml = """
        <html><head>
        <meta property="og:title" content="Beispiel &amp; Album">
        </head><body>
        <script>
        window.albumFiles = [
        {
          id:  42 ,
          name: "photo-a.jpg",
          original: "foto: eins \"x\".jpg",
          slug: "photo-a.jpg",
          type: "image/jpeg",
          extension: "Image",
          size:  69038 ,
          timestamp: "05:27:48 11/03/2022",
          thumbnail: "https://cdn.example/a.png",
          cdnEndpoint: "/photo-a.jpg"
        },
        {
          id:  43 ,
          name: "clip.mp4",
          original: "clip.mp4",
          slug: "clip.mp4",
          type: "video/mp4",
          extension: "Video",
          size:  1048576 ,
          timestamp: "05:27:49 11/03/2022",
          thumbnail: "https://cdn.example/b.png",
          cdnEndpoint: "/clip.mp4"
        }
        ];
        </script>
        </body></html>
        """;

    [Fact]
    public void Parses_album_object_literal()
    {
        var album = AlbumParser.ParseAlbum("https://bunkr.cr/a/demo", AlbumHtml);

        Assert.Equal("Beispiel & Album", album.Title);
        Assert.Equal(2, album.Files.Count);
        Assert.Equal("42", album.Files[0].Id);
        Assert.Equal("foto: eins \"x\".jpg", album.Files[0].Original);
        Assert.Equal(69038, album.Files[0].Size);
        Assert.Equal("video/mp4", album.Files[1].Type);
        Assert.Equal("1 MB", BunkrUrls.FormatSize(album.Files[1].Size));
    }

    [Fact]
    public void Parses_single_file_page()
    {
        const string html = """
            <title>clip.mp4 | Bunkr</title>
            <a href="https://dl.bunkr.cr/file/2937108">Download</a>
            """;

        var album = AlbumParser.ParseSingleFile("https://bunkr.cr/f/clip.mp4", html);

        Assert.Equal("clip.mp4", album.Title);
        Assert.Equal("2937108", album.Files[0].Id);
    }

    [Fact]
    public void Builds_signed_and_legacy_urls()
    {
        var current = """
            {"mediafiles":"https://c5bu-b.cdn.cr","original":"a.jpg","path":"/storage/media/a.jpg"}
            """;
        Assert.Equal("https://c5bu-b.cdn.cr/storage/media/a.jpg", AlbumParser.ReadResolvedUrl(current));

        var signed = AlbumParser.ReadSignature("""{"ex":1792322266,"token":"abc"}""");
        Assert.Equal(
            "https://c5bu-b.cdn.cr/storage/media/a.jpg?token=abc&ex=1792322266",
            BunkrUrls.WithSignature("https://c5bu-b.cdn.cr/storage/media/a.jpg", signed.Token, signed.Expires));

        var legacy = """
            {"encrypted":true,"timestamp":1792314000,"url":"OzE3IjZucGQmPTEaXE9ZW0U/IGwhMTstKiI8cFlcU1FXGiMtLCYqejU7Ig=="}
            """;
        Assert.Equal("https://cdn.example/storage/media/photo.jpg", AlbumParser.ReadResolvedUrl(legacy));
    }

    [Theory]
    [InlineData("https://bunkr.cr/a/cpdYatJu", "a", "cpdYatJu")]
    [InlineData("bunkr.si/f/name.jpg", "f", "name.jpg")]
    [InlineData("https://bunkr.pk/v/clip", "v", "clip")]
    public void Parses_links(string input, string section, string id)
    {
        var link = BunkrUrls.Parse(input);
        Assert.Equal(section, link.Section);
        Assert.Equal(id, link.IdOrSlug);
    }

    [Fact]
    public void Rejects_other_hosts()
    {
        var error = Assert.Throws<BunkrException>(() => BunkrUrls.Parse("https://example.com/a/1"));
        Assert.Contains("kein Bunkr", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitizes_file_names()
    {
        Assert.Equal("a_b.jpg", BunkrUrls.SanitizeFileName("a/b.jpg"));
        Assert.Equal("datei", BunkrUrls.SanitizeFileName("   "));
    }

    [Fact]
    public void Parses_live_fixture_when_present()
    {
        var albumPath = Environment.GetEnvironmentVariable("BUNKR_ALBUM_HTML");
        if (string.IsNullOrEmpty(albumPath) || !File.Exists(albumPath))
            return;

        var album = AlbumParser.ParseAlbum("https://bunkr.cr/a/example", File.ReadAllText(albumPath));
        Assert.True(album.Files.Count >= 1);
        Assert.All(album.Files, file =>
        {
            Assert.False(string.IsNullOrWhiteSpace(file.Id));
            Assert.False(string.IsNullOrWhiteSpace(file.Original));
        });

        var filePath = Environment.GetEnvironmentVariable("BUNKR_FILE_HTML");
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return;

        var single = AlbumParser.ParseSingleFile("https://bunkr.cr/f/example", File.ReadAllText(filePath));
        Assert.Matches("^[0-9]+$", single.Files[0].Id);
    }

    [Fact]
    public async Task Resolves_live_album_when_requested()
    {
        if (Environment.GetEnvironmentVariable("BUNKR_LIVE") != "1")
            return;

        using var client = new BunkrClient();
        var album = await client.OpenAsync("https://bunkr.cr/a/cpdYatJu");
        Assert.NotEmpty(album.Files);

        var url = await client.ResolveDownloadUrlAsync(album.Files[0]);
        Assert.Contains("token=", url, StringComparison.Ordinal);

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 15);
        request.Headers.TryAddWithoutValidation("Referer", "https://bunkr.cr/");
        request.Headers.TryAddWithoutValidation("User-Agent", BunkrClient.UserAgent);
        using var response = await http.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(16, bytes.Length);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
    }

}
