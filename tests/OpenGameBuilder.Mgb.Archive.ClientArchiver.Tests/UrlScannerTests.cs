using System.Text;
using Xunit;

namespace OpenGameBuilder.Mgb.Archive.ClientArchiver.Tests;

public sealed class UrlScannerTests
{
    [Fact]
    public void ScanFindsAbsoluteRelativeAndAppHostAssetUrls()
    {
        var body = Encoding.Latin1.GetBytes(
            """
            <script src="/scripts/site.js"></script>
            body { background: url(images/bg.png); }
            http://cdn.example.com/app.js
            game_music/McLeod9/MindGear.mp3
            """);

        var urls = UrlScanner.Scan(body, "https://s3.amazonaws.com/apphost/MGB.swf");

        Assert.Contains("http://cdn.example.com/app.js", urls);
        Assert.Contains("https://s3.amazonaws.com/scripts/site.js", urls);
        Assert.Contains("https://s3.amazonaws.com/apphost/images/bg.png", urls);
        Assert.Contains("https://s3.amazonaws.com/apphost/game_music/McLeod9/MindGear.mp3", urls);
    }
}
