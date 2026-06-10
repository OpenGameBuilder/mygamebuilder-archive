using System.Collections.ObjectModel;

namespace MyGameBuilder.Archive.Frontend;

public enum FrontendSeedKind
{
    Domain,
    Prefix,
    Url
}

public sealed record FrontendArchiveOptions(
    string SeedsPath,
    string OutputPath,
    string WorkDirectory,
    int Concurrency,
    bool Resume,
    bool Replace,
    Uri CdxEndpoint,
    Uri WaybackEndpoint);

public sealed record ValidateOptions(string DatabasePath);

public sealed record ExportUrlsOptions(string DatabasePath, string OutputPath);

public sealed record FrontendSeed(
    int LineNumber,
    FrontendSeedKind Kind,
    string Value,
    string RawText);

public sealed record FrontendExclude(
    int LineNumber,
    string Value,
    string RawText,
    string CanonicalPrefix,
    string HostPathPrefix);

public sealed record FrontendSeedFile(
    IReadOnlyList<FrontendSeed> Seeds,
    IReadOnlyList<FrontendExclude> Excludes,
    string Sha256);

public sealed record CdxCapture(
    string Timestamp,
    string OriginalUrl,
    string? MimeType,
    string? StatusCode,
    string? Digest,
    long? Length,
    string? RedirectUrl);

public sealed record CdxPage(IReadOnlyList<CdxCapture> Captures, string? ResumeKey);

public sealed record PendingReplayCapture(
    long CaptureId,
    string Timestamp,
    string OriginalUrl);

public sealed record ReplayHeader(string Name, string Value);

public sealed record ReplayDownload(
    Uri ReplayUri,
    int StatusCode,
    string? ReasonPhrase,
    byte[] Body,
    string BodySha256,
    IReadOnlyList<ReplayHeader> Headers);

public sealed record UrlMatch(string RawText, string? ResolvedUrl, string? ResolvedCanonicalUrl);

public sealed record FrontendValidationResult(bool IsValid, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public static FrontendValidationResult Success(IReadOnlyList<string>? warnings = null) =>
        new(true, Array.Empty<string>(), warnings ?? Array.Empty<string>());

    public static FrontendValidationResult Failure(IReadOnlyList<string> errors, IReadOnlyList<string>? warnings = null) =>
        new(false, errors, warnings ?? Array.Empty<string>());
}

public sealed class HeaderBag : ReadOnlyCollection<ReplayHeader>
{
    public HeaderBag(IList<ReplayHeader> list)
        : base(list)
    {
    }
}
