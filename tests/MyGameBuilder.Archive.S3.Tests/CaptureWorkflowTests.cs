using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MyGameBuilder.Archive.S3.Tests;

public sealed class CaptureWorkflowTests
{
    [Fact]
    public async Task CaptureWorkflowCreatesFinalArchiveFromFakeS3()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mgb-archive-tests", Guid.NewGuid().ToString("N"));
        var output = Path.Combine(directory, "archive.sqlite");
        var work = Path.Combine(directory, "work");
        Directory.CreateDirectory(directory);

        await using var diagnostics = DiagnosticsWriter.Create(work);
        using var loggerFactory = LoggerFactory.Create(static _ => { });
        using var httpClient = new HttpClient(new FakeS3Handler());
        var s3Client = new S3ArchiveClient(httpClient, new Uri("https://example.test"), "bucket", loggerFactory.CreateLogger<S3ArchiveClient>());
        var workflow = new CaptureWorkflow(
            new ArchiveOptions("bucket", new Uri("https://example.test"), output, work, Concurrency: 2, Resume: false, Replace: false),
            s3Client,
            diagnostics,
            loggerFactory.CreateLogger<CaptureWorkflow>());

        await workflow.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(output));
        Assert.False(File.Exists(output + ".inprogress.sqlite"));
        var validation = await ArchiveValidator.ValidateAsync(output, TestContext.Current.CancellationToken);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
    }

    [Fact]
    public async Task CaptureWorkflowPreservesMissingContentTypeAsNull()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mgb-archive-tests", Guid.NewGuid().ToString("N"));
        var output = Path.Combine(directory, "archive.sqlite");
        var work = Path.Combine(directory, "work");
        Directory.CreateDirectory(directory);

        await using var diagnostics = DiagnosticsWriter.Create(work);
        using var loggerFactory = LoggerFactory.Create(static _ => { });
        using var httpClient = new HttpClient(new FakeS3Handler(omitContentType: true));
        var s3Client = new S3ArchiveClient(httpClient, new Uri("https://example.test"), "bucket", loggerFactory.CreateLogger<S3ArchiveClient>());
        var workflow = new CaptureWorkflow(
            new ArchiveOptions("bucket", new Uri("https://example.test"), output, work, Concurrency: 2, Resume: false, Replace: false),
            s3Client,
            diagnostics,
            loggerFactory.CreateLogger<CaptureWorkflow>());

        await workflow.RunAsync(TestContext.Current.CancellationToken);

        var validation = await ArchiveValidator.ValidateAsync(output, TestContext.Current.CancellationToken);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));

        using var connection = Sqlite.OpenReadOnly(output);
        var contentType = Sqlite.ExecuteScalar(connection, "SELECT content_type FROM s3_entry WHERE is_delete_marker = 0;");
        Assert.True(contentType is null or DBNull);
        Assert.Equal("data", Encoding.UTF8.GetString((byte[])Sqlite.ExecuteScalar(connection, "SELECT body FROM s3_entry WHERE is_delete_marker = 0;")!));
    }

    private sealed class FakeS3Handler(bool omitContentType = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri?.Query.Contains("versions", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(Xml("""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <ListVersionsResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
                      <Name>bucket</Name>
                      <Version>
                        <Key>alice/./tile/Brick</Key>
                        <VersionId>null</VersionId>
                        <IsLatest>true</IsLatest>
                        <LastModified>2011-09-15T22:58:53.000Z</LastModified>
                        <ETag>&quot;8d777f385d3dfec8815d20f7496026dc&quot;</ETag>
                        <Size>4</Size>
                        <StorageClass>STANDARD</StorageClass>
                      </Version>
                      <DeleteMarker>
                        <Key>alice/project1/tile/Old</Key>
                        <VersionId>deleted-version</VersionId>
                        <IsLatest>true</IsLatest>
                        <LastModified>2011-09-16T22:58:53.000Z</LastModified>
                      </DeleteMarker>
                      <IsTruncated>false</IsTruncated>
                    </ListVersionsResult>
                    """));
            }

            if (request.RequestUri?.Query.Contains("versionId=null", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }

            if (request.RequestUri?.Query.Contains("tagging", StringComparison.Ordinal) == true
                || request.RequestUri?.Query.Contains("acl", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }

            if (request.RequestUri?.OriginalString.Contains("/alice/%2E/tile/Brick", StringComparison.Ordinal) != true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("dot segment was normalized away")
                });
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("data"u8.ToArray())
            };
            if (!omitContentType)
            {
                response.Content.Headers.ContentType = new("image/png");
            }

            response.Content.Headers.LastModified = DateTimeOffset.Parse("2011-09-15T22:58:53.000Z");
            response.Headers.ETag = new("\"8d777f385d3dfec8815d20f7496026dc\"");
            response.Headers.TryAddWithoutValidation("x-amz-meta-width", "32");
            response.Headers.TryAddWithoutValidation("x-amz-meta-height", "32");
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Xml(string value)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(value, Encoding.UTF8, "application/xml")
            };
        }
    }
}
