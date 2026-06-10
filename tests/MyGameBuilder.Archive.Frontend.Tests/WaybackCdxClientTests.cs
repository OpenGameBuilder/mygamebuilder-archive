using System.Net;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MyGameBuilder.Archive.Frontend.Tests;

public sealed class WaybackCdxClientTests
{
    [Fact]
    public async Task GetCdxPageReadsResumeKey()
    {
        using var loggerFactory = LoggerFactory.Create(static _ => { });
        using var httpClient = new HttpClient(new FakeCdxHandler());
        var client = new WaybackCdxClient(
            httpClient,
            new Uri("https://web.test/cdx/search/cdx"),
            new Uri("https://web.test/web"),
            loggerFactory.CreateLogger<WaybackCdxClient>());

        var first = await client.GetCdxPageAsync(
            new FrontendSeed(1, FrontendSeedKind.Prefix, "https://example.com/app/", "prefix https://example.com/app/"),
            resumeKey: null,
            TestContext.Current.CancellationToken);
        var second = await client.GetCdxPageAsync(
            new FrontendSeed(1, FrontendSeedKind.Prefix, "https://example.com/app/", "prefix https://example.com/app/"),
            first.ResumeKey,
            TestContext.Current.CancellationToken);

        Assert.Equal("next-page", first.ResumeKey);
        Assert.Null(second.ResumeKey);
        Assert.Single(first.Captures);
        Assert.Single(second.Captures);
        Assert.Equal("http://example.com/app/one.js", first.Captures[0].OriginalUrl);
        Assert.Equal("http://example.com/app/two.js", second.Captures[0].OriginalUrl);
    }

    private sealed class FakeCdxHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.RequestUri?.Query.Contains("resumeKey=next-page", StringComparison.Ordinal) == true
                ? """
                  [["timestamp","original","mimetype","statuscode","digest","length","redirect"],
                  ["20000201000000","http://example.com/app/two.js","application/javascript","200","DIGEST2","20",null]]
                  """
                : """
                  [["timestamp","original","mimetype","statuscode","digest","length","redirect"],
                  ["20000101000000","http://example.com/app/one.js","application/javascript","200","DIGEST1","10",null],
                  [],
                  ["next-page"]]
                  """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }
}
