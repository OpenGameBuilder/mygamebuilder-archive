namespace OpenGameBuilder.Mgb.Archive.S3Redactor;

public static class ReviewStatus
{
    public const string Unreviewed = "unreviewed";
    public const string Approved = "approved";
    public const string Redacted = "redacted";

    public static bool IsValid(string value) =>
        string.Equals(value, Unreviewed, StringComparison.Ordinal) ||
        string.Equals(value, Approved, StringComparison.Ordinal) ||
        string.Equals(value, Redacted, StringComparison.Ordinal);
}

public sealed record MgbPieceKey(
    string UserName,
    string ProjectName,
    string PieceType,
    string PieceName);

public sealed record PngArchiveEntry(
    long EntryId,
    long ObjectId,
    string KeyText,
    string? UserName,
    string? ProjectName,
    string? PieceType,
    string? PieceName,
    string? ContentType,
    byte[] Body);

public sealed record ReviewCandidate(
    long EntryId,
    long ObjectId,
    string KeyText,
    string? UserName,
    string? ProjectName,
    string? PieceType,
    string? PieceName,
    int Width,
    int Height,
    int UniqueColorCount,
    string Status);

public sealed record ReviewCounts(
    int Total,
    int Reviewed,
    int Approved,
    int Redacted,
    int Unreviewed);

public sealed record ScanProgress(
    int Processed,
    int Total,
    bool Complete);

public sealed record ReviewStateDto(
    bool Ready,
    string? Message,
    string ArchivePath,
    string ReviewPath,
    string OutputPath,
    int UniqueColorThreshold,
    ScanProgress Scan,
    int CurrentIndex,
    ReviewCounts Counts,
    ReviewCandidateDto? Current,
    ReviewCandidateDto? Previous,
    ReviewCandidateDto? Next,
    IReadOnlyList<ReviewCandidateDto> ReviewWindow);

public sealed record ReviewCandidateDto(
    long EntryId,
    int Ordinal,
    string KeyText,
    string? UserName,
    string? ProjectName,
    string? PieceType,
    string? PieceName,
    int Width,
    int Height,
    int UniqueColorCount,
    string Status,
    string ImageUrl);

public sealed record DecisionRequest(long EntryId, string Status);

public sealed record ReviewBatchRequest(
    IReadOnlyList<DecisionRequest> Decisions,
    int CurrentIndex);

public sealed record MoveRequest(int Delta);

public sealed record SubmitRequest(string? OutputPath);

public sealed record SubmitResult(
    string OutputPath,
    int ManualRedactedCount,
    int PropagatedScreenshotCount,
    int TotalRedactedCount);
