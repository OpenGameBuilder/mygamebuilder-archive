using System.Collections.ObjectModel;

namespace MyGameBuilder.Archive.S3;

public sealed record ArchiveOptions(
    string Bucket,
    Uri Endpoint,
    string OutputPath,
    string WorkDirectory,
    int Concurrency,
    bool Resume,
    bool Replace);

public sealed record ValidateOptions(string DatabasePath);

public sealed record SimplifyUnversionedOptions(string DatabasePath, string? OutputPath, bool Replace);

public sealed record UnversionedArchiveAnalysis(
    long ObjectCount,
    long EntryCount,
    long LiveEntryCount,
    long DeleteMarkerCount,
    long NonNullVersionIdCount,
    long ObjectsWithoutExactlyOneEntryCount,
    long NonLatestEntryCount,
    long NonZeroVersionOrderCount,
    long LiveEntryWithoutBodyCount,
    long CaseInsensitiveKeyCollisionGroupCount)
{
    public bool IsUnversioned =>
        ObjectCount == EntryCount
        && ObjectCount == LiveEntryCount
        && DeleteMarkerCount == 0
        && NonNullVersionIdCount == 0
        && ObjectsWithoutExactlyOneEntryCount == 0
        && NonLatestEntryCount == 0
        && NonZeroVersionOrderCount == 0
        && LiveEntryWithoutBodyCount == 0;
}

public sealed record ListedS3Entry(
    long SourceListOrdinal,
    string Key,
    string? RawVersionId,
    bool IsLatest,
    bool IsDeleteMarker,
    DateTimeOffset LastModifiedUtc,
    string? ETag,
    long? ContentLengthBytes,
    string? StorageClass,
    string SourceListXml)
{
    public string? ArchiveVersionId => string.Equals(RawVersionId, "null", StringComparison.Ordinal) ? null : RawVersionId;
}

public sealed record ListingSummary(
    string Fingerprint,
    long EntryCount,
    long LiveEntryCount,
    long DeleteMarkerCount,
    long ListedContentBytes);

public sealed record ListVersionsPage(
    IReadOnlyList<ListedS3Entry> Entries,
    bool IsTruncated,
    string? NextKeyMarker,
    string? NextVersionIdMarker);

public sealed record DownloadedObject(
    long SourceListOrdinal,
    string Key,
    string? RawVersionId,
    string? ContentType,
    string ETag,
    DateTimeOffset LastModifiedUtc,
    long ContentLengthBytes,
    byte[] Body,
    string BodySha256,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Headers);

public sealed record MgbKeyPart(string UserName, string ProjectName, string PieceType, string PieceName);

public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public static ValidationResult Success(IReadOnlyList<string>? warnings = null) =>
        new(true, Array.Empty<string>(), warnings ?? Array.Empty<string>());

    public static ValidationResult Failure(IReadOnlyList<string> errors, IReadOnlyList<string>? warnings = null) =>
        new(false, errors, warnings ?? Array.Empty<string>());
}

public sealed class HeaderBag : ReadOnlyDictionary<string, IReadOnlyList<string>>
{
    public HeaderBag(IDictionary<string, IReadOnlyList<string>> dictionary)
        : base(dictionary)
    {
    }
}
