using Microsoft.Data.Sqlite;

namespace OpenGameBuilder.Mgb.Archive.ClientArchiver;

public sealed class SqliteFrontendArchiveStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteFrontendArchiveStore(string path)
    {
        _connection = Sqlite.Open(path);
        Sqlite.ExecuteNonQuery(_connection, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY;");
    }

    public void Dispose() => _connection.Dispose();

    public void Initialize()
    {
        Sqlite.ExecuteNonQuery(_connection, SchemaSql);
        UpsertArchiveInfo("schema", "mgb-frontend-wayback-archive");
        UpsertArchiveInfo("schema_version", "2");
        UpsertArchiveInfo("content_scope", "Wayback CDX captures, raw replay headers and bodies");
    }

    public void ResetCapture()
    {
        Sqlite.ExecuteNonQuery(
            _connection,
            """
            DELETE FROM frontend_response_header;
            DELETE FROM frontend_seed_capture;
            DELETE FROM frontend_capture;
            DELETE FROM frontend_content;
            DELETE FROM frontend_resource;
            DELETE FROM frontend_exclude;
            DELETE FROM frontend_seed;
            DELETE FROM capture_state;
            """);
    }

    public void PrepareSeedFile(FrontendSeedFile seedFile, string seedsPath)
    {
        var existingHash = GetState("seed_file_sha256");
        if (existingHash is not null)
        {
            if (!string.Equals(existingHash, seedFile.Sha256, StringComparison.Ordinal))
            {
                throw new ArchiveFatalException(
                    "The existing in-progress frontend archive was created from a different seed file. "
                    + "Use --replace to start a new capture.");
            }

            ValidateExistingSeeds(seedFile.Seeds);
            ValidateExistingExcludes(seedFile.Excludes);
            return;
        }

        using var transaction = _connection.BeginTransaction();
        SetState("seed_file_sha256", seedFile.Sha256, transaction);
        SetState("seed_file_path", Path.GetFullPath(seedsPath), transaction);
        UpsertArchiveInfo("seed_file_sha256", seedFile.Sha256, transaction);
        UpsertArchiveInfo("seed_file_path", Path.GetFullPath(seedsPath), transaction);
        InsertSeeds(seedFile.Seeds, transaction);
        InsertExcludes(seedFile.Excludes, transaction);
        transaction.Commit();
    }

    private void InsertSeeds(IReadOnlyList<FrontendSeed> seeds, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO frontend_seed(line_number, seed_kind, seed_value, seed_text, cdx_search_url, cdx_match_type, created_utc)
            VALUES ($line_number, $seed_kind, $seed_value, $seed_text, $cdx_search_url, $cdx_match_type, $created_utc);
            """;
        AddParameter(command, "$line_number");
        AddParameter(command, "$seed_kind");
        AddParameter(command, "$seed_value");
        AddParameter(command, "$seed_text");
        AddParameter(command, "$cdx_search_url");
        AddParameter(command, "$cdx_match_type");
        AddParameter(command, "$created_utc");

        foreach (var seed in seeds)
        {
            command.Parameters["$line_number"].Value = seed.LineNumber;
            command.Parameters["$seed_kind"].Value = ToSeedKindText(seed.Kind);
            command.Parameters["$seed_value"].Value = seed.Value;
            command.Parameters["$seed_text"].Value = seed.RawText;
            command.Parameters["$cdx_search_url"].Value = WaybackCdxClient.BuildCdxSearchUrl(seed);
            command.Parameters["$cdx_match_type"].Value = (object?)ToCdxMatchType(seed.Kind) ?? DBNull.Value;
            command.Parameters["$created_utc"].Value = FormatUtc(DateTimeOffset.UtcNow);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<StoredSeed> GetIncompleteSeeds()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT seed_id, line_number, seed_kind, seed_value, seed_text, resume_key
            FROM frontend_seed
            WHERE completed_utc IS NULL
            ORDER BY seed_id;
            """;

        var seeds = new List<StoredSeed>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            seeds.Add(new StoredSeed(
                reader.GetInt64(0),
                new FrontendSeed(
                    reader.GetInt32(1),
                    FromSeedKindText(reader.GetString(2)),
                    reader.GetString(3),
                    reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return seeds;
    }

    public long GetCaptureCount() => Count("frontend_capture");

    public long GetDownloadedCaptureCount() => Convert.ToInt64(Sqlite.ExecuteScalar(
        _connection,
        "SELECT COUNT(*) FROM frontend_capture WHERE replayed_utc IS NOT NULL;") ?? 0);

    public long GetReplayErrorCount() => Convert.ToInt64(Sqlite.ExecuteScalar(
        _connection,
        "SELECT COUNT(*) FROM frontend_capture WHERE replay_error IS NOT NULL;") ?? 0);

    public long GetContentCount() => Count("frontend_content");

    public long ResetReplayErrors()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE frontend_capture
            SET replay_url = NULL,
                replayed_utc = NULL,
                replay_error = NULL
            WHERE replay_error IS NOT NULL
              AND content_id IS NULL;
            """;
        return command.ExecuteNonQuery();
    }

    public IReadOnlyList<PendingReplayCapture> GetPendingReplays(int take, IReadOnlyCollection<long>? excludedCaptureIds = null)
    {
        using var command = _connection.CreateCommand();
        var excluded = excludedCaptureIds is { Count: > 0 }
            ? excludedCaptureIds.Order().ToArray()
            : [];
        var excludedClause = string.Empty;
        if (excluded.Length != 0)
        {
            var placeholders = excluded.Select((_, index) => "$excluded" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            excludedClause = " AND capture_id NOT IN (" + string.Join(", ", placeholders) + ")";
            for (var index = 0; index < excluded.Length; index++)
            {
                command.Parameters.AddWithValue("$excluded" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), excluded[index]);
            }
        }

        command.CommandText =
            $"""
             SELECT capture_id, capture_timestamp, original_url
             FROM frontend_capture
             WHERE replayed_utc IS NULL
               AND replay_error IS NULL
             {excludedClause}
             ORDER BY capture_id
             LIMIT $take;
             """;
        command.Parameters.AddWithValue("$take", take);

        var captures = new List<PendingReplayCapture>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            captures.Add(new PendingReplayCapture(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }

        return captures;
    }

    public long GetPendingReplayCount()
    {
        return Convert.ToInt64(Sqlite.ExecuteScalar(
            _connection,
            """
            SELECT COUNT(*)
            FROM frontend_capture
            WHERE replayed_utc IS NULL
              AND replay_error IS NULL;
            """) ?? 0);
    }

    public long PruneResourcesWithOnlyErrorStatuses()
    {
        using var transaction = _connection.BeginTransaction();
        using var collect = _connection.CreateCommand();
        collect.Transaction = transaction;
        collect.CommandText =
            """
            SELECT r.resource_id
            FROM frontend_resource r
            JOIN frontend_capture c ON c.resource_id = r.resource_id
            GROUP BY r.resource_id
            HAVING COUNT(*) > 0
               AND SUM(CASE WHEN c.replayed_utc IS NOT NULL OR c.replay_error IS NOT NULL THEN 1 ELSE 0 END) = 0
               AND SUM(
                    CASE
                        WHEN c.cdx_status_code GLOB '[0-9][0-9][0-9]'
                         AND CAST(c.cdx_status_code AS INTEGER) BETWEEN 400 AND 599
                        THEN 1
                        ELSE 0
                    END
               ) = COUNT(*);
            """;

        var resourceIds = new List<long>();
        using (var reader = collect.ExecuteReader())
        {
            while (reader.Read())
            {
                resourceIds.Add(reader.GetInt64(0));
            }
        }

        if (resourceIds.Count == 0)
        {
            transaction.Commit();
            return 0;
        }

        foreach (var resourceId in resourceIds)
        {
            using (var deleteLinks = _connection.CreateCommand())
            {
                deleteLinks.Transaction = transaction;
                deleteLinks.CommandText =
                    """
                    DELETE FROM frontend_seed_capture
                    WHERE capture_id IN (
                        SELECT capture_id
                        FROM frontend_capture
                        WHERE resource_id = $resource_id
                    );
                    """;
                deleteLinks.Parameters.AddWithValue("$resource_id", resourceId);
                deleteLinks.ExecuteNonQuery();
            }

            using (var deleteCaptures = _connection.CreateCommand())
            {
                deleteCaptures.Transaction = transaction;
                deleteCaptures.CommandText = "DELETE FROM frontend_capture WHERE resource_id = $resource_id;";
                deleteCaptures.Parameters.AddWithValue("$resource_id", resourceId);
                deleteCaptures.ExecuteNonQuery();
            }

            using (var deleteResource = _connection.CreateCommand())
            {
                deleteResource.Transaction = transaction;
                deleteResource.CommandText = "DELETE FROM frontend_resource WHERE resource_id = $resource_id;";
                deleteResource.Parameters.AddWithValue("$resource_id", resourceId);
                deleteResource.ExecuteNonQuery();
            }
        }

        using (var updateSeeds = _connection.CreateCommand())
        {
            updateSeeds.Transaction = transaction;
            updateSeeds.CommandText =
                """
                UPDATE frontend_seed
                SET cdx_row_count = (
                    SELECT COUNT(*)
                    FROM frontend_seed_capture
                    WHERE frontend_seed_capture.seed_id = frontend_seed.seed_id
                );
                """;
            updateSeeds.ExecuteNonQuery();
        }

        transaction.Commit();
        return resourceIds.Count;
    }

    public void InsertCdxPage(long seedId, IReadOnlyList<CdxCapture> captures, string? resumeKey)
    {
        using var transaction = _connection.BeginTransaction();

        foreach (var capture in captures)
        {
            var resourceId = GetOrInsertResource(capture.OriginalUrl, transaction);
            var captureId = GetOrInsertCapture(resourceId, capture, transaction);
            LinkSeedCapture(seedId, captureId, transaction);
        }

        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE frontend_seed
                SET resume_key = $resume_key,
                    cdx_row_count = (SELECT COUNT(*) FROM frontend_seed_capture WHERE seed_id = $seed_id)
                WHERE seed_id = $seed_id;
                """;
            command.Parameters.AddWithValue("$seed_id", seedId);
            command.Parameters.AddWithValue("$resume_key", (object?)resumeKey ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void MarkSeedComplete(long seedId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE frontend_seed
            SET completed_utc = $completed_utc,
                resume_key = NULL,
                cdx_row_count = (SELECT COUNT(*) FROM frontend_seed_capture WHERE seed_id = $seed_id)
            WHERE seed_id = $seed_id;
            """;
        command.Parameters.AddWithValue("$seed_id", seedId);
        command.Parameters.AddWithValue("$completed_utc", FormatUtc(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    public void InsertReplay(long captureId, ReplayDownload download)
    {
        using var transaction = _connection.BeginTransaction();
        InsertReplay(captureId, download, transaction);
        transaction.Commit();
    }

    public void InsertDirectCapture(string originalUrl, string timestamp, ReplayDownload download)
    {
        using var transaction = _connection.BeginTransaction();
        var resourceId = GetOrInsertResource(originalUrl, transaction);
        var capture = new CdxCapture(
            timestamp,
            originalUrl,
            GetFirstHeader(download.Headers, "content-type"),
            download.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            download.BodySha256,
            download.Body.LongLength,
            GetFirstHeader(download.Headers, "location"));
        var captureId = GetOrInsertCapture(resourceId, capture, transaction);
        InsertReplay(captureId, download, transaction);
        transaction.Commit();
    }

    public void InsertDirectCaptureFailure(string originalUrl, string timestamp, Uri uri, string error)
    {
        using var transaction = _connection.BeginTransaction();
        var resourceId = GetOrInsertResource(originalUrl, transaction);
        var capture = new CdxCapture(timestamp, originalUrl, null, null, null, null, null);
        var captureId = GetOrInsertCapture(resourceId, capture, transaction);
        MarkReplayFailure(captureId, uri, error, transaction);
        transaction.Commit();
    }

    private void InsertReplay(long captureId, ReplayDownload download, SqliteTransaction transaction)
    {
        var contentId = GetOrInsertContent(download.BodySha256, download.Body, transaction);

        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE frontend_capture
                SET replay_url = $replay_url,
                    replay_status_code = $replay_status_code,
                    replay_reason_phrase = $replay_reason_phrase,
                    replayed_utc = $replayed_utc,
                    replay_error = $replay_error,
                    content_id = $content_id,
                    replay_content_length_bytes = $content_length_bytes,
                    replay_body_sha256 = $body_sha256
                WHERE capture_id = $capture_id;
                """;
            command.Parameters.AddWithValue("$capture_id", captureId);
            command.Parameters.AddWithValue("$replay_url", download.ReplayUri.ToString());
            command.Parameters.AddWithValue("$replay_status_code", download.StatusCode);
            command.Parameters.AddWithValue("$replay_reason_phrase", (object?)download.ReasonPhrase ?? DBNull.Value);
            command.Parameters.AddWithValue("$replayed_utc", FormatUtc(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$replay_error", (object?)download.BodyReadError ?? DBNull.Value);
            command.Parameters.AddWithValue("$content_id", contentId);
            command.Parameters.AddWithValue("$content_length_bytes", download.Body.LongLength);
            command.Parameters.AddWithValue("$body_sha256", download.BodySha256);
            command.ExecuteNonQuery();
        }

        DeleteReplayHeaders(captureId, transaction);
        InsertReplayHeaders(captureId, download.Headers, transaction);
    }

    public void MarkReplayFailure(long captureId, Uri replayUri, string error)
    {
        using var transaction = _connection.BeginTransaction();
        MarkReplayFailure(captureId, replayUri, error, transaction);
        transaction.Commit();
    }

    private void MarkReplayFailure(long captureId, Uri replayUri, string error, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE frontend_capture
            SET replay_url = $replay_url,
                replay_error = $replay_error,
                replayed_utc = $replayed_utc
            WHERE capture_id = $capture_id;
            """;
        command.Parameters.AddWithValue("$capture_id", captureId);
        command.Parameters.AddWithValue("$replay_url", replayUri.ToString());
        command.Parameters.AddWithValue("$replay_error", error);
        command.Parameters.AddWithValue("$replayed_utc", FormatUtc(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    public void DeleteCapture(long captureId)
    {
        using var transaction = _connection.BeginTransaction();
        long? resourceId = null;
        using (var select = _connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT resource_id FROM frontend_capture WHERE capture_id = $capture_id;";
            select.Parameters.AddWithValue("$capture_id", captureId);
            var value = select.ExecuteScalar();
            if (value is not null && value != DBNull.Value)
            {
                resourceId = Convert.ToInt64(value);
            }
        }

        using (var deleteHeaders = _connection.CreateCommand())
        {
            deleteHeaders.Transaction = transaction;
            deleteHeaders.CommandText = "DELETE FROM frontend_response_header WHERE capture_id = $capture_id;";
            deleteHeaders.Parameters.AddWithValue("$capture_id", captureId);
            deleteHeaders.ExecuteNonQuery();
        }

        using (var deleteLinks = _connection.CreateCommand())
        {
            deleteLinks.Transaction = transaction;
            deleteLinks.CommandText = "DELETE FROM frontend_seed_capture WHERE capture_id = $capture_id;";
            deleteLinks.Parameters.AddWithValue("$capture_id", captureId);
            deleteLinks.ExecuteNonQuery();
        }

        using (var deleteCapture = _connection.CreateCommand())
        {
            deleteCapture.Transaction = transaction;
            deleteCapture.CommandText = "DELETE FROM frontend_capture WHERE capture_id = $capture_id;";
            deleteCapture.Parameters.AddWithValue("$capture_id", captureId);
            deleteCapture.ExecuteNonQuery();
        }

        if (resourceId is not null)
        {
            using var deleteResource = _connection.CreateCommand();
            deleteResource.Transaction = transaction;
            deleteResource.CommandText =
                """
                DELETE FROM frontend_resource
                WHERE resource_id = $resource_id
                  AND NOT EXISTS (
                      SELECT 1
                      FROM frontend_capture
                      WHERE frontend_capture.resource_id = frontend_resource.resource_id
                  );
                """;
            deleteResource.Parameters.AddWithValue("$resource_id", resourceId.Value);
            deleteResource.ExecuteNonQuery();
        }

        using (var updateSeeds = _connection.CreateCommand())
        {
            updateSeeds.Transaction = transaction;
            updateSeeds.CommandText =
                """
                UPDATE frontend_seed
                SET cdx_row_count = (
                    SELECT COUNT(*)
                    FROM frontend_seed_capture
                    WHERE frontend_seed_capture.seed_id = frontend_seed.seed_id
                );
                """;
            updateSeeds.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void FinalizeArchiveInfo(FrontendArchiveOptions options)
    {
        using var transaction = _connection.BeginTransaction();
        UpsertArchiveInfo("capture_tool", "OpenGameBuilder.Mgb.Archive.ClientArchiver", transaction);
        UpsertArchiveInfo("created_utc", FormatUtc(DateTimeOffset.UtcNow), transaction);
        UpsertArchiveInfo("cdx_endpoint", options.CdxEndpoint.ToString(), transaction);
        UpsertArchiveInfo("wayback_endpoint", options.WaybackEndpoint.ToString(), transaction);
        UpsertArchiveInfo("seed_count", Count("frontend_seed").ToString(System.Globalization.CultureInfo.InvariantCulture), transaction);
        UpsertArchiveInfo("exclude_count", Count("frontend_exclude").ToString(System.Globalization.CultureInfo.InvariantCulture), transaction);
        UpsertArchiveInfo("resource_count", Count("frontend_resource").ToString(System.Globalization.CultureInfo.InvariantCulture), transaction);
        UpsertArchiveInfo("capture_count", Count("frontend_capture").ToString(System.Globalization.CultureInfo.InvariantCulture), transaction);
        UpsertArchiveInfo("content_count", Count("frontend_content").ToString(System.Globalization.CultureInfo.InvariantCulture), transaction);
        UpsertArchiveInfo("replayed_capture_count", GetDownloadedCaptureCount().ToString(System.Globalization.CultureInfo.InvariantCulture), transaction);
        UpsertArchiveInfo("replay_error_count", GetReplayErrorCount().ToString(System.Globalization.CultureInfo.InvariantCulture), transaction);
        transaction.Commit();
    }

    public void PrepareStandaloneFile()
    {
        Sqlite.ExecuteNonQuery(
            _connection,
            """
            PRAGMA optimize;
            PRAGMA wal_checkpoint(TRUNCATE);
            PRAGMA journal_mode = DELETE;
            """);
    }

    private void ValidateExistingSeeds(IReadOnlyList<FrontendSeed> seeds)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT line_number, seed_kind, seed_value, seed_text
            FROM frontend_seed
            ORDER BY seed_id;
            """;

        var existing = new List<FrontendSeed>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                existing.Add(new FrontendSeed(
                    reader.GetInt32(0),
                    FromSeedKindText(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }

        if (existing.Count != seeds.Count || existing.Where((seed, index) => seed != seeds[index]).Any())
        {
            throw new ArchiveFatalException(
                "The existing in-progress frontend archive has a seed list that does not match the current seed file. "
                + "Use --replace to start a new capture.");
        }
    }

    private void ValidateExistingExcludes(IReadOnlyList<FrontendExclude> excludes)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT line_number, exclude_kind, exclude_value, exclude_text, canonical_prefix, host_path_prefix, match_text
            FROM frontend_exclude
            ORDER BY exclude_id;
            """;

        var existing = new List<FrontendExclude>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                existing.Add(new FrontendExclude(
                    reader.GetInt32(0),
                    FromExcludeKindText(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6)));
            }
        }

        if (existing.Count != excludes.Count || existing.Where((exclude, index) => exclude != excludes[index]).Any())
        {
            throw new ArchiveFatalException(
                "The existing in-progress frontend archive has exclude prefixes that do not match the current seed file. "
                + "Use --replace to start a new capture.");
        }
    }

    private void InsertExcludes(IReadOnlyList<FrontendExclude> excludes, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO frontend_exclude(line_number, exclude_kind, exclude_value, exclude_text, canonical_prefix, host_path_prefix, match_text)
            VALUES ($line_number, $exclude_kind, $exclude_value, $exclude_text, $canonical_prefix, $host_path_prefix, $match_text);
            """;
        AddParameter(command, "$line_number");
        AddParameter(command, "$exclude_kind");
        AddParameter(command, "$exclude_value");
        AddParameter(command, "$exclude_text");
        AddParameter(command, "$canonical_prefix");
        AddParameter(command, "$host_path_prefix");
        AddParameter(command, "$match_text");

        foreach (var exclude in excludes)
        {
            command.Parameters["$line_number"].Value = exclude.LineNumber;
            command.Parameters["$exclude_kind"].Value = ToExcludeKindText(exclude.Kind);
            command.Parameters["$exclude_value"].Value = exclude.Value;
            command.Parameters["$exclude_text"].Value = exclude.RawText;
            command.Parameters["$canonical_prefix"].Value = exclude.CanonicalPrefix;
            command.Parameters["$host_path_prefix"].Value = exclude.HostPathPrefix;
            command.Parameters["$match_text"].Value = exclude.MatchText;
            command.ExecuteNonQuery();
        }
    }

    private long GetOrInsertResource(string originalUrl, SqliteTransaction transaction)
    {
        var canonical = UrlCanonicalizer.Canonicalize(originalUrl);
        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT OR IGNORE INTO frontend_resource(canonical_url, sample_original_url)
                VALUES ($canonical_url, $sample_original_url);
                """;
            insert.Parameters.AddWithValue("$canonical_url", canonical);
            insert.Parameters.AddWithValue("$sample_original_url", originalUrl);
            insert.ExecuteNonQuery();
        }

        using var select = _connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT resource_id FROM frontend_resource WHERE canonical_url = $canonical_url;";
        select.Parameters.AddWithValue("$canonical_url", canonical);
        return Convert.ToInt64(select.ExecuteScalar() ?? throw new ArchiveFatalException($"Could not insert resource '{canonical}'."));
    }

    private long GetOrInsertCapture(long resourceId, CdxCapture capture, SqliteTransaction transaction)
    {
        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO frontend_capture(
                    resource_id, capture_timestamp, original_url, cdx_mimetype, cdx_status_code,
                    cdx_digest, cdx_length, cdx_redirect_url, cdx_raw_json
                )
                VALUES (
                    $resource_id, $capture_timestamp, $original_url, $cdx_mimetype, $cdx_status_code,
                    $cdx_digest, $cdx_length, $cdx_redirect_url, $cdx_raw_json
                )
                ON CONFLICT(capture_timestamp, original_url) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$resource_id", resourceId);
            insert.Parameters.AddWithValue("$capture_timestamp", capture.Timestamp);
            insert.Parameters.AddWithValue("$original_url", capture.OriginalUrl);
            insert.Parameters.AddWithValue("$cdx_mimetype", (object?)capture.MimeType ?? DBNull.Value);
            insert.Parameters.AddWithValue("$cdx_status_code", (object?)capture.StatusCode ?? DBNull.Value);
            insert.Parameters.AddWithValue("$cdx_digest", (object?)capture.Digest ?? DBNull.Value);
            insert.Parameters.AddWithValue("$cdx_length", (object?)capture.Length ?? DBNull.Value);
            insert.Parameters.AddWithValue("$cdx_redirect_url", (object?)capture.RedirectUrl ?? DBNull.Value);
            insert.Parameters.AddWithValue("$cdx_raw_json", System.Text.Json.JsonSerializer.Serialize(capture));
            insert.ExecuteNonQuery();
        }

        using var select = _connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            """
            SELECT capture_id
            FROM frontend_capture
            WHERE capture_timestamp = $capture_timestamp
              AND original_url = $original_url;
            """;
        select.Parameters.AddWithValue("$capture_timestamp", capture.Timestamp);
        select.Parameters.AddWithValue("$original_url", capture.OriginalUrl);
        return Convert.ToInt64(select.ExecuteScalar() ?? throw new ArchiveFatalException($"Could not insert capture '{capture.OriginalUrl}' at {capture.Timestamp}."));
    }

    private void LinkSeedCapture(long seedId, long captureId, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO frontend_seed_capture(seed_id, capture_id)
            VALUES ($seed_id, $capture_id);
            """;
        command.Parameters.AddWithValue("$seed_id", seedId);
        command.Parameters.AddWithValue("$capture_id", captureId);
        command.ExecuteNonQuery();
    }

    private long GetOrInsertContent(string bodySha256, byte[] body, SqliteTransaction transaction)
    {
        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT OR IGNORE INTO frontend_content(body_sha256, content_length_bytes, body)
                VALUES ($body_sha256, $content_length_bytes, $body);
                """;
            insert.Parameters.AddWithValue("$body_sha256", bodySha256);
            insert.Parameters.AddWithValue("$content_length_bytes", body.LongLength);
            insert.Parameters.Add("$body", SqliteType.Blob).Value = body;
            insert.ExecuteNonQuery();
        }

        using var select = _connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT content_id FROM frontend_content WHERE body_sha256 = $body_sha256;";
        select.Parameters.AddWithValue("$body_sha256", bodySha256);
        return Convert.ToInt64(select.ExecuteScalar() ?? throw new ArchiveFatalException($"Could not insert content '{bodySha256}'."));
    }

    private void DeleteReplayHeaders(long captureId, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM frontend_response_header WHERE capture_id = $capture_id;";
        command.Parameters.AddWithValue("$capture_id", captureId);
        command.ExecuteNonQuery();
    }

    private void InsertReplayHeaders(long captureId, IReadOnlyList<ReplayHeader> headers, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO frontend_response_header(capture_id, header_order, name, value)
            VALUES ($capture_id, $header_order, $name, $value);
            """;
        AddParameter(command, "$capture_id");
        AddParameter(command, "$header_order");
        AddParameter(command, "$name");
        AddParameter(command, "$value");

        for (var index = 0; index < headers.Count; index++)
        {
            command.Parameters["$capture_id"].Value = captureId;
            command.Parameters["$header_order"].Value = index;
            command.Parameters["$name"].Value = headers[index].Name;
            command.Parameters["$value"].Value = headers[index].Value;
            command.ExecuteNonQuery();
        }
    }

    private long Count(string tableName)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + tableName + ";";
        return Convert.ToInt64(command.ExecuteScalar() ?? 0);
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

    private void UpsertArchiveInfo(string name, string value, SqliteTransaction? transaction = null)
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

    private static void AddParameter(SqliteCommand command, string name)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        command.Parameters.Add(parameter);
    }

    private static string ToSeedKindText(FrontendSeedKind kind) =>
        kind switch
        {
            FrontendSeedKind.Domain => "domain",
            FrontendSeedKind.Prefix => "prefix",
            FrontendSeedKind.Url => "url",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static string ToExcludeKindText(FrontendExcludeKind kind) =>
        kind switch
        {
            FrontendExcludeKind.Prefix => "prefix",
            FrontendExcludeKind.Contains => "contains",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static string? ToCdxMatchType(FrontendSeedKind kind) =>
        kind switch
        {
            FrontendSeedKind.Domain => "domain",
            FrontendSeedKind.Prefix => "prefix",
            _ => null
        };

    private static FrontendSeedKind FromSeedKindText(string value) =>
        value switch
        {
            "domain" => FrontendSeedKind.Domain,
            "prefix" => FrontendSeedKind.Prefix,
            "url" => FrontendSeedKind.Url,
            _ => throw new ArchiveFatalException($"Unknown stored seed kind '{value}'.")
        };

    private static FrontendExcludeKind FromExcludeKindText(string value) =>
        value switch
        {
            "prefix" => FrontendExcludeKind.Prefix,
            "contains" => FrontendExcludeKind.Contains,
            _ => throw new ArchiveFatalException($"Unknown stored exclude kind '{value}'.")
        };

    private static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static string? GetFirstHeader(IReadOnlyList<ReplayHeader> headers, string name) =>
        headers.FirstOrDefault(header => header.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private const string SchemaSql =
        """
        PRAGMA encoding = 'UTF-8';
        PRAGMA foreign_keys = ON;
        PRAGMA application_id = 0x4D474246;
        PRAGMA user_version = 1;

        CREATE TABLE IF NOT EXISTS archive_info (
            name TEXT PRIMARY KEY COLLATE BINARY,
            value TEXT NOT NULL
        ) STRICT, WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS capture_state (
            name TEXT PRIMARY KEY COLLATE BINARY,
            value TEXT NOT NULL
        ) STRICT, WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS frontend_seed (
            seed_id INTEGER PRIMARY KEY,
            line_number INTEGER NOT NULL CHECK (line_number > 0),
            seed_kind TEXT NOT NULL COLLATE BINARY CHECK (seed_kind IN ('domain', 'prefix', 'url')),
            seed_value TEXT NOT NULL COLLATE BINARY,
            seed_text TEXT NOT NULL COLLATE BINARY,
            cdx_search_url TEXT NOT NULL COLLATE BINARY,
            cdx_match_type TEXT NULL COLLATE BINARY CHECK (cdx_match_type IS NULL OR cdx_match_type IN ('domain', 'prefix')),
            resume_key TEXT NULL COLLATE BINARY,
            cdx_row_count INTEGER NOT NULL DEFAULT 0 CHECK (cdx_row_count >= 0),
            created_utc TEXT NOT NULL COLLATE BINARY,
            completed_utc TEXT NULL COLLATE BINARY,
            UNIQUE(line_number, seed_kind, seed_value)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS frontend_exclude (
            exclude_id INTEGER PRIMARY KEY,
            line_number INTEGER NOT NULL CHECK (line_number > 0),
            exclude_kind TEXT NOT NULL COLLATE BINARY CHECK (exclude_kind IN ('prefix', 'contains')),
            exclude_value TEXT NOT NULL COLLATE BINARY,
            exclude_text TEXT NOT NULL COLLATE BINARY,
            canonical_prefix TEXT NOT NULL COLLATE BINARY,
            host_path_prefix TEXT NOT NULL COLLATE BINARY,
            match_text TEXT NOT NULL COLLATE BINARY,
            UNIQUE(line_number, exclude_kind, exclude_value)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS frontend_resource (
            resource_id INTEGER PRIMARY KEY,
            canonical_url TEXT NOT NULL COLLATE BINARY,
            sample_original_url TEXT NOT NULL COLLATE BINARY,
            UNIQUE(canonical_url)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS frontend_content (
            content_id INTEGER PRIMARY KEY,
            body_sha256 TEXT NOT NULL COLLATE BINARY CHECK (
                length(body_sha256) = 64
                AND body_sha256 NOT GLOB '*[^0-9a-f]*'
            ),
            content_length_bytes INTEGER NOT NULL CHECK (content_length_bytes >= 0),
            body BLOB NOT NULL,
            CHECK (content_length_bytes = length(body)),
            UNIQUE(body_sha256)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS frontend_capture (
            capture_id INTEGER PRIMARY KEY,
            resource_id INTEGER NOT NULL
                REFERENCES frontend_resource(resource_id)
                ON DELETE RESTRICT,
            capture_timestamp TEXT NOT NULL COLLATE BINARY CHECK (
                length(capture_timestamp) = 14
                AND capture_timestamp NOT GLOB '*[^0-9]*'
            ),
            original_url TEXT NOT NULL COLLATE BINARY,
            cdx_mimetype TEXT NULL COLLATE BINARY,
            cdx_status_code TEXT NULL COLLATE BINARY,
            cdx_digest TEXT NULL COLLATE BINARY,
            cdx_length INTEGER NULL,
            cdx_redirect_url TEXT NULL COLLATE BINARY,
            cdx_raw_json TEXT NOT NULL,
            replay_url TEXT NULL COLLATE BINARY,
            replay_status_code INTEGER NULL CHECK (replay_status_code IS NULL OR replay_status_code BETWEEN 100 AND 599),
            replay_reason_phrase TEXT NULL,
            replayed_utc TEXT NULL COLLATE BINARY,
            replay_error TEXT NULL,
            content_id INTEGER NULL
                REFERENCES frontend_content(content_id)
                ON DELETE RESTRICT,
            replay_content_length_bytes INTEGER NULL CHECK (
                replay_content_length_bytes IS NULL OR replay_content_length_bytes >= 0
            ),
            replay_body_sha256 TEXT NULL COLLATE BINARY CHECK (
                replay_body_sha256 IS NULL OR (
                    length(replay_body_sha256) = 64
                    AND replay_body_sha256 NOT GLOB '*[^0-9a-f]*'
                )
            ),
            CHECK (
                (
                    content_id IS NULL
                    AND replay_content_length_bytes IS NULL
                    AND replay_body_sha256 IS NULL
                )
                OR
                (
                    content_id IS NOT NULL
                    AND replay_content_length_bytes IS NOT NULL
                    AND replay_body_sha256 IS NOT NULL
                )
            ),
            UNIQUE(capture_timestamp, original_url)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS frontend_seed_capture (
            seed_id INTEGER NOT NULL
                REFERENCES frontend_seed(seed_id)
                ON DELETE RESTRICT,
            capture_id INTEGER NOT NULL
                REFERENCES frontend_capture(capture_id)
                ON DELETE RESTRICT,
            PRIMARY KEY(seed_id, capture_id)
        ) STRICT, WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS frontend_response_header (
            capture_id INTEGER NOT NULL
                REFERENCES frontend_capture(capture_id)
                ON DELETE RESTRICT,
            header_order INTEGER NOT NULL CHECK (header_order >= 0),
            name TEXT NOT NULL COLLATE BINARY,
            value TEXT NOT NULL,
            PRIMARY KEY(capture_id, header_order),
            CHECK (length(name) > 0)
        ) STRICT, WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS ix_frontend_capture_resource_timestamp
            ON frontend_capture(resource_id, capture_timestamp);

        CREATE INDEX IF NOT EXISTS ix_frontend_capture_cdx_digest
            ON frontend_capture(cdx_digest)
            WHERE cdx_digest IS NOT NULL;

        CREATE INDEX IF NOT EXISTS ix_frontend_capture_content
            ON frontend_capture(content_id)
            WHERE content_id IS NOT NULL;

        CREATE INDEX IF NOT EXISTS ix_frontend_header_name
            ON frontend_response_header(name, value);

        CREATE VIEW IF NOT EXISTS v_frontend_capture_lookup AS
        SELECT
            r.canonical_url,
            c.capture_id,
            c.capture_timestamp,
            c.original_url,
            c.cdx_mimetype,
            c.cdx_status_code,
            c.cdx_digest,
            c.cdx_length,
            c.cdx_redirect_url,
            c.replay_url,
            c.replay_status_code,
            c.replay_reason_phrase,
            c.replayed_utc,
            c.replay_error,
            c.replay_content_length_bytes,
            c.replay_body_sha256,
            c.content_id
        FROM frontend_capture c
        JOIN frontend_resource r ON r.resource_id = c.resource_id;
        """;
}

public sealed record StoredSeed(long SeedId, FrontendSeed Seed, string? ResumeKey);
