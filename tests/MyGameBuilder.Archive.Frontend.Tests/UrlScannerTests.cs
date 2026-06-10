using System.Text;
using Xunit;

namespace MyGameBuilder.Archive.Frontend.Tests;

public sealed class UrlScannerTests
{
    [Fact]
    public void ScanFindsAbsoluteEscapedRelativeAndCommentedUrls()
    {
        var body = Encoding.Latin1.GetBytes("""
            /* http://comment.example.com/a.js */
            var escaped = "https:\/\/cdn.example.com/x.png";
            body { background: url(/css/site.css); }
            preload = "images/logo.png";
            """);

        var matches = UrlScanner.Scan(body, "http://mygamebuilder.com/root/index.html");
        var canonical = matches.Select(match => match.ResolvedCanonicalUrl ?? match.RawText).ToArray();

        Assert.Contains("http://comment.example.com/a.js", canonical);
        Assert.Contains("https://cdn.example.com/x.png", canonical);
        Assert.Contains("http://mygamebuilder.com/css/site.css", canonical);
        Assert.Contains("http://mygamebuilder.com/root/images/logo.png", canonical);
    }
}
