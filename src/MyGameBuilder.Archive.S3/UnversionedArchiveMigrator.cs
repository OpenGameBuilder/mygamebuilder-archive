using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace MyGameBuilder.Archive.S3;

public static class UnversionedArchiveMigrator
{
    public static bool IsSimplifiedArchive(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return false;
        }

        try
        {
            using var connection = Sqlite.OpenReadOnly(databasePath);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT value
                FROM archive_info
                WHERE name = 'schema';
                """;
            return string.Equals(command.ExecuteScalar() as string, "mgb-jgi-test1-unversioned-archive", StringComparison.Ordinal);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    public static Task<UnversionedArchiveAnalysis> AnalyzeAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Sqlite.OpenReadOnly(databasePath);
        var analysis = new UnversionedArchiveAnalysis(
            Count(connection, "SELECT COUNT(*) FROM s3_object;"),
            Count(connection, "SELECT COUNT(*) FROM s3_entry;"),
            Count(connection, "SELECT COUNT(*) FROM s3_entry WHERE is_delete_marker = 0;"),
            Count(connection, "SELECT COUNT(*) FROM s3_entry WHERE is_delete_marker = 1;"),
            Count(connection, "SELECT COUNT(*) FROM s3_entry WHERE version_id IS NOT NULL;"),
            Count(
                connection,
                """
                SELECT COUNT(*)
                FROM s3_object o
                WHERE (SELECT COUNT(*) FROM s3_entry e WHERE e.object_id = o.object_id) <> 1;
                """),
            Count(connection, "SELECT COUNT(*) FROM s3_entry WHERE is_latest <> 1;"),
            Count(connection, "SELECT COUNT(*) FROM s3_entry WHERE version_order <> 0;"),
            Count(
                connection,
                """
                SELECT COUNT(*)
                FROM s3_entry
                WHERE is_delete_marker = 0
                  AND (
                      body IS NULL
                      OR body_sha256 IS NULL
                      OR content_length_bytes IS NULL
                      OR content_length_bytes <> length(body)
                  );
                """),
            Count(
                connection,
                """
                SELECT COUNT(*)
                FROM (
                    SELECT lower(key_text) AS key_lower
                    FROM s3_object
                    GROUP BY lower(key_text)
                    HAVING COUNT(*) > 1
                );
                """));

        return Task.FromResult(analysis);
    }

    public static async Task ConvertAsync(string sourceDatabasePath, string outputPath, bool replace, CancellationToken cancellationToken = default)
    {
        var sourceValidation = await ArchiveValidator.ValidateAsync(sourceDatabasePath, cancellationToken).ConfigureAwait(false);
        if (!sourceValidation.IsValid)
        {
            throw new ArchiveFatalException("Source archive validation failed; refusing to create simplified archive: " + string.Join("; ", sourceValidation.Errors));
        }

        var analysis = await AnalyzeAsync(sourceDatabasePath, cancellationToken).ConfigureAwait(false);
        if (!analysis.IsUnversioned)
        {
            throw new ArchiveFatalException("Source archive contains versioning evidence; simplified archive was not created.");
        }

        var absoluteOutputPath = Path.GetFullPath(outputPath);
        var tempPath = absoluteOutputPath + ".inprogress.sqlite";
        Directory.CreateDirectory(Path.GetDirectoryName(absoluteOutputPath) ?? ".");

        if (File.Exists(absoluteOutputPath) && !replace)
        {
            throw new ArchiveFatalException($"Output archive already exists: {absoluteOutputPath}. Use --replace to overwrite it.");
        }

        DeleteIfExists(tempPath);
        if (replace)
        {
            DeleteIfExists(absoluteOutputPath);
        }

        try
        {
            using (var connection = Sqlite.Open(tempPath))
            {
                Sqlite.ExecuteNonQuery(connection, SimplifiedSchemaSql);
                AttachSource(connection, sourceDatabasePath);
                CopyData(connection, sourceDatabasePath, analysis);
                Sqlite.ExecuteNonQuery(
                    connection,
                    """
                    PRAGMA foreign_key_check;
                    PRAGMA integrity_check;
                    PRAGMA optimize;
                    PRAGMA journal_mode = DELETE;
                    """);
            }

            Sqlite.ClearPools();
            EnsureNoSqliteSidecars(tempPath);

            var validation = await ValidateSimplifiedAsync(tempPath, cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                throw new ArchiveFatalException("Simplified archive validation failed; refusing to publish: " + string.Join("; ", validation.Errors));
            }

            Sqlite.ClearPools();
            File.Move(tempPath, absoluteOutputPath);
            File.SetAttributes(absoluteOutputPath, File.GetAttributes(absoluteOutputPath) | FileAttributes.ReadOnly);
        }
        catch
        {
            Sqlite.ClearPools();
            throw;
        }
    }

    public static Task<ValidationResult> ValidateSimplifiedAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(databasePath))
        {
            return Task.FromResult(ValidationResult.Failure([$"Database does not exist: {databasePath}"]));
        }

        using var connection = Sqlite.OpenReadOnly(databasePath);
        var errors = new List<string>();
        RequireIntegrityCheck(connection, errors);
        RequireNoRows(connection, "PRAGMA foreign_key_check;", "foreign_key_check returned violations.", errors);
        RequireNoRows(
            connection,
            """
            SELECT object_id
            FROM s3_object
            WHERE body IS NULL
              OR body_sha256 IS NULL
              OR content_length_bytes IS NULL
              OR content_length_bytes <> length(body);
            """,
            "One or more objects are missing body data or have incorrect body lengths.",
            errors);
        RequireNoRows(
            connection,
            """
            SELECT o.object_id
            FROM s3_object o
            WHERE NOT EXISTS (SELECT 1 FROM s3_response_header h WHERE h.object_id = o.object_id);
            """,
            "One or more objects have no captured GET response headers.",
            errors);
        RequireArchiveInfoCount(connection, "object_count", "SELECT COUNT(*) FROM s3_object;", errors);
        RequireArchiveInfoCount(connection, "content_bytes", "SELECT coalesce(sum(content_length_bytes), 0) FROM s3_object;", errors);
        VerifyBodyHashes(connection, errors, cancellationToken);

        return Task.FromResult(errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors));
    }

    private static void AttachSource(SqliteConnection connection, string sourceDatabasePath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "ATTACH DATABASE $source AS source;";
        command.Parameters.AddWithValue("$source", sourceDatabasePath);
        command.ExecuteNonQuery();
    }

    private static void CopyData(SqliteConnection connection, string sourceDatabasePath, UnversionedArchiveAnalysis analysis)
    {
        using var transaction = connection.BeginTransaction();
        Execute(
            connection,
            transaction,
            """
            INSERT INTO archive_info(name, value)
            SELECT name, value
            FROM source.archive_info
            WHERE name IN (
                'bucket',
                'source_endpoint',
                'content_scope',
                'capture_tool',
                'anonymous_object_tagging_probe_status',
                'anonymous_object_acl_probe_status'
            );

            INSERT INTO archive_info(name, value)
            VALUES
                ('schema', 'mgb-jgi-test1-unversioned-archive'),
                ('schema_version', '2'),
                ('source_archive_path', $source_archive_path),
                ('source_archive_schema', coalesce((SELECT value FROM source.archive_info WHERE name = 'schema'), 'unknown')),
                ('source_archive_listing_fingerprint_sha256', coalesce((SELECT value FROM source.archive_info WHERE name = 'listing_fingerprint_sha256'), 'unknown')),
                ('created_utc', $created_utc),
                ('content_shape', 'one row per S3 object with body and source metadata directly on s3_object'),
                ('object_count', $object_count),
                ('content_bytes', $content_bytes),
                ('case_insensitive_key_collision_groups', $case_insensitive_key_collision_groups)
            ON CONFLICT(name) DO UPDATE SET value = excluded.value;

            INSERT INTO s3_object (
                object_id, key_text, key_utf8,
                last_modified_utc, content_type, etag, storage_class,
                content_length_bytes, body_sha256, body,
                source_list_ordinal, source_list_xml,
                meta_width, meta_height, meta_tilename, meta_blobencoding, meta_comment, meta_acl
            )
            SELECT
                o.object_id,
                o.key_text,
                o.key_utf8,
                e.last_modified_utc,
                e.content_type,
                e.etag,
                e.storage_class,
                e.content_length_bytes,
                e.body_sha256,
                e.body,
                e.source_list_ordinal,
                e.source_list_xml,
                e.meta_width,
                e.meta_height,
                e.meta_tilename,
                e.meta_blobencoding,
                e.meta_comment,
                e.meta_acl
            FROM source.s3_object o
            JOIN source.s3_entry e ON e.object_id = o.object_id
            WHERE e.is_delete_marker = 0
              AND e.is_latest = 1
              AND e.version_id IS NULL
              AND e.version_order = 0;

            INSERT INTO mgb_key_part(object_id, user_name, project_name, piece_type, piece_name)
            SELECT object_id, user_name, project_name, piece_type, piece_name
            FROM source.mgb_key_part;

            INSERT INTO s3_response_header(object_id, name, value)
            SELECT e.object_id, h.name, h.value
            FROM source.s3_response_header h
            JOIN source.s3_entry e ON e.entry_id = h.entry_id;

            INSERT INTO s3_user_metadata_extra(object_id, name, value)
            SELECT e.object_id, m.name, m.value
            FROM source.s3_user_metadata_extra m
            JOIN source.s3_entry e ON e.entry_id = m.entry_id;
            """,
            [
                new("$source_archive_path", sourceDatabasePath),
                new("$created_utc", DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)),
                new("$object_count", analysis.ObjectCount.ToString(CultureInfo.InvariantCulture)),
                new("$content_bytes", Count(connection, "SELECT coalesce(sum(content_length_bytes), 0) FROM source.s3_entry WHERE is_delete_marker = 0;").ToString(CultureInfo.InvariantCulture)),
                new("$case_insensitive_key_collision_groups", analysis.CaseInsensitiveKeyCollisionGroupCount.ToString(CultureInfo.InvariantCulture))
            ]);
        transaction.Commit();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, IReadOnlyList<SqliteParameter>? parameters = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (parameters is not null)
        {
            command.Parameters.AddRange(parameters);
        }

        command.ExecuteNonQuery();
    }

    private static long Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0);
    }

    private static void RequireIntegrityCheck(SqliteConnection connection, List<string> errors)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var value = reader.GetString(0);
            if (!string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("integrity_check: " + value);
            }
        }
    }

    private static void RequireNoRows(SqliteConnection connection, string sql, string message, List<string> errors)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            errors.Add(message);
        }
    }

    private static void RequireArchiveInfoCount(SqliteConnection connection, string name, string sql, List<string> errors)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM archive_info WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        var expected = command.ExecuteScalar() as string;
        if (expected is null)
        {
            errors.Add($"archive_info is missing '{name}'.");
            return;
        }

        var actual = Count(connection, sql).ToString(CultureInfo.InvariantCulture);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            errors.Add($"archive_info '{name}' is '{expected}', but the database contains '{actual}'.");
        }
    }

    private static void VerifyBodyHashes(SqliteConnection connection, List<string> errors, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT object_id, body, body_sha256 FROM s3_object;";
        using var reader = command.ExecuteReader();
        using var sha256 = SHA256.Create();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var objectId = reader.GetInt64(0);
            var body = (byte[])reader["body"];
            var expected = reader.GetString(2);
            var actual = Convert.ToHexString(sha256.ComputeHash(body)).ToLowerInvariant();
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                errors.Add($"SHA-256 mismatch for object_id {objectId}.");
                return;
            }
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
    }

    private static void EnsureNoSqliteSidecars(string databasePath)
    {
        var sidecars = new[] { databasePath + "-wal", databasePath + "-shm" }
            .Where(File.Exists)
            .ToArray();
        if (sidecars.Length != 0)
        {
            throw new ArchiveFatalException("SQLite sidecar files remained after simplified archive finalization: " + string.Join(", ", sidecars));
        }
    }

    private const string SimplifiedSchemaSql =
        """
        PRAGMA encoding = 'UTF-8';
        PRAGMA foreign_keys = ON;
        PRAGMA journal_mode = DELETE;
        PRAGMA synchronous = NORMAL;
        PRAGMA busy_timeout = 5000;
        PRAGMA application_id = 0x4D474241;
        PRAGMA user_version = 2;

        CREATE TABLE archive_info (
            name  TEXT PRIMARY KEY COLLATE BINARY,
            value TEXT NOT NULL
        ) STRICT, WITHOUT ROWID;

        CREATE TABLE s3_object (
            object_id INTEGER PRIMARY KEY,
            key_text TEXT NOT NULL COLLATE BINARY,
            key_utf8 BLOB NOT NULL,
            last_modified_utc TEXT NOT NULL COLLATE BINARY,
            content_type TEXT NULL COLLATE BINARY,
            etag TEXT NOT NULL COLLATE BINARY,
            storage_class TEXT NULL COLLATE BINARY,
            content_length_bytes INTEGER NOT NULL CHECK (content_length_bytes >= 0),
            body_sha256 TEXT NOT NULL COLLATE BINARY CHECK (
                length(body_sha256) = 64
                AND body_sha256 NOT GLOB '*[^0-9a-f]*'
            ),
            body BLOB NOT NULL,
            source_list_ordinal INTEGER NOT NULL CHECK (source_list_ordinal >= 0),
            source_list_xml TEXT NOT NULL,
            meta_width        TEXT NULL,
            meta_height       TEXT NULL,
            meta_tilename     TEXT NULL,
            meta_blobencoding TEXT NULL,
            meta_comment      TEXT NULL,
            meta_acl          TEXT NULL,
            CHECK (length(key_utf8) BETWEEN 1 AND 1024),
            CHECK (key_utf8 = CAST(key_text AS BLOB)),
            CHECK (content_length_bytes = length(body)),
            UNIQUE (key_utf8)
        ) STRICT;

        CREATE UNIQUE INDEX ux_s3_object_key_text
            ON s3_object(key_text COLLATE BINARY);

        CREATE UNIQUE INDEX ux_s3_object_source_list_ordinal
            ON s3_object(source_list_ordinal);

        CREATE INDEX ix_s3_object_last_modified
            ON s3_object(last_modified_utc);

        CREATE INDEX ix_s3_object_etag
            ON s3_object(etag);

        CREATE INDEX ix_s3_object_body_sha256
            ON s3_object(body_sha256);

        CREATE TABLE mgb_key_part (
            object_id INTEGER PRIMARY KEY
                REFERENCES s3_object(object_id)
                ON DELETE RESTRICT,
            user_name    TEXT NOT NULL COLLATE BINARY,
            project_name TEXT NOT NULL COLLATE BINARY,
            piece_type   TEXT NOT NULL COLLATE BINARY CHECK (piece_type IN (
                'tile', 'actor', 'map', 'screenshot', 'profile', 'tutorial'
            )),
            piece_name   TEXT NOT NULL COLLATE BINARY,
            CHECK (length(user_name) > 0),
            CHECK (length(project_name) > 0),
            CHECK (length(piece_type) > 0),
            CHECK (length(piece_name) > 0)
        ) STRICT;

        CREATE INDEX ix_mgb_key_project_piece
            ON mgb_key_part(user_name, project_name, piece_type, piece_name);

        CREATE INDEX ix_mgb_key_project
            ON mgb_key_part(user_name, project_name);

        CREATE INDEX ix_mgb_key_piece_type
            ON mgb_key_part(piece_type, user_name, project_name);

        CREATE TABLE s3_response_header (
            object_id INTEGER NOT NULL
                REFERENCES s3_object(object_id)
                ON DELETE RESTRICT,
            name TEXT NOT NULL COLLATE BINARY,
            value TEXT NOT NULL,
            PRIMARY KEY(object_id, name, value),
            CHECK (length(name) > 0)
        ) STRICT, WITHOUT ROWID;

        CREATE INDEX ix_s3_response_header_name
            ON s3_response_header(name, value);

        CREATE TABLE s3_user_metadata_extra (
            object_id INTEGER NOT NULL
                REFERENCES s3_object(object_id)
                ON DELETE RESTRICT,
            name TEXT NOT NULL COLLATE BINARY,
            value TEXT NOT NULL,
            PRIMARY KEY(object_id, name),
            CHECK (length(name) > 0),
            CHECK (name NOT IN ('width', 'height', 'tilename', 'blobencoding', 'comment', 'acl'))
        ) STRICT, WITHOUT ROWID;

        CREATE INDEX ix_s3_user_metadata_extra_name
            ON s3_user_metadata_extra(name, value);

        CREATE VIEW v_s3_objects AS
        SELECT
            object_id,
            key_text,
            key_utf8,
            last_modified_utc,
            content_type,
            etag,
            storage_class,
            content_length_bytes,
            body_sha256,
            source_list_ordinal,
            source_list_xml,
            meta_width,
            meta_height,
            meta_tilename,
            meta_blobencoding,
            meta_comment,
            meta_acl
        FROM s3_object;

        CREATE VIEW v_s3_bodies AS
        SELECT *
        FROM s3_object;

        CREATE VIEW v_mgb_pieces AS
        SELECT
            m.user_name,
            m.project_name,
            m.piece_type,
            m.piece_name,
            o.key_text,
            o.key_utf8,
            o.object_id,
            o.last_modified_utc,
            o.content_type,
            o.etag,
            o.storage_class,
            o.content_length_bytes,
            o.body_sha256,
            o.source_list_ordinal,
            o.source_list_xml,
            o.meta_width,
            o.meta_height,
            o.meta_tilename,
            o.meta_blobencoding,
            o.meta_comment,
            o.meta_acl
        FROM s3_object o
        JOIN mgb_key_part m ON m.object_id = o.object_id;
        """;
}
