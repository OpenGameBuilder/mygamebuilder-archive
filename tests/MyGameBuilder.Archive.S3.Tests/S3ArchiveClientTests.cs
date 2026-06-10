using Microsoft.Extensions.Logging;
using Xunit;

namespace MyGameBuilder.Archive.S3.Tests;

public sealed class S3ArchiveClientTests
{
    [Theory]
    [InlineData("0flobe/./tile/russian", "https://s3.amazonaws.com/JGI_test1/0flobe/%2E/tile/russian")]
    [InlineData("a/../b", "https://s3.amazonaws.com/JGI_test1/a/%2E%2E/b")]
    [InlineData("./leading-dot", "https://s3.amazonaws.com/JGI_test1/%2E/leading-dot")]
    [InlineData("../leading-dotdot", "https://s3.amazonaws.com/JGI_test1/%2E%2E/leading-dotdot")]
    [InlineData("trailing-dot/.", "https://s3.amazonaws.com/JGI_test1/trailing-dot/%2E")]
    [InlineData("trailing-dotdot/..", "https://s3.amazonaws.com/JGI_test1/trailing-dotdot/%2E%2E")]
    [InlineData("double//slash", "https://s3.amazonaws.com/JGI_test1/double//slash")]
    [InlineData("/leading/slash", "https://s3.amazonaws.com/JGI_test1//leading/slash")]
    [InlineData("space plus+percent%question?hash#unicode-å", "https://s3.amazonaws.com/JGI_test1/space%20plus%2Bpercent%25question%3Fhash%23unicode-%C3%A5")]
    [InlineData("Case/Sensitive/Key", "https://s3.amazonaws.com/JGI_test1/Case/Sensitive/Key")]
    public void BuildObjectUriPreservesS3KeyPathSemantics(string key, string expectedUri)
    {
        using var loggerFactory = LoggerFactory.Create(static _ => { });
        using var httpClient = new HttpClient(new NeverCalledHandler());
        var client = new S3ArchiveClient(httpClient, new Uri("https://s3.amazonaws.com"), "JGI_test1", loggerFactory.CreateLogger<S3ArchiveClient>());
        var entry = new ListedS3Entry(
            0,
            key,
            RawVersionId: "null",
            IsLatest: true,
            IsDeleteMarker: false,
            DateTimeOffset.UnixEpoch,
            ETag: "etag",
            ContentLengthBytes: 0,
            StorageClass: "STANDARD",
            SourceListXml: "<Version />");

        var uri = client.BuildObjectUri(entry);

        Assert.Equal(expectedUri, uri.OriginalString);
        Assert.Equal(expectedUri, uri.AbsoluteUri);
    }

    [Fact]
    public void BuildObjectUriAddsRealVersionIdWithoutAddingNullVersionIdForLatestNullVersion()
    {
        using var loggerFactory = LoggerFactory.Create(static _ => { });
        using var httpClient = new HttpClient(new NeverCalledHandler());
        var client = new S3ArchiveClient(httpClient, new Uri("https://s3.amazonaws.com"), "JGI_test1", loggerFactory.CreateLogger<S3ArchiveClient>());
        var versioned = NewEntry("key", "real/version+id", isLatest: false);
        var latestNull = NewEntry("key", "null", isLatest: true);

        Assert.Equal("https://s3.amazonaws.com/JGI_test1/key?versionId=real%2Fversion%2Bid", client.BuildObjectUri(versioned).OriginalString);
        Assert.Equal("https://s3.amazonaws.com/JGI_test1/key", client.BuildObjectUri(latestNull).OriginalString);
    }

    private static ListedS3Entry NewEntry(string key, string rawVersionId, bool isLatest) =>
        new(
            0,
            key,
            rawVersionId,
            isLatest,
            IsDeleteMarker: false,
            DateTimeOffset.UnixEpoch,
            ETag: "etag",
            ContentLengthBytes: 0,
            StorageClass: "STANDARD",
            SourceListXml: "<Version />");

    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This test should not send HTTP requests.");
    }
}
