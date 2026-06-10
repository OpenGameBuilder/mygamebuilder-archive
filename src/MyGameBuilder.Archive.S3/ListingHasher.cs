using System.Security.Cryptography;
using System.Text;

namespace MyGameBuilder.Archive.S3;

public sealed class ListingHasher : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public void Add(ListedS3Entry entry)
    {
        AddField(entry.SourceListOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddField(entry.Key);
        AddField(entry.RawVersionId ?? string.Empty);
        AddField(entry.IsLatest ? "1" : "0");
        AddField(entry.IsDeleteMarker ? "1" : "0");
        AddField(entry.LastModifiedUtc.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        AddField(entry.ETag ?? string.Empty);
        AddField(entry.ContentLengthBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        AddField(entry.StorageClass ?? string.Empty);
        AddField(entry.SourceListXml);
        AddSeparator('\n');
    }

    public string GetHashAndReset() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

    public void Dispose() => _hash.Dispose();

    private void AddField(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        _hash.AppendData(bytes);
        AddSeparator('\u001f');
    }

    private void AddSeparator(char separator)
    {
        Span<byte> buffer = stackalloc byte[4];
        var count = Encoding.UTF8.GetBytes(stackalloc[] { separator }, buffer);
        _hash.AppendData(buffer[..count]);
    }
}
