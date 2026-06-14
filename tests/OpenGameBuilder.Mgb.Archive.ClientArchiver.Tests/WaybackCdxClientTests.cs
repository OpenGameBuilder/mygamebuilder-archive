using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Xunit;

namespace OpenGameBuilder.Mgb.Archive.ClientArchiver.Tests;

public sealed class WaybackCdxClientTests
{
    [Fact]
    public async Task GetCdxPageReadsResumeKey()
    {
        using var loggerFactory = LoggerFactory.Create(static _ => { });
        var handler = new FakeCdxHandler();
        using var httpClient = new HttpClient(handler);
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
        Assert.Contains("OpenGameBuilder.Mgb.Archive.ClientArchiver", handler.LastUserAgent, StringComparison.Ordinal);
        Assert.Equal("http://example.com/app/one.js", first.Captures[0].OriginalUrl);
        Assert.Equal("http://example.com/app/two.js", second.Captures[0].OriginalUrl);
    }

    [Fact]
    public async Task GetCdxPageTreatsGatewayTimeoutAsTransient()
    {
        using var loggerFactory = LoggerFactory.Create(static _ => { });
        using var httpClient = new HttpClient(new CdxGatewayTimeoutHandler());
        var client = new WaybackCdxClient(
            httpClient,
            new Uri("https://web.test/cdx/search/cdx"),
            new Uri("https://web.test/web"),
            loggerFactory.CreateLogger<WaybackCdxClient>());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetCdxPageAsync(
            new FrontendSeed(15, FrontendSeedKind.Url, "http://s3.amazonaws.com/apphost/sounds/Bounce3.mp3", "url http://s3.amazonaws.com/apphost/sounds/Bounce3.mp3"),
            resumeKey: null,
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.GatewayTimeout, ex.StatusCode);
        Assert.True(WaybackCdxClient.IsTransient(ex));
        Assert.Contains("CDX query failed with HTTP 504 Gateway Time-out", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadReplayPreservesPartialBodyWhenReadFails()
    {
        using var loggerFactory = LoggerFactory.Create(static _ => { });
        using var httpClient = new HttpClient(new PartialBodyHandler());
        var client = new WaybackCdxClient(
            httpClient,
            new Uri("https://web.test/cdx/search/cdx"),
            new Uri("https://web.test/web"),
            loggerFactory.CreateLogger<WaybackCdxClient>());

        var download = await client.DownloadReplayAsync(
            new PendingReplayCapture(1, "20000101000000", "http://example.com/robots.txt"),
            TestContext.Current.CancellationToken);

        Assert.Equal(404, download.StatusCode);
        Assert.Equal("partial body", Encoding.UTF8.GetString(download.Body));
        Assert.Contains("simulated truncated response", download.BodyReadError, StringComparison.Ordinal);
    }

    private sealed class FakeCdxHandler : HttpMessageHandler
    {
        public string LastUserAgent { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastUserAgent = request.Headers.UserAgent.ToString();
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

    private sealed class CdxGatewayTimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
            {
                ReasonPhrase = "Gateway Time-out",
                Content = new StringContent(
                    "<html><head><title>504 Gateway Time-out</title></head><body>nginx</body></html>",
                    Encoding.UTF8,
                    "text/html")
            });
        }
    }

    private sealed class PartialBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StreamContent(new ThrowingReadStream("partial body"u8.ToArray()))
            };
            response.Content.Headers.ContentType = new("text/plain");
            response.Content.Headers.ContentLength = "partial body".Length + 1;
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingReadStream(byte[] bytes) : Stream
    {
        private bool _readOnce;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length + 1;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_readOnce)
            {
                throw new IOException("simulated truncated response");
            }

            _readOnce = true;
            Array.Copy(bytes, 0, buffer, offset, bytes.Length);
            Position += bytes.Length;
            return bytes.Length;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_readOnce)
            {
                throw new IOException("simulated truncated response");
            }

            _readOnce = true;
            bytes.CopyTo(buffer);
            Position += bytes.Length;
            return ValueTask.FromResult(bytes.Length);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
