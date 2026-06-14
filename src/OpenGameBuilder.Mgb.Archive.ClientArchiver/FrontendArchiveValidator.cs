using Microsoft.Data.Sqlite;

namespace OpenGameBuilder.Mgb.Archive.ClientArchiver;

public static class FrontendArchiveValidator
{
    private static readonly string[] RequiredTables =
    [
        "archive_info",
        "frontend_seed",
        "frontend_exclude",
        "frontend_resource",
        "frontend_capture",
        "frontend_content",
        "frontend_seed_capture",
        "frontend_response_header"
    ];

    public static Task<FrontendValidationResult> ValidateAsync(string databasePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!File.Exists(databasePath))
        {
            return Task.FromResult(FrontendValidationResult.Failure([$"Archive database does not exist: {databasePath}"]));
        }

        using var connection = Sqlite.OpenReadOnly(databasePath);
        ValidateRequiredTables(connection, errors);
        if (errors.Count != 0)
        {
            return Task.FromResult(FrontendValidationResult.Failure(errors, warnings));
        }

        ValidateIntegrity(connection, errors);
        ValidateCounts(connection, errors, warnings);

        return Task.FromResult(errors.Count == 0
            ? FrontendValidationResult.Success(warnings)
            : FrontendValidationResult.Failure(errors, warnings));
    }

    private static void ValidateRequiredTables(SqliteConnection connection, ICollection<string> errors)
    {
        foreach (var table in RequiredTables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
            command.Parameters.AddWithValue("$name", table);
            if (Convert.ToInt64(command.ExecuteScalar() ?? 0) == 0)
            {
                errors.Add($"Required table is missing: {table}");
            }
        }
    }

    private static void ValidateIntegrity(SqliteConnection connection, ICollection<string> errors)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check;";
            var result = command.ExecuteScalar() as string;
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("PRAGMA integrity_check failed: " + result);
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_key_check;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                errors.Add(
                    "Foreign key violation: table="
                    + reader.GetString(0)
                    + " rowid="
                    + reader.GetValue(1)
                    + " parent="
                    + reader.GetString(2)
                    + " fkid="
                    + reader.GetValue(3));
            }
        }
    }

    private static void ValidateCounts(SqliteConnection connection, ICollection<string> errors, ICollection<string> warnings)
    {
        var pending = Count(
            connection,
            """
            SELECT COUNT(*)
            FROM frontend_capture
            WHERE replayed_utc IS NULL
              AND replay_error IS NULL;
            """);
        if (pending != 0)
        {
            errors.Add($"{pending} captures have not been replayed and do not have a recorded replay error.");
        }

        var contentMismatch = Count(
            connection,
            """
            SELECT COUNT(*)
            FROM frontend_content
            WHERE content_length_bytes != length(body);
            """);
        if (contentMismatch != 0)
        {
            errors.Add($"{contentMismatch} content rows have a body length mismatch.");
        }

        var captureContentMismatch = Count(
            connection,
            """
            SELECT COUNT(*)
            FROM frontend_capture c
            JOIN frontend_content b ON b.content_id = c.content_id
            WHERE c.replay_content_length_bytes != b.content_length_bytes
               OR c.replay_body_sha256 != b.body_sha256;
            """);
        if (captureContentMismatch != 0)
        {
            errors.Add($"{captureContentMismatch} captures disagree with their deduped content metadata.");
        }

        var replayErrorsWithoutBody = Count(connection, "SELECT COUNT(*) FROM frontend_capture WHERE replay_error IS NOT NULL AND content_id IS NULL;");
        if (replayErrorsWithoutBody != 0)
        {
            warnings.Add($"{replayErrorsWithoutBody} captures have recorded replay errors and no archived body.");
        }

        var replayErrorsWithBody = Count(connection, "SELECT COUNT(*) FROM frontend_capture WHERE replay_error IS NOT NULL AND content_id IS NOT NULL;");
        if (replayErrorsWithBody != 0)
        {
            warnings.Add($"{replayErrorsWithBody} captures have archived partial bodies with recorded replay read errors.");
        }

        var captures = Count(connection, "SELECT COUNT(*) FROM frontend_capture;");
        var contents = Count(connection, "SELECT COUNT(*) FROM frontend_content;");
        if (captures != 0 && contents == 0)
        {
            warnings.Add("Archive contains capture metadata but no downloaded content bodies.");
        }
    }

    private static long Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0);
    }
}
