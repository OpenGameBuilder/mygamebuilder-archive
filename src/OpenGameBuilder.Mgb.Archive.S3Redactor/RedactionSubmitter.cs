using Microsoft.Data.Sqlite;

namespace OpenGameBuilder.Mgb.Archive.S3Redactor;

public sealed class RedactionSubmitter
{
    private readonly ArchiveDb _sourceArchive;
    private readonly ReviewStore _reviewStore;
    private readonly ScreenshotPropagation _propagation;

    public RedactionSubmitter(ArchiveDb sourceArchive, ReviewStore reviewStore, ScreenshotPropagation propagation)
    {
        _sourceArchive = sourceArchive;
        _reviewStore = reviewStore;
        _propagation = propagation;
    }

    public SubmitResult Submit(string outputPath, int threshold)
    {
        var counts = _reviewStore.GetCounts();
        if (counts.Unreviewed != 0)
        {
            throw new InvalidOperationException("Review is not complete.");
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        if (File.Exists(fullOutputPath))
        {
            throw new InvalidOperationException($"Output already exists: {fullOutputPath}");
        }

        var redactedCandidates = _reviewStore.GetRedactedCandidates();
        var manualEntryIds = redactedCandidates.Select(static c => c.EntryId).ToHashSet();
        var screenshotEntryIds = _propagation.FindScreenshotEntryIds(redactedCandidates);
        var allEntryIds = manualEntryIds.Concat(screenshotEntryIds).Distinct().Order().ToArray();
        var tempPath = fullOutputPath + ".tmp";

        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        _reviewStore.SetSubmitState("submit_started_utc", FormatUtc(DateTimeOffset.UtcNow));
        _reviewStore.SetSubmitState("submit_output_path", fullOutputPath);

        _sourceArchive.BackupTo(tempPath);
        ApplyRedactions(tempPath, allEntryIds, redactedCandidates.Count, screenshotEntryIds.Count, threshold);
        SqliteConnection.ClearAllPools();
        File.Move(tempPath, fullOutputPath);

        _reviewStore.SetSubmitState("submit_completed_utc", FormatUtc(DateTimeOffset.UtcNow));
        return new SubmitResult(fullOutputPath, redactedCandidates.Count, screenshotEntryIds.Count, allEntryIds.Length);
    }

    private static void ApplyRedactions(string outputPath, IReadOnlyList<long> entryIds, int manualCount, int screenshotCount, int threshold)
    {
        var archive = new ArchiveDb(outputPath);
        using var connection = archive.OpenReadWrite();
        ArchiveDb.Execute(connection, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;");
        using var transaction = connection.BeginTransaction();

        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT body FROM s3_entry WHERE entry_id = $entry_id AND is_delete_marker = 0;";
        select.Parameters.Add("$entry_id", SqliteType.Integer);

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE s3_entry
            SET body = $body,
                content_length_bytes = $content_length_bytes,
                body_sha256 = $body_sha256
            WHERE entry_id = $entry_id;
            """;
        update.Parameters.Add("$body", SqliteType.Blob);
        update.Parameters.Add("$content_length_bytes", SqliteType.Integer);
        update.Parameters.Add("$body_sha256", SqliteType.Text);
        update.Parameters.Add("$entry_id", SqliteType.Integer);

        foreach (var entryId in entryIds)
        {
            select.Parameters["$entry_id"].Value = entryId;
            var body = select.ExecuteScalar() as byte[];
            if (body is null)
            {
                continue;
            }

            var blackBody = PngRedactor.CreateBlackPng(body);
            update.Parameters["$body"].Value = blackBody;
            update.Parameters["$content_length_bytes"].Value = blackBody.Length;
            update.Parameters["$body_sha256"].Value = PngRedactor.Sha256Hex(blackBody);
            update.Parameters["$entry_id"].Value = entryId;
            update.ExecuteNonQuery();
        }

        UpsertArchiveInfo(connection, transaction, "redaction_tool", "OpenGameBuilder.Mgb.Archive.S3Redactor");
        UpsertArchiveInfo(connection, transaction, "redaction_completed_utc", FormatUtc(DateTimeOffset.UtcNow));
        UpsertArchiveInfo(connection, transaction, "redaction_unique_color_threshold", threshold.ToString(System.Globalization.CultureInfo.InvariantCulture));
        UpsertArchiveInfo(connection, transaction, "redaction_manual_entry_count", manualCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        UpsertArchiveInfo(connection, transaction, "redaction_propagated_screenshot_entry_count", screenshotCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        UpsertArchiveInfo(connection, transaction, "redaction_total_entry_count", entryIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        UpsertArchiveInfo(
            connection,
            transaction,
            "listed_content_bytes",
            ScalarString(connection, transaction, "SELECT coalesce(sum(content_length_bytes), 0) FROM s3_entry WHERE is_delete_marker = 0;"));

        transaction.Commit();
        ArchiveDb.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode = DELETE; PRAGMA optimize;");
    }

    private static void UpsertArchiveInfo(SqliteConnection connection, SqliteTransaction transaction, string name, string value)
    {
        using var command = connection.CreateCommand();
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

    private static string ScalarString(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "0";
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
