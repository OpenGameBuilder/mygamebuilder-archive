using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml.Linq;

namespace OpenGameBuilder.Mgb.Archive.S3Redactor;

public static class MgbDecoders
{
    public static IReadOnlySet<string> ReadActorTileReferences(byte[] body)
    {
        var text = ReadCompressedWriteUtf(body).Replace("{{{", "<", StringComparison.Ordinal).Replace("}}}", ">", StringComparison.Ordinal);
        var document = XDocument.Parse(text);
        var table = document.Descendants("animationTable").FirstOrDefault()?.Value;
        if (string.IsNullOrWhiteSpace(table))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in table.Split('#', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = row.Split('|');
            if (fields.Length >= 2 && !string.IsNullOrWhiteSpace(fields[1]))
            {
                names.Add(fields[1]);
            }
        }

        return names;
    }

    public static IReadOnlySet<string> ReadMapActorReferences(byte[] body)
    {
        var bytes = DecompressZlib(body);
        var offset = 0;
        var layerCount = ReadInt32(bytes, ref offset);
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var layer = 0; layer < layerCount; layer++)
        {
            var cellCount = ReadInt32(bytes, ref offset);
            for (var cell = 0; cell < cellCount; cell++)
            {
                var value = ReadUtf(bytes, ref offset);
                if (layer < 3 && !string.IsNullOrWhiteSpace(value))
                {
                    names.Add(value);
                }
            }
        }

        return names;
    }

    private static string ReadCompressedWriteUtf(byte[] body)
    {
        var bytes = DecompressZlib(body);
        var offset = 0;
        return ReadUtf(bytes, ref offset);
    }

    private static byte[] DecompressZlib(byte[] body)
    {
        using var input = new MemoryStream(body);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static int ReadInt32(byte[] bytes, ref int offset)
    {
        EnsureAvailable(bytes, offset, 4);
        var value = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
        offset += 4;
        return value;
    }

    private static string ReadUtf(byte[] bytes, ref int offset)
    {
        EnsureAvailable(bytes, offset, 2);
        var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
        offset += 2;
        EnsureAvailable(bytes, offset, length);
        var value = System.Text.Encoding.UTF8.GetString(bytes, offset, length);
        offset += length;
        return value;
    }

    private static void EnsureAvailable(byte[] bytes, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > bytes.Length)
        {
            throw new InvalidDataException("MGB payload ended before the expected field was complete.");
        }
    }
}
