using System.Text;
using System.Text.Json;

namespace MyGameBuilder.Archive.Frontend;

public static class UrlExporter
{
    public static async Task ExportAsync(string databasePath, string outputPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        if (Path.GetExtension(outputPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            await ExportJsonAsync(databasePath, outputPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ExportCsvAsync(databasePath, outputPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExportJsonAsync(string databasePath, string outputPath, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        using var connection = Sqlite.OpenReadOnly(databasePath);
        using var command = CreateExportCommand(connection);
        using var reader = command.ExecuteReader();

        writer.WriteStartArray();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            writer.WriteString("raw_text", reader.GetString(0));
            WriteNullableString(writer, "resolved_url", reader, 1);
            WriteNullableString(writer, "resolved_canonical_url", reader, 2);
            writer.WriteNumber("occurrence_count", reader.GetInt64(3));
            writer.WriteNumber("source_capture_id", reader.GetInt64(4));
            writer.WriteString("source_timestamp", reader.GetString(5));
            writer.WriteString("source_original_url", reader.GetString(6));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExportCsvAsync(string databasePath, string outputPath, CancellationToken cancellationToken)
    {
        await using var output = new StreamWriter(new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8);
        using var connection = Sqlite.OpenReadOnly(databasePath);
        using var command = CreateExportCommand(connection);
        using var reader = command.ExecuteReader();

        await output.WriteLineAsync("raw_text,resolved_url,resolved_canonical_url,occurrence_count,source_capture_id,source_timestamp,source_original_url").ConfigureAwait(false);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await output.WriteLineAsync(string.Join(
                ",",
                Csv(reader.GetString(0)),
                Csv(reader.IsDBNull(1) ? string.Empty : reader.GetString(1)),
                Csv(reader.IsDBNull(2) ? string.Empty : reader.GetString(2)),
                reader.GetInt64(3).ToString(System.Globalization.CultureInfo.InvariantCulture),
                reader.GetInt64(4).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Csv(reader.GetString(5)),
                Csv(reader.GetString(6)))).ConfigureAwait(false);
        }
    }

    private static Microsoft.Data.Sqlite.SqliteCommand CreateExportCommand(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                u.raw_text,
                u.resolved_url,
                u.resolved_canonical_url,
                u.occurrence_count,
                u.first_capture_id,
                c.capture_timestamp,
                c.original_url
            FROM frontend_discovered_url u
            JOIN frontend_capture c ON c.capture_id = u.first_capture_id
            ORDER BY coalesce(u.resolved_canonical_url, u.resolved_url, u.raw_text), u.raw_text;
            """;
        return command;
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, Microsoft.Data.Sqlite.SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, reader.GetString(ordinal));
        }
    }

    private static string Csv(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
