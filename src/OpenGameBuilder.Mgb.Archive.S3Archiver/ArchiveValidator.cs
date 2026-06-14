using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace OpenGameBuilder.Mgb.Archive.S3Archiver;

public static class ArchiveValidator
{
    public static Task<ValidationResult> ValidateAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(databasePath))
        {
            return Task.FromResult(ValidationResult.Failure([$"Database does not exist: {databasePath}"]));
        }

        using var connection = Sqlite.OpenReadOnly(databasePath);
        var errors = new List<string>();
        var warnings = new List<string>();

        RequireIntegrityCheck(connection, errors);
        RequireNoRows(connection, "PRAGMA foreign_key_check;", "foreign_key_check returned violations.", errors);
        RequireNoRows(connection, "SELECT * FROM v_integrity_object_without_current;", "One or more objects have no latest entry.", errors);
        RequireNoRows(
            connection,
            """
            SELECT entry_id
            FROM s3_entry
            WHERE is_delete_marker = 0
              AND (body IS NULL OR body_sha256 IS NULL OR content_length_bytes IS NULL OR content_length_bytes != length(body));
            """,
            "One or more live entries are missing body data or have incorrect body lengths.",
            errors);
        RequireNoRows(
            connection,
            """
            SELECT entry_id
            FROM s3_entry
            WHERE is_delete_marker = 1
              AND (body IS NOT NULL OR body_sha256 IS NOT NULL OR content_length_bytes IS NOT NULL OR content_type IS NOT NULL OR etag IS NOT NULL);
            """,
            "One or more delete markers contain live-object data.",
            errors);
        RequireNoRows(
            connection,
            "SELECT entry_id FROM s3_entry WHERE source_list_xml IS NULL OR length(source_list_xml) = 0;",
            "One or more entries are missing source ListObjectVersions XML.",
            errors);
        RequireNoRows(
            connection,
            """
            SELECT e.entry_id
            FROM s3_entry e
            WHERE e.is_delete_marker = 0
              AND NOT EXISTS (SELECT 1 FROM s3_response_header h WHERE h.entry_id = e.entry_id);
            """,
            "One or more live entries have no captured GET response headers.",
            errors);
        RequireNoRows(
            connection,
            """
            SELECT h.entry_id
            FROM s3_response_header h
            JOIN s3_entry e ON e.entry_id = h.entry_id
            WHERE e.is_delete_marker = 1;
            """,
            "One or more delete markers have GET response headers.",
            errors);
        RequireArchiveInfoCount(connection, "listed_entry_count", "SELECT COUNT(*) FROM s3_entry;", errors);
        RequireArchiveInfoCount(connection, "live_entry_count", "SELECT COUNT(*) FROM s3_entry WHERE is_delete_marker = 0;", errors);
        RequireArchiveInfoCount(connection, "delete_marker_count", "SELECT COUNT(*) FROM s3_entry WHERE is_delete_marker = 1;", errors);
        RequireArchiveInfoCount(connection, "listed_content_bytes", "SELECT coalesce(sum(content_length_bytes), 0) FROM s3_entry WHERE is_delete_marker = 0;", errors);
        VerifyBodyHashes(connection, errors, cancellationToken);

        var missingMgb = CountRows(connection, "SELECT COUNT(*) FROM v_integrity_live_entry_without_mgb_key;");
        if (missingMgb > 0)
        {
            warnings.Add($"{missingMgb} current live entries do not match the MGB key projection.");
        }

        WarnIfArchiveInfoMissing(connection, "anonymous_object_tagging_probe_status", warnings);
        WarnIfArchiveInfoMissing(connection, "anonymous_object_acl_probe_status", warnings);

        return Task.FromResult(errors.Count == 0
            ? ValidationResult.Success(warnings)
            : ValidationResult.Failure(errors, warnings));
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

    private static void RequireNoRows(
        SqliteConnection connection,
        string sql,
        string message,
        List<string> errors)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            errors.Add(message);
        }
    }

    private static long CountRows(SqliteConnection connection, string sql) => Convert.ToInt64(Sqlite.ExecuteScalar(connection, sql) ?? 0);

    private static void RequireArchiveInfoCount(SqliteConnection connection, string name, string sql, List<string> errors)
    {
        var expected = GetArchiveInfo(connection, name);
        if (expected is null)
        {
            errors.Add($"archive_info is missing '{name}'.");
            return;
        }

        var actual = CountRows(connection, sql).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            errors.Add($"archive_info '{name}' is '{expected}', but the database contains '{actual}'.");
        }
    }

    private static void WarnIfArchiveInfoMissing(SqliteConnection connection, string name, List<string> warnings)
    {
        if (GetArchiveInfo(connection, name) is null)
        {
            warnings.Add($"archive_info is missing '{name}'.");
        }
    }

    private static string? GetArchiveInfo(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM archive_info WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() as string;
    }

    private static void VerifyBodyHashes(SqliteConnection connection, List<string> errors, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT entry_id, body, body_sha256
            FROM s3_entry
            WHERE is_delete_marker = 0;
            """;

        using var reader = command.ExecuteReader();
        using var sha256 = SHA256.Create();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryId = reader.GetInt64(0);
            var body = (byte[])reader["body"];
            var expected = reader.GetString(2);
            var actual = Convert.ToHexString(sha256.ComputeHash(body)).ToLowerInvariant();
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                errors.Add($"SHA-256 mismatch for entry_id {entryId}.");
                return;
            }
        }
    }
}
