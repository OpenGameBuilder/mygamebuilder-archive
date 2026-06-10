using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace MyGameBuilder.Archive.S3.Redactor.Tests;

internal sealed record TestEntry(
    long EntryId,
    long ObjectId,
    string UserName,
    string ProjectName,
    string PieceType,
    string PieceName,
    string ContentType,
    byte[] Body)
{
    public string KeyText => $"{UserName}/{ProjectName}/{PieceType}/{PieceName}";

    public static TestEntry Tile(long entryId, string userName, string projectName, string pieceName, byte[] body) =>
        new(entryId, entryId, userName, projectName, "tile", pieceName, "image/png", body);

    public static TestEntry Actor(long entryId, string userName, string projectName, string pieceName, byte[] body) =>
        new(entryId, entryId, userName, projectName, "actor", pieceName, "text/plain", body);

    public static TestEntry Map(long entryId, string userName, string projectName, string pieceName, byte[] body) =>
        new(entryId, entryId, userName, projectName, "map", pieceName, "text/plain", body);

    public static TestEntry Screenshot(long entryId, string userName, string projectName, string pieceName, byte[] body) =>
        new(entryId, entryId, userName, projectName, "screenshot", pieceName, "image/png", body);
}

internal static class TestFiles
{
    public static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mgb-redactor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static byte[] PngWithUniqueColors(int count)
    {
        using var image = new Image<Rgba32>(count, 1);
        for (var x = 0; x < count; x++)
        {
            image[x, 0] = new Rgba32((byte)(x * 17), (byte)(255 - x * 11), (byte)(x * 7), 255);
        }

        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        return stream.ToArray();
    }

    public static byte[] ActorReferencingTile(string tileName)
    {
        var xml = $"<actor><animationTable>face north|{tileName}|no effect</animationTable></actor>";
        return CompressWriteUtf(xml.Replace("<", "{{{", StringComparison.Ordinal).Replace(">", "}}}", StringComparison.Ordinal));
    }

    public static byte[] MapReferencingActor(string actorName)
    {
        using var stream = new MemoryStream();
        WriteInt32(stream, 4);
        for (var layer = 0; layer < 4; layer++)
        {
            WriteInt32(stream, 1);
            WriteUtf(stream, layer == 1 ? actorName : string.Empty);
        }

        return Compress(stream.ToArray());
    }

    public static void CreateArchive(string path, IReadOnlyList<TestEntry> entries)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        Execute(
            connection,
            """
            CREATE TABLE archive_info (
                name TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE s3_object (
                object_id INTEGER PRIMARY KEY,
                key_text TEXT NOT NULL,
                key_utf8 BLOB NOT NULL
            );

            CREATE TABLE mgb_key_part (
                object_id INTEGER PRIMARY KEY,
                user_name TEXT NOT NULL,
                project_name TEXT NOT NULL,
                piece_type TEXT NOT NULL,
                piece_name TEXT NOT NULL
            );

            CREATE TABLE s3_entry (
                entry_id INTEGER PRIMARY KEY,
                object_id INTEGER NOT NULL,
                is_delete_marker INTEGER NOT NULL,
                content_type TEXT NOT NULL,
                content_length_bytes INTEGER NOT NULL,
                body_sha256 TEXT NOT NULL,
                body BLOB NOT NULL
            );
            """);

        foreach (var entry in entries)
        {
            Execute(
                connection,
                """
                INSERT INTO s3_object(object_id, key_text, key_utf8)
                VALUES ($object_id, $key_text, $key_utf8);
                INSERT INTO mgb_key_part(object_id, user_name, project_name, piece_type, piece_name)
                VALUES ($object_id, $user_name, $project_name, $piece_type, $piece_name);
                INSERT INTO s3_entry(entry_id, object_id, is_delete_marker, content_type, content_length_bytes, body_sha256, body)
                VALUES ($entry_id, $object_id, 0, $content_type, $content_length_bytes, $body_sha256, $body);
                """,
                command =>
                {
                    command.Parameters.AddWithValue("$object_id", entry.ObjectId);
                    command.Parameters.AddWithValue("$key_text", entry.KeyText);
                    command.Parameters.Add("$key_utf8", SqliteType.Blob).Value = System.Text.Encoding.UTF8.GetBytes(entry.KeyText);
                    command.Parameters.AddWithValue("$user_name", entry.UserName);
                    command.Parameters.AddWithValue("$project_name", entry.ProjectName);
                    command.Parameters.AddWithValue("$piece_type", entry.PieceType);
                    command.Parameters.AddWithValue("$piece_name", entry.PieceName);
                    command.Parameters.AddWithValue("$entry_id", entry.EntryId);
                    command.Parameters.AddWithValue("$content_type", entry.ContentType);
                    command.Parameters.AddWithValue("$content_length_bytes", entry.Body.Length);
                    command.Parameters.AddWithValue("$body_sha256", PngRedactor.Sha256Hex(entry.Body));
                    command.Parameters.Add("$body", SqliteType.Blob).Value = entry.Body;
                });
        }

        Execute(
            connection,
            "INSERT INTO archive_info(name, value) VALUES ('listed_content_bytes', (SELECT sum(content_length_bytes) FROM s3_entry));");
    }

    private static byte[] CompressWriteUtf(string value)
    {
        using var stream = new MemoryStream();
        WriteUtf(stream, value);
        return Compress(stream.ToArray());
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(bytes);
        }

        return output.ToArray();
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUtf(Stream stream, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)bytes.Length));
        stream.Write(length);
        stream.Write(bytes);
    }

    private static void Execute(SqliteConnection connection, string sql, Action<SqliteCommand>? configure = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        command.ExecuteNonQuery();
    }
}
