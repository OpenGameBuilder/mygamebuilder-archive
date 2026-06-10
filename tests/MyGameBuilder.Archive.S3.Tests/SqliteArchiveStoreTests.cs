using Xunit;

namespace MyGameBuilder.Archive.S3.Tests;

public sealed class SqliteArchiveStoreTests
{
    [Fact]
    public async Task MaterializeFinalTablesProducesValidArchive()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mgb-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "archive.sqlite");

        await using var diagnostics = DiagnosticsWriter.Create(directory);
        using (var store = new SqliteArchiveStore(database))
        {
            store.Initialize();
            var listed = new ListedS3Entry(
                SourceListOrdinal: 0,
                Key: "alice/project1/tile/Brick",
                RawVersionId: "null",
                IsLatest: true,
                IsDeleteMarker: false,
                LastModifiedUtc: DateTimeOffset.Parse("2011-09-15T22:58:53.000Z"),
                ETag: "8d777f385d3dfec8815d20f7496026dc",
                ContentLengthBytes: 4,
                StorageClass: "STANDARD",
                SourceListXml: "<Version><Key>alice/project1/tile/Brick</Key></Version>");

            store.InsertListingPage([listed]);
            store.CompleteListing("test-fingerprint");
            store.InsertDownload(new DownloadedObject(
                SourceListOrdinal: 0,
                Key: listed.Key,
                RawVersionId: "null",
                ContentType: "image/png",
                ETag: listed.ETag!,
                LastModifiedUtc: listed.LastModifiedUtc,
                ContentLengthBytes: 4,
                Body: "data"u8.ToArray(),
                BodySha256: "3a6eb0790f39ac87c94f3856b2dd2c5d110e6811602261a9a923d3bb23adc8b7",
                Headers: new HeaderBag(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["etag"] = ["\"8d777f385d3dfec8815d20f7496026dc\""],
                    ["content-type"] = ["image/png"],
                    ["x-amz-meta-width"] = ["32"],
                    ["x-amz-meta-height"] = ["32"],
                    ["x-amz-meta-custom"] = ["kept"]
                })));

            store.MaterializeFinalTables(diagnostics);
            store.FinalizeArchiveInfo(new ArchiveOptions("bucket", new Uri("https://example.test"), database, directory, 2, Resume: false, Replace: false));
        }

        var validation = await ArchiveValidator.ValidateAsync(database, TestContext.Current.CancellationToken);

        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
    }
}
