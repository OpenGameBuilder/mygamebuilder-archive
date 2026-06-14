using Xunit;

namespace OpenGameBuilder.Mgb.Archive.ClientArchiver.Tests;

public sealed class FrontendUrlExcluderTests
{
    [Theory]
    [InlineData("https://v2.example.com/client.html")]
    [InlineData("//v2.example.com/client.html")]
    [InlineData("v2.example.com/client.html")]
    [InlineData("/ignored/client.html")]
    public void IsExcludedMatchesRedirectTargets(string redirectUrl)
    {
        var excludes = new[]
        {
            PrefixExclude("https://v2.example.com/"),
            PrefixExclude("https://example.com/ignored/")
        };

        var capture = new CdxCapture(
            "20000101000000",
            "http://example.com/start.html",
            "text/html",
            "302",
            null,
            null,
            redirectUrl);

        Assert.True(FrontendUrlExcluder.IsExcluded(capture, excludes));
    }

    [Fact]
    public void IsExcludedIgnoresMissingRedirectSentinels()
    {
        var excludes = new[] { PrefixExclude("https://v2.example.com/") };
        var capture = new CdxCapture(
            "20000101000000",
            "http://example.com/start.html",
            "text/html",
            "302",
            null,
            null,
            "-");

        Assert.False(FrontendUrlExcluder.IsExcluded(capture, excludes));
    }

    [Theory]
    [InlineData("https://web.test/web/20000101000000id_/https://v2.example.com/client.html")]
    [InlineData("/web/20000101000000id_/https://v2.example.com/client.html")]
    [InlineData("https://v2.example.com/client.html")]
    public void IsExcludedReplayRedirectMatchesWaybackLocations(string location)
    {
        var excludes = new[] { PrefixExclude("https://v2.example.com/") };
        var download = new ReplayDownload(
            new Uri("https://web.test/web/20000101000000id_/http://example.com/start.html"),
            302,
            "Found",
            [],
            new string('0', 64),
            null,
            new HeaderBag([new ReplayHeader("location", location)]));

        Assert.True(FrontendUrlExcluder.IsExcludedReplayRedirect("http://example.com/start.html", download, excludes));
    }

    [Fact]
    public void IsExcludedReplayRedirectMatchesAutoFollowedWaybackReplayUrl()
    {
        var excludes = new[] { PrefixExclude("https://v2.example.com/") };
        var download = new ReplayDownload(
            new Uri("https://web.test/web/20000101000000id_/https://v2.example.com/client.html"),
            302,
            "Found",
            [],
            new string('0', 64),
            null,
            new HeaderBag([]));

        Assert.True(FrontendUrlExcluder.IsExcludedReplayRedirect("http://example.com/start.html", download, excludes));
    }

    private static FrontendExclude PrefixExclude(string value)
    {
        var (seeds, excludes) = SeedFileParser.Parse(
            $"""
             url http://example.com/
             exclude {value}
             """);
        Assert.Single(seeds);
        return Assert.Single(excludes);
    }
}
