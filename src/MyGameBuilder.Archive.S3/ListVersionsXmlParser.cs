using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace MyGameBuilder.Archive.S3;

public static class ListVersionsXmlParser
{
    private static readonly XNamespace S3Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    public static ListVersionsPage Parse(string xml, long firstOrdinal)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (Exception ex)
        {
            throw new ArchiveFatalException($"Malformed ListObjectVersions XML: {ex.Message}");
        }

        var root = document.Root;
        if (root is null || root.Name != S3Namespace + "ListVersionsResult")
        {
            throw new ArchiveFatalException("ListObjectVersions response did not contain a ListVersionsResult root.");
        }

        var urlEncoded = string.Equals(ReadOptional(root, "EncodingType"), "url", StringComparison.OrdinalIgnoreCase);
        var entries = new List<ListedS3Entry>();
        var ordinal = firstOrdinal;
        foreach (var element in root.Elements())
        {
            if (element.Name == S3Namespace + "Version")
            {
                entries.Add(ParseEntry(element, ordinal++, isDeleteMarker: false, urlEncoded));
            }
            else if (element.Name == S3Namespace + "DeleteMarker")
            {
                entries.Add(ParseEntry(element, ordinal++, isDeleteMarker: true, urlEncoded));
            }
        }

        var isTruncated = string.Equals(ReadOptional(root, "IsTruncated"), "true", StringComparison.OrdinalIgnoreCase);
        var nextKeyMarker = DecodeIfNeeded(ReadOptional(root, "NextKeyMarker"), urlEncoded);
        var nextVersionIdMarker = ReadOptional(root, "NextVersionIdMarker");

        if (isTruncated && string.IsNullOrEmpty(nextKeyMarker))
        {
            throw new ArchiveFatalException("ListObjectVersions response was truncated but did not include NextKeyMarker.");
        }

        return new ListVersionsPage(entries, isTruncated, nextKeyMarker, nextVersionIdMarker);
    }

    private static ListedS3Entry ParseEntry(XElement element, long ordinal, bool isDeleteMarker, bool urlEncoded)
    {
        var key = DecodeIfNeeded(ReadRequired(element, "Key"), urlEncoded)
            ?? throw new ArchiveFatalException($"ListObjectVersions response has an empty key at list ordinal {ordinal}.");
        var keyLengthBytes = Encoding.UTF8.GetByteCount(key);
        if (keyLengthBytes is < 1 or > 1024)
        {
            throw new ArchiveFatalException($"Invalid S3 key length at list ordinal {ordinal}.");
        }

        var rawVersionId = ReadRequired(element, "VersionId");
        if (!bool.TryParse(ReadRequired(element, "IsLatest"), out var isLatest))
        {
            throw new ArchiveFatalException($"Invalid IsLatest value for key '{key}' at list ordinal {ordinal}.");
        }

        if (!DateTimeOffset.TryParse(
                ReadRequired(element, "LastModified"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var lastModified))
        {
            throw new ArchiveFatalException($"Invalid LastModified value for key '{key}' at list ordinal {ordinal}.");
        }

        var sourceListXml = element.ToString(SaveOptions.DisableFormatting);

        if (isDeleteMarker)
        {
            return new ListedS3Entry(
                ordinal,
                key,
                rawVersionId,
                isLatest,
                IsDeleteMarker: true,
                lastModified,
                ETag: null,
                ContentLengthBytes: null,
                StorageClass: null,
                sourceListXml);
        }

        var sizeText = ReadRequired(element, "Size");
        if (!long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size < 0)
        {
            throw new ArchiveFatalException($"Invalid object size '{sizeText}' for key '{key}'.");
        }

        return new ListedS3Entry(
            ordinal,
            key,
            rawVersionId,
            isLatest,
            IsDeleteMarker: false,
            lastModified,
            NormalizeETag(ReadRequired(element, "ETag")),
            size,
            ReadOptional(element, "StorageClass"),
            sourceListXml);
    }

    private static string ReadRequired(XElement parent, string localName)
    {
        var value = ReadOptional(parent, localName);
        if (value is null)
        {
            throw new ArchiveFatalException($"ListObjectVersions response is missing required element '{localName}'.");
        }

        return value;
    }

    private static string? ReadOptional(XElement parent, string localName) =>
        parent.Element(S3Namespace + localName)?.Value;

    public static string NormalizeETag(string etag) => etag.Trim().Trim('"');

    private static string? DecodeIfNeeded(string? value, bool urlEncoded) =>
        value is null || !urlEncoded ? value : WebUtility.UrlDecode(value);
}
