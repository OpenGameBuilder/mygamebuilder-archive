using System.Reflection;
using Microsoft.Data.Sqlite;

namespace MyGameBuilder.Archive.S3;

public sealed class SqliteArchiveStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteArchiveStore(string path)
    {
        _connection = Sqlite.Open(path);
        Sqlite.ExecuteNonQuery(_connection, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY;");
    }

    public void Dispose() => _connection.Dispose();

    public void Initialize()
    {
        if (!MainSchemaExists())
        {
            Sqlite.ExecuteNonQuery(_connection, LoadEmbeddedSchema());
        }

        CreateStagingTables();
        ValidateCaptureSchemaCompatibility();
    }

    public bool IsListingComplete() => string.Equals(GetState("listing_complete"), "1", StringComparison.Ordinal);

    public string? GetListingFingerprint() => GetState("listing_fingerprint");

    public long GetListingCount() => Convert.ToInt64(Sqlite.ExecuteScalar(_connection, "SELECT COUNT(*) FROM capture_listing;") ?? 0);

    public long GetDownloadedLiveCount() => Convert.ToInt64(Sqlite.ExecuteScalar(
        _connection,
        """
        SELECT COUNT(*)
        FROM capture_listing l
        JOIN capture_download d ON d.list_id = l.list_id
        WHERE l.is_delete_marker = 0;
        """) ?? 0);

    public long GetLiveCount() => Convert.ToInt64(Sqlite.ExecuteScalar(
        _connection,
        "SELECT COUNT(*) FROM capture_listing WHERE is_delete_marker = 0;") ?? 0);

    public long GetDeleteMarkerCount() => Convert.ToInt64(Sqlite.ExecuteScalar(
        _connection,
        "SELECT COUNT(*) FROM capture_listing WHERE is_delete_marker = 1;") ?? 0);

    public long GetListedContentBytes() => Convert.ToInt64(Sqlite.ExecuteScalar(
        _connection,
        "SELECT coalesce(sum(content_length_bytes), 0) FROM capture_listing WHERE is_delete_marker = 0;") ?? 0);

    public long GetDownloadedBytes() => Convert.ToInt64(Sqlite.ExecuteScalar(
        _connection,
        "SELECT coalesce(sum(content_length_bytes), 0) FROM capture_download;") ?? 0);

    public ListedS3Entry? GetFirstLiveEntry()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT list_id, key_text, version_id_raw, is_latest, is_delete_marker, last_modified_utc, etag, content_length_bytes, storage_class, source_list_xml
            FROM capture_listing
            WHERE is_delete_marker = 0
            ORDER BY list_id
            LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadListedEntry(reader) : null;
    }

    public void SetCaptureState(string name, string value) => SetState(name, value);

    public IReadOnlyList<ListedS3Entry> GetPendingDownloads(int take)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT list_id, key_text, version_id_raw, is_latest, is_delete_marker, last_modified_utc, etag, content_length_bytes, storage_class, source_list_xml
            FROM capture_listing l
            WHERE l.is_delete_marker = 0
              AND NOT EXISTS (SELECT 1 FROM capture_download d WHERE d.list_id = l.list_id)
            ORDER BY list_id
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$take", take);

        var entries = new List<ListedS3Entry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ReadListedEntry(reader));
        }

        return entries;
    }

    public void ResetListing()
    {
        Sqlite.ExecuteNonQuery(
            _connection,
            """
            DELETE FROM capture_response_header;
            DELETE FROM capture_download;
            DELETE FROM capture_listing;
            DELETE FROM capture_state;
            """);
    }

    public void InsertListingPage(IEnumerable<ListedS3Entry> entries)
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO capture_listing (
                list_id, key_text, key_utf8, version_id_raw, version_id_archive,
                is_latest, is_delete_marker, last_modified_utc, etag,
                content_length_bytes, storage_class, source_list_xml
            )
            VALUES (
                $list_id, $key_text, $key_utf8, $version_id_raw, $version_id_archive,
                $is_latest, $is_delete_marker, $last_modified_utc, $etag,
                $content_length_bytes, $storage_class, $source_list_xml
            );
            """;
        AddParameter(command, "$list_id");
        AddParameter(command, "$key_text");
        AddParameter(command, "$key_utf8");
        AddParameter(command, "$version_id_raw");
        AddParameter(command, "$version_id_archive");
        AddParameter(command, "$is_latest");
        AddParameter(command, "$is_delete_marker");
        AddParameter(command, "$last_modified_utc");
        AddParameter(command, "$etag");
        AddParameter(command, "$content_length_bytes");
        AddParameter(command, "$storage_class");
        AddParameter(command, "$source_list_xml");

        foreach (var entry in entries)
        {
            command.Parameters["$list_id"].Value = entry.SourceListOrdinal;
            command.Parameters["$key_text"].Value = entry.Key;
            command.Parameters["$key_utf8"].Value = System.Text.Encoding.UTF8.GetBytes(entry.Key);
            command.Parameters["$version_id_raw"].Value = (object?)entry.RawVersionId ?? DBNull.Value;
            command.Parameters["$version_id_archive"].Value = (object?)entry.ArchiveVersionId ?? DBNull.Value;
            command.Parameters["$is_latest"].Value = entry.IsLatest ? 1 : 0;
            command.Parameters["$is_delete_marker"].Value = entry.IsDeleteMarker ? 1 : 0;
            command.Parameters["$last_modified_utc"].Value = FormatUtc(entry.LastModifiedUtc);
            command.Parameters["$etag"].Value = (object?)entry.ETag ?? DBNull.Value;
            command.Parameters["$content_length_bytes"].Value = (object?)entry.ContentLengthBytes ?? DBNull.Value;
            command.Parameters["$storage_class"].Value = (object?)entry.StorageClass ?? DBNull.Value;
            command.Parameters["$source_list_xml"].Value = entry.SourceListXml;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void CompleteListing(string fingerprint)
    {
        SetState("listing_complete", "1");
        SetState("listing_fingerprint", fingerprint);
        SetState("listing_count", GetListingCount().ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetState("live_entry_count", GetLiveCount().ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetState("delete_marker_count", GetDeleteMarkerCount().ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetState("listed_content_bytes", GetListedContentBytes().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void InsertDownload(DownloadedObject download)
    {
        using var transaction = _connection.BeginTransaction();
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT OR REPLACE INTO capture_download (
                    list_id, content_type, etag, last_modified_utc,
                    content_length_bytes, body_sha256, body, downloaded_utc
                )
                VALUES (
                    $list_id, $content_type, $etag, $last_modified_utc,
                    $content_length_bytes, $body_sha256, $body, $downloaded_utc
                );
                """;
            command.Parameters.AddWithValue("$list_id", download.SourceListOrdinal);
            command.Parameters.AddWithValue("$content_type", download.ContentType);
            command.Parameters.AddWithValue("$etag", download.ETag);
            command.Parameters.AddWithValue("$last_modified_utc", FormatUtc(download.LastModifiedUtc));
            command.Parameters.AddWithValue("$content_length_bytes", download.ContentLengthBytes);
            command.Parameters.AddWithValue("$body_sha256", download.BodySha256);
            command.Parameters.Add("$body", SqliteType.Blob).Value = download.Body;
            command.Parameters.AddWithValue("$downloaded_utc", FormatUtc(DateTimeOffset.UtcNow));
            command.ExecuteNonQuery();
        }

        using (var deleteHeaders = _connection.CreateCommand())
        {
            deleteHeaders.Transaction = transaction;
            deleteHeaders.CommandText = "DELETE FROM capture_response_header WHERE list_id = $list_id;";
            deleteHeaders.Parameters.AddWithValue("$list_id", download.SourceListOrdinal);
            deleteHeaders.ExecuteNonQuery();
        }

        using (var headerCommand = _connection.CreateCommand())
        {
            headerCommand.Transaction = transaction;
            headerCommand.CommandText =
                """
                INSERT INTO capture_response_header(list_id, name, value)
                VALUES ($list_id, $name, $value);
                """;
            AddParameter(headerCommand, "$list_id");
            AddParameter(headerCommand, "$name");
            AddParameter(headerCommand, "$value");

            foreach (var (name, values) in download.Headers.OrderBy(static h => h.Key, StringComparer.Ordinal))
            {
                foreach (var value in values)
                {
                    headerCommand.Parameters["$list_id"].Value = download.SourceListOrdinal;
                    headerCommand.Parameters["$name"].Value = name;
                    headerCommand.Parameters["$value"].Value = value;
                    headerCommand.ExecuteNonQuery();
                }
            }
        }

        transaction.Commit();
    }

    public void MaterializeFinalTables(DiagnosticsWriter diagnostics)
    {
        EnsureAllLiveEntriesDownloaded();

        using var transaction = _connection.BeginTransaction();
        ExecuteInTransaction(
            transaction,
            """
            DELETE FROM s3_user_metadata_extra;
            DELETE FROM s3_response_header;
            DELETE FROM mgb_key_part;
            DELETE FROM s3_entry;
            DELETE FROM s3_object;
            """);

        ExecuteInTransaction(
            transaction,
            """
            INSERT INTO s3_object(object_id, key_text, key_utf8)
            SELECT row_number() OVER (ORDER BY MIN(list_id)) AS object_id, key_text, key_utf8
            FROM capture_listing
            GROUP BY key_text, key_utf8
            ORDER BY MIN(list_id);
            """);

        InsertMgbKeyParts(transaction, diagnostics);

        ExecuteInTransaction(
            transaction,
            """
            WITH ordered AS (
                SELECT
                    l.*,
                    row_number() OVER (
                        PARTITION BY l.key_text
                        ORDER BY l.list_id DESC
                    ) - 1 AS version_order
                FROM capture_listing l
            )
            INSERT INTO s3_entry (
                entry_id, object_id, version_id, version_order, is_latest, is_delete_marker,
                source_list_ordinal, source_list_xml, last_modified_utc, content_type, etag, storage_class,
                content_length_bytes, body_sha256, body,
                meta_width, meta_height, meta_tilename, meta_blobencoding, meta_comment, meta_acl
            )
            SELECT
                o.list_id,
                obj.object_id,
                o.version_id_archive,
                o.version_order,
                o.is_latest,
                o.is_delete_marker,
                o.list_id,
                o.source_list_xml,
                o.last_modified_utc,
                CASE WHEN o.is_delete_marker = 1 THEN NULL ELSE d.content_type END,
                CASE WHEN o.is_delete_marker = 1 THEN NULL ELSE d.etag END,
                o.storage_class,
                CASE WHEN o.is_delete_marker = 1 THEN NULL ELSE d.content_length_bytes END,
                CASE WHEN o.is_delete_marker = 1 THEN NULL ELSE d.body_sha256 END,
                CASE WHEN o.is_delete_marker = 1 THEN NULL ELSE d.body END,
                (SELECT h.value FROM capture_response_header h WHERE h.list_id = o.list_id AND h.name = 'x-amz-meta-width' LIMIT 1),
                (SELECT h.value FROM capture_response_header h WHERE h.list_id = o.list_id AND h.name = 'x-amz-meta-height' LIMIT 1),
                (SELECT h.value FROM capture_response_header h WHERE h.list_id = o.list_id AND h.name = 'x-amz-meta-tilename' LIMIT 1),
                (SELECT h.value FROM capture_response_header h WHERE h.list_id = o.list_id AND h.name = 'x-amz-meta-blobencoding' LIMIT 1),
                (SELECT h.value FROM capture_response_header h WHERE h.list_id = o.list_id AND h.name = 'x-amz-meta-comment' LIMIT 1),
                (SELECT h.value FROM capture_response_header h WHERE h.list_id = o.list_id AND h.name = 'x-amz-meta-acl' LIMIT 1)
            FROM ordered o
            JOIN s3_object obj ON obj.key_utf8 = o.key_utf8
            LEFT JOIN capture_download d ON d.list_id = o.list_id;
            """);

        ExecuteInTransaction(
            transaction,
            """
            INSERT INTO s3_response_header(entry_id, name, value)
            SELECT list_id, name, value
            FROM capture_response_header;
            """);

        ExecuteInTransaction(
            transaction,
            """
            INSERT INTO s3_user_metadata_extra(entry_id, name, value)
            SELECT
                list_id,
                substr(name, length('x-amz-meta-') + 1),
                value
            FROM capture_response_header
            WHERE name LIKE 'x-amz-meta-%'
              AND substr(name, length('x-amz-meta-') + 1) NOT IN (
                  'width', 'height', 'tilename', 'blobencoding', 'comment', 'acl'
              );
            """);

        SetState("materialized_utc", FormatUtc(DateTimeOffset.UtcNow), transaction);
        transaction.Commit();
    }

    public void FinalizeArchiveInfo(ArchiveOptions options)
    {
        using var transaction = _connection.BeginTransaction();
        UpsertArchiveInfo("bucket", options.Bucket, transaction);
        UpsertArchiveInfo("source_endpoint", new Uri(options.Endpoint, options.Bucket + "/").ToString(), transaction);
        UpsertArchiveInfo("capture_tool", "MyGameBuilder.Archive.S3", transaction);
        UpsertArchiveInfo("created_utc", FormatUtc(DateTimeOffset.UtcNow), transaction);
        UpsertArchiveInfo("listing_fingerprint_sha256", GetState("listing_fingerprint") ?? string.Empty, transaction);
        UpsertArchiveInfo("listed_entry_count", GetListingCount().ToString(System.Globalization.CultureInfo.InvariantCulture), transaction);
        UpsertArchiveInfo("live_entry_count", GetLiveCount().ToString(System.Globalization.CultureInfo.InvariantCulture), transaction);
        UpsertArchiveInfo("delete_marker_count", GetDeleteMarkerCount().ToString(System.Globalization.CultureInfo.InvariantCulture), transaction);
        UpsertArchiveInfo("listed_content_bytes", GetListedContentBytes().ToString(System.Globalization.CultureInfo.InvariantCulture), transaction);
        CopyCaptureStateToArchiveInfo("anonymous_object_tagging_probe_status", transaction);
        CopyCaptureStateToArchiveInfo("anonymous_object_acl_probe_status", transaction);
        transaction.Commit();
    }

    public void DropStagingTables()
    {
        Sqlite.ExecuteNonQuery(
            _connection,
            """
            DROP TABLE IF EXISTS capture_response_header;
            DROP TABLE IF EXISTS capture_download;
            DROP TABLE IF EXISTS capture_listing;
            DROP TABLE IF EXISTS capture_state;
            PRAGMA wal_checkpoint(TRUNCATE);
            PRAGMA optimize;
            PRAGMA journal_mode = DELETE;
            """);
    }

    private void EnsureAllLiveEntriesDownloaded()
    {
        var missing = Convert.ToInt64(Sqlite.ExecuteScalar(
            _connection,
            """
            SELECT COUNT(*)
            FROM capture_listing l
            WHERE l.is_delete_marker = 0
              AND NOT EXISTS (SELECT 1 FROM capture_download d WHERE d.list_id = l.list_id);
            """) ?? 0);

        if (missing != 0)
        {
            throw new ArchiveFatalException($"{missing} live entries are still missing downloaded bodies.");
        }
    }

    private void InsertMgbKeyParts(SqliteTransaction transaction, DiagnosticsWriter diagnostics)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT object_id, key_text FROM s3_object ORDER BY object_id;";

        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO mgb_key_part(object_id, user_name, project_name, piece_type, piece_name)
            VALUES ($object_id, $user_name, $project_name, $piece_type, $piece_name);
            """;
        AddParameter(insert, "$object_id");
        AddParameter(insert, "$user_name");
        AddParameter(insert, "$project_name");
        AddParameter(insert, "$piece_type");
        AddParameter(insert, "$piece_name");

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var objectId = reader.GetInt64(0);
            var key = reader.GetString(1);
            if (!MgbKeyParser.TryParse(key, out var part))
            {
                diagnostics.Write("mgb-key", "warning", "S3 key did not match the MGB key projection.", key);
                continue;
            }

            insert.Parameters["$object_id"].Value = objectId;
            insert.Parameters["$user_name"].Value = part.UserName;
            insert.Parameters["$project_name"].Value = part.ProjectName;
            insert.Parameters["$piece_type"].Value = part.PieceType;
            insert.Parameters["$piece_name"].Value = part.PieceName;
            insert.ExecuteNonQuery();
        }
    }

    private void CreateStagingTables()
    {
        Sqlite.ExecuteNonQuery(
            _connection,
            """
            CREATE TABLE IF NOT EXISTS capture_state (
                name TEXT PRIMARY KEY COLLATE BINARY,
                value TEXT NOT NULL
            ) STRICT, WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS capture_listing (
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

            CREATE UNIQUE INDEX IF NOT EXISTS ux_capture_listing_identity_nonnull
                ON capture_listing(key_utf8, version_id_archive)
                WHERE version_id_archive IS NOT NULL;

            CREATE UNIQUE INDEX IF NOT EXISTS ux_capture_listing_identity_null
                ON capture_listing(key_utf8)
                WHERE version_id_archive IS NULL;

            CREATE TABLE IF NOT EXISTS capture_download (
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

            CREATE TABLE IF NOT EXISTS capture_response_header (
                list_id INTEGER NOT NULL
                    REFERENCES capture_listing(list_id)
                    ON DELETE CASCADE,
                name TEXT NOT NULL COLLATE BINARY,
                value TEXT NOT NULL,
                PRIMARY KEY(list_id, name, value)
            ) STRICT, WITHOUT ROWID;
            """);
    }

    private void ValidateCaptureSchemaCompatibility()
    {
        var requiredListingColumns = new[]
        {
            "list_id",
            "key_text",
            "key_utf8",
            "version_id_raw",
            "version_id_archive",
            "is_latest",
            "is_delete_marker",
            "last_modified_utc",
            "etag",
            "content_length_bytes",
            "storage_class",
            "source_list_xml"
        };

        var existingColumns = new HashSet<string>(StringComparer.Ordinal);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(capture_listing);";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        var missing = requiredListingColumns.Where(column => !existingColumns.Contains(column)).ToArray();
        if (missing.Length != 0)
        {
            throw new ArchiveFatalException(
                "The existing in-progress archive uses an older capture schema and cannot be safely resumed. "
                + "Delete the .inprogress.sqlite file or run capture with --replace so the bucket can be re-enumerated. "
                + "Missing capture_listing columns: "
                + string.Join(", ", missing));
        }
    }

    private bool MainSchemaExists()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'archive_info';";
        return Convert.ToInt64(command.ExecuteScalar() ?? 0) > 0;
    }

    private static ListedS3Entry ReadListedEntry(SqliteDataReader reader)
    {
        return new ListedS3Entry(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt64(3) == 1,
            reader.GetInt64(4) == 1,
            DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9));
    }

    private string? GetState(string name)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM capture_state WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() as string;
    }

    private void SetState(string name, string value, SqliteTransaction? transaction = null)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO capture_state(name, value)
            VALUES ($name, $value)
            ON CONFLICT(name) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private void UpsertArchiveInfo(string name, string value, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO archive_info(name, value)
            VALUES ($name, $value)
            ON CONFLICT(name) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private void CopyCaptureStateToArchiveInfo(string name, SqliteTransaction transaction)
    {
        var value = GetState(name);
        if (value is not null)
        {
            UpsertArchiveInfo(name, value, transaction);
        }
    }

    private void ExecuteInTransaction(SqliteTransaction transaction, string sql)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void AddParameter(SqliteCommand command, string name)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        command.Parameters.Add(parameter);
    }

    private static string FormatUtc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static string LoadEmbeddedSchema()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith("schema-draft.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new ArchiveFatalException("Embedded SQLite schema resource was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
