using Microsoft.Data.Sqlite;

namespace MyGameBuilder.Archive.S3.Redactor;

public sealed class ArchiveDb
{
    private readonly string _path;

    public ArchiveDb(string path)
    {
        _path = System.IO.Path.GetFullPath(path);
    }

    public string Path => _path;

    public SqliteConnection OpenReadOnly()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return connection;
    }

    public SqliteConnection OpenReadWrite()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return connection;
    }

    public int CountManualReviewPngEntries()
    {
        using var connection = OpenReadOnly();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM s3_entry e
            LEFT JOIN mgb_key_part m ON m.object_id = e.object_id
            WHERE e.is_delete_marker = 0
              AND e.body IS NOT NULL
              AND (
                  lower(coalesce(e.content_type, '')) = 'image/png'
                  OR substr(e.body, 1, 8) = x'89504E470D0A1A0A'
              )
              AND coalesce(m.piece_type, '') <> 'screenshot';
            """;

        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<PngArchiveEntry> GetManualReviewPngEntriesAfter(long entryId, int take)
    {
        using var connection = OpenReadOnly();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                e.entry_id,
                e.object_id,
                o.key_text,
                m.user_name,
                m.project_name,
                m.piece_type,
                m.piece_name,
                e.content_type,
                e.body
            FROM s3_entry e
            JOIN s3_object o ON o.object_id = e.object_id
            LEFT JOIN mgb_key_part m ON m.object_id = e.object_id
            WHERE e.is_delete_marker = 0
              AND e.body IS NOT NULL
              AND (
                  lower(coalesce(e.content_type, '')) = 'image/png'
                  OR substr(e.body, 1, 8) = x'89504E470D0A1A0A'
              )
              AND coalesce(m.piece_type, '') <> 'screenshot'
              AND e.entry_id > $entry_id
            ORDER BY e.entry_id
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$entry_id", entryId);
        command.Parameters.AddWithValue("$take", take);

        var entries = new List<PngArchiveEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ReadPngEntry(reader));
        }

        return entries;
    }

    public byte[] GetBody(long entryId)
    {
        using var connection = OpenReadOnly();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM s3_entry WHERE entry_id = $entry_id AND is_delete_marker = 0;";
        command.Parameters.AddWithValue("$entry_id", entryId);
        return command.ExecuteScalar() as byte[]
            ?? throw new InvalidOperationException($"Entry {entryId} does not have a live body.");
    }

    public IReadOnlyList<PngArchiveEntry> GetEntriesByPieceType(string pieceType)
    {
        using var connection = OpenReadOnly();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                e.entry_id,
                e.object_id,
                o.key_text,
                m.user_name,
                m.project_name,
                m.piece_type,
                m.piece_name,
                e.content_type,
                e.body
            FROM s3_entry e
            JOIN s3_object o ON o.object_id = e.object_id
            JOIN mgb_key_part m ON m.object_id = e.object_id
            WHERE e.is_delete_marker = 0
              AND e.body IS NOT NULL
              AND m.piece_type = $piece_type
            ORDER BY e.entry_id;
            """;
        command.Parameters.AddWithValue("$piece_type", pieceType);

        var entries = new List<PngArchiveEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ReadPngEntry(reader));
        }

        return entries;
    }

    public void BackupTo(string outputPath)
    {
        var fullOutputPath = System.IO.Path.GetFullPath(outputPath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullOutputPath) ?? Environment.CurrentDirectory);
        using var source = OpenReadOnly();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = fullOutputPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        };

        using var destination = new SqliteConnection(builder.ToString());
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static PngArchiveEntry ReadPngEntry(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            (byte[])reader["body"]);

    public static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
