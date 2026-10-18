using BunkrAlbum;
using Xunit;

namespace BunkrAlbum.Core.Tests;

public class TransferTests
{
    [Theory]
    [InlineData(0, 200, true, 0, false, false)]
    [InlineData(100, 206, true, 100, false, false)]
    [InlineData(100, 416, true, 100, false, true)]
    [InlineData(100, 200, true, 0, true, false)]
    [InlineData(100, 200, false, 0, false, false)]
    public void Range_decision_matches_the_server_reply(
        long offset, int status, bool canSeek, long start, bool truncate, bool complete)
    {
        var decision = BunkrUrls.DecideRange(offset, status, canSeek);
        Assert.Equal(start, decision.StartAt);
        Assert.Equal(truncate, decision.Truncate);
        Assert.Equal(complete, decision.AlreadyComplete);
    }

    [Fact]
    public void Extract_url_finds_a_bunkr_link_inside_shared_text()
    {
        Assert.Equal(
            "https://bunkr.cr/a/cpdYatJu",
            BunkrUrls.ExtractUrl("schau mal https://bunkr.cr/a/cpdYatJu danke"));
        Assert.Null(BunkrUrls.ExtractUrl("https://example.com/a/nope"));
    }

    [Fact]
    public void Kind_follows_the_mime_type()
    {
        var image = new BunkrFile("1", "a", "a.jpg", "a", "image/jpeg", "Image", 1, "", "");
        var video = new BunkrFile("2", "b", "b.mp4", "b", "video/mp4", "Video", 1, "", "");
        Assert.True(BunkrUrls.IsImage(image));
        Assert.False(BunkrUrls.IsVideo(image));
        Assert.True(BunkrUrls.IsVideo(video));
    }
}
