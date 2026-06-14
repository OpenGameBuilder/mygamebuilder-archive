using Microsoft.Data.Sqlite;
using Xunit;

namespace OpenGameBuilder.Mgb.Archive.S3Archiver.Tests;

public sealed class UnversionedArchiveMigratorTests
{
    [Fact]
    public async Task ConvertAsyncCreatesSimplifiedArchiveWhenSourceHasNoVersioningArtifacts()
    {
        var directory = CreateTempDirectory();
        var source = Path.Combine(directory, "source.sqlite");
        var output = Path.Combine(directory, "simple.sqlite");
        await CreateSourceArchiveAsync(source, directory, versioned: false);

        var analysis = await UnversionedArchiveMigrator.AnalyzeAsync(source, TestContext.Current.CancellationToken);
        Assert.True(analysis.IsUnversioned);

        await UnversionedArchiveMigrator.ConvertAsync(source, output, replace: false, TestContext.Current.CancellationToken);

        var validation = await UnversionedArchiveMigrator.ValidateSimplifiedAsync(output, TestContext.Current.CancellationToken);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        using var connection = Sqlite.OpenReadOnly(output);
        Assert.Equal(0L, CountColumns(connection, "s3_object", "version_id"));
        Assert.Equal(0L, CountTables(connection, "s3_entry"));
        Assert.Equal(0L, CountArchiveInfo(connection, "versioning"));
        Assert.Equal(1L, CountArchiveInfo(connection, "content_shape"));
        Assert.Equal(1L, Convert.ToInt64(Sqlite.ExecuteScalar(connection, "SELECT COUNT(*) FROM s3_object;")));
        Assert.Equal("data", System.Text.Encoding.UTF8.GetString((byte[])Sqlite.ExecuteScalar(connection, "SELECT body FROM s3_object;")!));
    }

    [Fact]
    public async Task ConvertAsyncRefusesVersionedArchive()
    {
        var directory = CreateTempDirectory();
        var source = Path.Combine(directory, "source.sqlite");
        var output = Path.Combine(directory, "simple.sqlite");
        await CreateSourceArchiveAsync(source, directory, versioned: true);

        var analysis = await UnversionedArchiveMigrator.AnalyzeAsync(source, TestContext.Current.CancellationToken);
        Assert.False(analysis.IsUnversioned);
        Assert.Equal(2, analysis.NonNullVersionIdCount);
        Assert.Equal(1, analysis.ObjectsWithoutExactlyOneEntryCount);

        await Assert.ThrowsAsync<ArchiveFatalException>(() =>
            UnversionedArchiveMigrator.ConvertAsync(source, output, replace: false, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(output));
    }

    private static async Task CreateSourceArchiveAsync(string database, string workDirectory, bool versioned)
    {
        await using var diagnostics = DiagnosticsWriter.Create(workDirectory);
        using var store = new SqliteArchiveStore(database);
        store.Initialize();

        if (versioned)
        {
            var oldEntry = NewListedEntry(0, "alice/project1/tile/Brick", "old-version", isLatest: false, bodyLength: 3);
            var latestEntry = NewListedEntry(1, "alice/project1/tile/Brick", "new-version", isLatest: true, bodyLength: 4);
            store.InsertListingPage([latestEntry, oldEntry]);
            store.CompleteListing("fingerprint");
            store.InsertDownload(NewDownload(oldEntry, "old"u8.ToArray(), "cba06b5736faf67e54b07b561eae94395e774c517a7d910a54369e1263ccfbd4"));
            store.InsertDownload(NewDownload(latestEntry, "data"u8.ToArray(), "3a6eb0790f39ac87c94f3856b2dd2c5d110e6811602261a9a923d3bb23adc8b7"));
        }
        else
        {
            var entry = NewListedEntry(0, "alice/project1/tile/Brick", "null", isLatest: true, bodyLength: 4);
            store.InsertListingPage([entry]);
            store.CompleteListing("fingerprint");
            store.InsertDownload(NewDownload(entry, "data"u8.ToArray(), "3a6eb0790f39ac87c94f3856b2dd2c5d110e6811602261a9a923d3bb23adc8b7"));
        }

        store.SetCaptureState("anonymous_object_tagging_probe_status", "403");
        store.SetCaptureState("anonymous_object_acl_probe_status", "403");
        store.MaterializeFinalTables(diagnostics);
        store.FinalizeArchiveInfo(new ArchiveOptions("bucket", new Uri("https://example.test"), database, workDirectory, 2, Resume: false, Replace: false));
    }

    private static ListedS3Entry NewListedEntry(long ordinal, string key, string rawVersionId, bool isLatest, long bodyLength) =>
        new(
            ordinal,
            key,
            rawVersionId,
            isLatest,
            IsDeleteMarker: false,
            DateTimeOffset.Parse("2011-09-15T22:58:53.000Z"),
            ETag: "8d777f385d3dfec8815d20f7496026dc",
            ContentLengthBytes: bodyLength,
            StorageClass: "STANDARD",
            SourceListXml: $"<Version><Key>{key}</Key><VersionId>{rawVersionId}</VersionId></Version>");

    private static DownloadedObject NewDownload(ListedS3Entry entry, byte[] body, string sha256) =>
        new(
            entry.SourceListOrdinal,
            entry.Key,
            entry.RawVersionId,
            ContentType: "image/png",
            ETag: entry.ETag!,
            entry.LastModifiedUtc,
            body.LongLength,
            body,
            sha256,
            new HeaderBag(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["etag"] = [$"\"{entry.ETag}\""],
                ["content-type"] = ["image/png"],
                ["x-amz-meta-width"] = ["32"],
                ["x-amz-meta-height"] = ["32"]
            }));

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mgb-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static long CountColumns(SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE name = $column;";
        command.Parameters.AddWithValue("$column", columnName);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static long CountTables(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $table;";
        command.Parameters.AddWithValue("$table", tableName);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static long CountArchiveInfo(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM archive_info WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
