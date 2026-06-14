using Microsoft.Data.Sqlite;
using Xunit;

namespace OpenGameBuilder.Mgb.Archive.S3Archiver.Tests;

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

    [Fact]
    public void InitializeMigratesOldInProgressContentTypeRequirement()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mgb-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "archive.sqlite");
        CreateOldInProgressDatabase(database);

        using (var store = new SqliteArchiveStore(database))
        {
            store.Initialize();
            store.InsertDownload(new DownloadedObject(
                SourceListOrdinal: 0,
                Key: "alice/project1/actor/Bad Boar",
                RawVersionId: "null",
                ContentType: null,
                ETag: "8d777f385d3dfec8815d20f7496026dc",
                LastModifiedUtc: DateTimeOffset.Parse("2011-09-15T22:58:53.000Z"),
                ContentLengthBytes: 4,
                Body: "data"u8.ToArray(),
                BodySha256: "3a6eb0790f39ac87c94f3856b2dd2c5d110e6811602261a9a923d3bb23adc8b7",
                Headers: new HeaderBag(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["etag"] = ["\"8d777f385d3dfec8815d20f7496026dc\""]
                })));
        }

        using var connection = Sqlite.OpenReadOnly(database);
        Assert.False(IsColumnNotNull(connection, "capture_download", "content_type"));
        Assert.Equal("2", Sqlite.ExecuteScalar(connection, "SELECT value FROM archive_info WHERE name = 'schema_version';"));
        var contentType = Sqlite.ExecuteScalar(connection, "SELECT content_type FROM capture_download WHERE list_id = 0;");
        Assert.True(contentType is null or DBNull);
    }

    private static void CreateOldInProgressDatabase(string database)
    {
        using var connection = Sqlite.Open(database);
        Sqlite.ExecuteNonQuery(
            connection,
            """
            CREATE TABLE archive_info (
                name TEXT PRIMARY KEY COLLATE BINARY,
                value TEXT NOT NULL
            ) STRICT, WITHOUT ROWID;

            INSERT INTO archive_info(name, value)
            VALUES ('schema', 'mgb-jgi-test1-canonical-archive'), ('schema_version', '1');

            CREATE TABLE s3_object (
                object_id INTEGER PRIMARY KEY,
                key_text TEXT NOT NULL COLLATE BINARY,
                key_utf8 BLOB NOT NULL,
                CHECK (key_utf8 = CAST(key_text AS BLOB)),
                UNIQUE (key_utf8)
            ) STRICT;

            CREATE TABLE s3_entry (
                entry_id INTEGER PRIMARY KEY,
                object_id INTEGER NOT NULL REFERENCES s3_object(object_id),
                is_delete_marker INTEGER NOT NULL CHECK (is_delete_marker IN (0, 1)),
                content_type TEXT NULL COLLATE BINARY,
                body BLOB NULL,
                content_length_bytes INTEGER NULL,
                etag TEXT NULL,
                body_sha256 TEXT NULL,
                CHECK (
                    (is_delete_marker = 1 AND content_type IS NULL)
                    OR
                    (is_delete_marker = 0 AND body IS NOT NULL AND content_type IS NOT NULL)
                )
            ) STRICT;

            CREATE TABLE capture_state (
                name TEXT PRIMARY KEY COLLATE BINARY,
                value TEXT NOT NULL
            ) STRICT, WITHOUT ROWID;

            CREATE TABLE capture_listing (
                list_id INTEGER PRIMARY KEY,
                key_text TEXT NOT NULL COLLATE BINARY,
                key_utf8 BLOB NOT NULL,
                version_id_raw TEXT NULL COLLATE BINARY,
                version_id_archive TEXT NULL COLLATE BINARY,
                is_latest INTEGER NOT NULL CHECK (is_latest IN (0, 1)),
                is_delete_marker INTEGER NOT NULL CHECK (is_delete_marker IN (0, 1)),
                last_modified_utc TEXT NOT NULL COLLATE BINARY,
                etag TEXT NULL COLLATE BINARY,
                content_length_bytes INTEGER NULL,
                storage_class TEXT NULL COLLATE BINARY,
                source_list_xml TEXT NOT NULL,
                CHECK (key_utf8 = CAST(key_text AS BLOB))
            ) STRICT;

            INSERT INTO capture_listing (
                list_id, key_text, key_utf8, version_id_raw, version_id_archive,
                is_latest, is_delete_marker, last_modified_utc, etag,
                content_length_bytes, storage_class, source_list_xml
            )
            VALUES (
                0, 'alice/project1/actor/Bad Boar', CAST('alice/project1/actor/Bad Boar' AS BLOB),
                'null', NULL, 1, 0, '2011-09-15T22:58:53.000Z',
                '8d777f385d3dfec8815d20f7496026dc', 4, 'STANDARD',
                '<Version><Key>alice/project1/actor/Bad Boar</Key></Version>'
            );

            CREATE TABLE capture_download (
                list_id INTEGER PRIMARY KEY
                    REFERENCES capture_listing(list_id)
                    ON DELETE CASCADE,
                content_type TEXT NOT NULL,
                etag TEXT NOT NULL,
                last_modified_utc TEXT NOT NULL,
                content_length_bytes INTEGER NOT NULL CHECK (content_length_bytes >= 0),
                body_sha256 TEXT NOT NULL,
                body BLOB NOT NULL,
                downloaded_utc TEXT NOT NULL,
                CHECK (content_length_bytes = length(body))
            ) STRICT;

            INSERT INTO capture_download (
                list_id, content_type, etag, last_modified_utc,
                content_length_bytes, body_sha256, body, downloaded_utc
            )
            VALUES (
                0, 'image/png', '8d777f385d3dfec8815d20f7496026dc',
                '2011-09-15T22:58:53.000Z', 4,
                '3a6eb0790f39ac87c94f3856b2dd2c5d110e6811602261a9a923d3bb23adc8b7',
                CAST('data' AS BLOB), '2026-06-10T00:00:00.000Z'
            );

            CREATE TABLE capture_response_header (
                list_id INTEGER NOT NULL
                    REFERENCES capture_listing(list_id)
                    ON DELETE CASCADE,
                name TEXT NOT NULL COLLATE BINARY,
                value TEXT NOT NULL,
                PRIMARY KEY(list_id, name, value)
            ) STRICT, WITHOUT ROWID;
            """);
    }

    private static bool IsColumnNotNull(SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
            {
                return reader.GetInt64(3) != 0;
            }
        }

        throw new InvalidOperationException($"Column {columnName} was not found in {tableName}.");
    }
}
