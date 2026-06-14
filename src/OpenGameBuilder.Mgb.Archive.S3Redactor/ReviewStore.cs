using Microsoft.Data.Sqlite;

namespace OpenGameBuilder.Mgb.Archive.S3Redactor;

public sealed class ReviewStore
{
    private const int ReviewWindowBack = 4;
    private const int ReviewWindowAhead = 80;
    private readonly string _path;
    private readonly object _writeGate = new();

    public ReviewStore(string path)
    {
        _path = System.IO.Path.GetFullPath(path);
    }

    public string Path => _path;

    public void Initialize(string archivePath, int threshold)
    {
        lock (_writeGate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path) ?? Environment.CurrentDirectory);
            using var connection = Open();
            ArchiveDb.Execute(connection, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;");
            ArchiveDb.Execute(
                connection,
                """
                CREATE TABLE IF NOT EXISTS review_state (
                    name TEXT PRIMARY KEY COLLATE BINARY,
                    value TEXT NOT NULL
                ) STRICT, WITHOUT ROWID;

                CREATE TABLE IF NOT EXISTS review_candidate (
                    entry_id INTEGER PRIMARY KEY,
                    object_id INTEGER NOT NULL,
                    key_text TEXT NOT NULL COLLATE BINARY,
                    user_name TEXT NULL COLLATE BINARY,
                    project_name TEXT NULL COLLATE BINARY,
                    piece_type TEXT NULL COLLATE BINARY,
                    piece_name TEXT NULL COLLATE BINARY,
                    width INTEGER NOT NULL CHECK (width > 0),
                    height INTEGER NOT NULL CHECK (height > 0),
                    unique_color_count INTEGER NOT NULL CHECK (unique_color_count > 0),
                    status TEXT NOT NULL DEFAULT 'unreviewed' CHECK (status IN ('unreviewed', 'approved', 'redacted')),
                    decided_utc TEXT NULL COLLATE BINARY
                ) STRICT;

                CREATE INDEX IF NOT EXISTS ix_review_candidate_status
                    ON review_candidate(status, entry_id);

                CREATE TABLE IF NOT EXISTS review_scanned_entry (
                    entry_id INTEGER PRIMARY KEY,
                    accepted INTEGER NOT NULL CHECK (accepted IN (0, 1)),
                    unique_color_count INTEGER NULL CHECK (unique_color_count IS NULL OR unique_color_count > 0),
                    error TEXT NULL,
                    scanned_utc TEXT NOT NULL COLLATE BINARY
                ) STRICT;
                """);

            var existingArchivePath = GetState(connection, "archive_path");
            var fullArchivePath = System.IO.Path.GetFullPath(archivePath);
            if (existingArchivePath is not null && !string.Equals(existingArchivePath, fullArchivePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Review database belongs to '{existingArchivePath}', not '{fullArchivePath}'. Use --review to choose another sidecar.");
            }

            var existingThreshold = GetState(connection, "unique_color_threshold");
            if (existingThreshold is not null && !string.Equals(existingThreshold, threshold.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Review database was created with threshold {existingThreshold}, not {threshold}. Use --review to choose another sidecar.");
            }

            SetState(connection, "archive_path", fullArchivePath);
            SetState(connection, "unique_color_threshold", threshold.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (GetState(connection, "current_index") is null)
            {
                SetState(connection, "current_index", "0");
            }
        }
    }

    public void SetScanTotal(int total)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            SetState(connection, "scan_total", total.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public long GetLastScannedEntryId()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT coalesce(max(entry_id), 0) FROM review_scanned_entry;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void RecordScanned(PngArchiveEntry entry, PngInspectionResult? inspection, bool accepted, string? error)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (accepted && inspection is not null)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO review_candidate (
                        entry_id, object_id, key_text, user_name, project_name, piece_type,
                        piece_name, width, height, unique_color_count
                    )
                    VALUES (
                        $entry_id, $object_id, $key_text, $user_name, $project_name, $piece_type,
                        $piece_name, $width, $height, $unique_color_count
                    )
                    ON CONFLICT(entry_id) DO UPDATE SET
                        object_id = excluded.object_id,
                        key_text = excluded.key_text,
                        user_name = excluded.user_name,
                        project_name = excluded.project_name,
                        piece_type = excluded.piece_type,
                        piece_name = excluded.piece_name,
                        width = excluded.width,
                        height = excluded.height,
                        unique_color_count = excluded.unique_color_count;
                    """;
                AddParameter(insert, "$entry_id");
                AddParameter(insert, "$object_id");
                AddParameter(insert, "$key_text");
                AddParameter(insert, "$user_name");
                AddParameter(insert, "$project_name");
                AddParameter(insert, "$piece_type");
                AddParameter(insert, "$piece_name");
                AddParameter(insert, "$width");
                AddParameter(insert, "$height");
                AddParameter(insert, "$unique_color_count");
                insert.Parameters["$entry_id"].Value = entry.EntryId;
                insert.Parameters["$object_id"].Value = entry.ObjectId;
                insert.Parameters["$key_text"].Value = entry.KeyText;
                insert.Parameters["$user_name"].Value = (object?)entry.UserName ?? DBNull.Value;
                insert.Parameters["$project_name"].Value = (object?)entry.ProjectName ?? DBNull.Value;
                insert.Parameters["$piece_type"].Value = (object?)entry.PieceType ?? DBNull.Value;
                insert.Parameters["$piece_name"].Value = (object?)entry.PieceName ?? DBNull.Value;
                insert.Parameters["$width"].Value = inspection.Width;
                insert.Parameters["$height"].Value = inspection.Height;
                insert.Parameters["$unique_color_count"].Value = inspection.VisibleUniqueColorCount;
                insert.ExecuteNonQuery();
            }

            using (var scanned = connection.CreateCommand())
            {
                scanned.Transaction = transaction;
                scanned.CommandText =
                    """
                    INSERT INTO review_scanned_entry(entry_id, accepted, unique_color_count, error, scanned_utc)
                    VALUES ($entry_id, $accepted, $unique_color_count, $error, $scanned_utc)
                    ON CONFLICT(entry_id) DO UPDATE SET
                        accepted = excluded.accepted,
                        unique_color_count = excluded.unique_color_count,
                        error = excluded.error,
                        scanned_utc = excluded.scanned_utc;
                    """;
                scanned.Parameters.AddWithValue("$entry_id", entry.EntryId);
                scanned.Parameters.AddWithValue("$accepted", accepted ? 1 : 0);
                scanned.Parameters.AddWithValue("$unique_color_count", (object?)inspection?.VisibleUniqueColorCount ?? DBNull.Value);
                scanned.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
                scanned.Parameters.AddWithValue("$scanned_utc", FormatUtc(DateTimeOffset.UtcNow));
                scanned.ExecuteNonQuery();
            }

            if (IsScanComplete(connection, transaction))
            {
                SetState(connection, "candidate_scan_completed_utc", FormatUtc(DateTimeOffset.UtcNow), transaction);
            }

            transaction.Commit();
        }
    }

    public ReviewStateDto GetStateDto(RedactorOptions options, int? requestedCurrentIndex = null)
    {
        using var connection = Open();
        var counts = GetCounts(connection);
        var currentIndex = Math.Clamp(requestedCurrentIndex ?? GetCurrentIndex(connection), 0, Math.Max(0, counts.Total - 1));
        if (counts.Total == 0)
        {
            currentIndex = 0;
        }

        var windowStart = Math.Max(0, currentIndex - ReviewWindowBack);
        IReadOnlyList<ReviewCandidateDto> window = counts.Total == 0
            ? Array.Empty<ReviewCandidateDto>()
            : GetCandidateDtos(connection, windowStart, ReviewWindowBack + 1 + ReviewWindowAhead);
        var previous = currentIndex > 0 ? window.FirstOrDefault(candidate => candidate.Ordinal == currentIndex) : null;
        var current = counts.Total == 0 ? null : window.FirstOrDefault(candidate => candidate.Ordinal == currentIndex + 1);
        var next = currentIndex < counts.Total - 1 ? window.FirstOrDefault(candidate => candidate.Ordinal == currentIndex + 2) : null;
        return new ReviewStateDto(
            true,
            null,
            System.IO.Path.GetFullPath(options.ArchivePath!),
            _path,
            options.EffectiveOutputPath,
            options.UniqueColorThreshold,
            GetScanProgress(connection),
            currentIndex,
            counts,
            current,
            previous,
            next,
            window);
    }

    public ReviewCandidate? GetCandidate(long entryId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT entry_id, object_id, key_text, user_name, project_name, piece_type, piece_name,
                   width, height, unique_color_count, status
            FROM review_candidate
            WHERE entry_id = $entry_id;
            """;
        command.Parameters.AddWithValue("$entry_id", entryId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadCandidate(reader) : null;
    }

    public void SetDecision(long entryId, string status)
    {
        if (!ReviewStatus.IsValid(status) || string.Equals(status, ReviewStatus.Unreviewed, StringComparison.Ordinal))
        {
            throw new ArgumentException("Status must be approved or redacted.", nameof(status));
        }

        lock (_writeGate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE review_candidate
                    SET status = $status,
                        decided_utc = $decided_utc
                    WHERE entry_id = $entry_id;
                    """;
                command.Parameters.AddWithValue("$status", status);
                command.Parameters.AddWithValue("$decided_utc", FormatUtc(DateTimeOffset.UtcNow));
                command.Parameters.AddWithValue("$entry_id", entryId);
                command.ExecuteNonQuery();
            }

            var index = GetIndexOf(connection, transaction, entryId);
            var total = GetCounts(connection, transaction).Total;
            if (index is not null && total > 0)
            {
                SetCurrentIndex(connection, Math.Min(index.Value + 1, total - 1), transaction);
            }

            transaction.Commit();
        }
    }

    public void ApplyBatch(IReadOnlyList<DecisionRequest> decisions, int currentIndex)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE review_candidate
                SET status = $status,
                    decided_utc = $decided_utc
                WHERE entry_id = $entry_id;
                """;
            command.Parameters.AddWithValue("$status", string.Empty);
            command.Parameters.AddWithValue("$decided_utc", string.Empty);
            command.Parameters.AddWithValue("$entry_id", 0L);

            foreach (var decision in decisions)
            {
                if (!ReviewStatus.IsValid(decision.Status) || string.Equals(decision.Status, ReviewStatus.Unreviewed, StringComparison.Ordinal))
                {
                    throw new ArgumentException("Statuses must be approved or redacted.", nameof(decisions));
                }

                command.Parameters["$status"].Value = decision.Status;
                command.Parameters["$decided_utc"].Value = FormatUtc(DateTimeOffset.UtcNow);
                command.Parameters["$entry_id"].Value = decision.EntryId;
                command.ExecuteNonQuery();
            }

            var counts = GetCounts(connection, transaction);
            SetCurrentIndex(connection, Math.Clamp(currentIndex, 0, Math.Max(0, counts.Total - 1)), transaction);
            transaction.Commit();
        }
    }

    public void Move(int delta)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            var counts = GetCounts(connection);
            var current = GetCurrentIndex(connection);
            var next = Math.Clamp(current + delta, 0, Math.Max(0, counts.Total - 1));
            SetCurrentIndex(connection, next);
        }
    }

    public IReadOnlyList<ReviewCandidate> GetRedactedCandidates()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT entry_id, object_id, key_text, user_name, project_name, piece_type, piece_name,
                   width, height, unique_color_count, status
            FROM review_candidate
            WHERE status = 'redacted'
            ORDER BY entry_id;
            """;

        var candidates = new List<ReviewCandidate>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            candidates.Add(ReadCandidate(reader));
        }

        return candidates;
    }

    public ReviewCounts GetCounts()
    {
        using var connection = Open();
        return GetCounts(connection);
    }

    public void SetSubmitState(string name, string value)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            SetState(connection, name, value);
        }
    }

    private SqliteConnection Open()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        ArchiveDb.Execute(connection, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 15000;");
        return connection;
    }

    private static ReviewCounts GetCounts(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                COUNT(*),
                SUM(CASE WHEN status <> 'unreviewed' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'approved' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'redacted' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'unreviewed' THEN 1 ELSE 0 END)
            FROM review_candidate;
            """;
        using var reader = command.ExecuteReader();
        reader.Read();
        return new ReviewCounts(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            reader.IsDBNull(4) ? 0 : reader.GetInt32(4));
    }

    private static ScanProgress GetScanProgress(SqliteConnection connection)
    {
        var totalValue = GetState(connection, "scan_total");
        var total = int.TryParse(totalValue, out var parsedTotal) ? parsedTotal : 0;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM review_scanned_entry;";
        var processed = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        return new ScanProgress(processed, total, total > 0 && processed >= total);
    }

    private static bool IsScanComplete(SqliteConnection connection, SqliteTransaction transaction)
    {
        var totalValue = GetState(connection, "scan_total", transaction);
        if (!int.TryParse(totalValue, out var total) || total == 0)
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM review_scanned_entry;";
        var processed = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        return processed >= total;
    }

    private static ReviewCandidate? GetCandidateAt(SqliteConnection connection, int index)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT entry_id, object_id, key_text, user_name, project_name, piece_type, piece_name,
                   width, height, unique_color_count, status
            FROM review_candidate
            ORDER BY entry_id
            LIMIT 1 OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$offset", index);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadCandidate(reader) : null;
    }

    private static IReadOnlyList<ReviewCandidateDto> GetCandidateDtos(SqliteConnection connection, int startIndex, int take)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT entry_id, object_id, key_text, user_name, project_name, piece_type, piece_name,
                   width, height, unique_color_count, status
            FROM review_candidate
            ORDER BY entry_id
            LIMIT $take OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$take", take);
        command.Parameters.AddWithValue("$offset", startIndex);

        var candidates = new List<ReviewCandidateDto>();
        using var reader = command.ExecuteReader();
        var index = startIndex;
        while (reader.Read())
        {
            candidates.Add(ToDto(ReadCandidate(reader), index));
            index++;
        }

        return candidates;
    }

    private static int? GetIndexOf(SqliteConnection connection, SqliteTransaction transaction, long entryId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT rn - 1
            FROM (
                SELECT entry_id, row_number() OVER (ORDER BY entry_id) AS rn
                FROM review_candidate
            )
            WHERE entry_id = $entry_id;
            """;
        command.Parameters.AddWithValue("$entry_id", entryId);
        var value = command.ExecuteScalar();
        return value is null ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int GetCurrentIndex(SqliteConnection connection)
    {
        var value = GetState(connection, "current_index");
        return int.TryParse(value, out var index) ? index : 0;
    }

    private static void SetCurrentIndex(SqliteConnection connection, int index, SqliteTransaction? transaction = null) =>
        SetState(connection, "current_index", index.ToString(System.Globalization.CultureInfo.InvariantCulture), transaction);

    private static string? GetState(SqliteConnection connection, string name, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM review_state WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() as string;
    }

    private static void SetState(SqliteConnection connection, string name, string value, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO review_state(name, value)
            VALUES ($name, $value)
            ON CONFLICT(name) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static ReviewCandidate ReadCandidate(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetString(10));

    private static ReviewCandidateDto ToDto(ReviewCandidate candidate, int index) =>
        new(
            candidate.EntryId,
            index + 1,
            candidate.KeyText,
            candidate.UserName,
            candidate.ProjectName,
            candidate.PieceType,
            candidate.PieceName,
            candidate.Width,
            candidate.Height,
            candidate.UniqueColorCount,
            candidate.Status,
            "/image/" + candidate.EntryId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static void AddParameter(SqliteCommand command, string name)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        command.Parameters.Add(parameter);
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
