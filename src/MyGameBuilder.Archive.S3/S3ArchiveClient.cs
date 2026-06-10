using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace MyGameBuilder.Archive.S3;

public sealed class S3ArchiveClient(HttpClient httpClient, Uri endpoint, string bucket, ILogger<S3ArchiveClient> logger)
{
    private readonly Uri _bucketBaseUri = BuildBucketBaseUri(endpoint, bucket);
    private readonly string _bucketBaseUriText = BuildBucketBaseUriText(endpoint, bucket);
    private readonly ILogger<S3ArchiveClient> _logger = logger;

    public async Task<ListVersionsPage> ListObjectVersionsAsync(
        string? keyMarker,
        string? versionIdMarker,
        long firstOrdinal,
        CancellationToken cancellationToken)
    {
        var query = new List<string> { "versions", "max-keys=1000", "encoding-type=url" };
        if (!string.IsNullOrEmpty(keyMarker))
        {
            query.Add("key-marker=" + Uri.EscapeDataString(keyMarker));
        }

        if (!string.IsNullOrEmpty(versionIdMarker))
        {
            query.Add("version-id-marker=" + Uri.EscapeDataString(versionIdMarker));
        }

        var uri = new Uri(_bucketBaseUri, "?" + string.Join("&", query));
        _logger.LogDebug("Listing S3 object versions from {Uri}", uri);
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ArchiveFatalException($"ListObjectVersions failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body)}");
        }

        return ListVersionsXmlParser.Parse(body, firstOrdinal);
    }

    public async Task<DownloadedObject> DownloadObjectAsync(ListedS3Entry entry, CancellationToken cancellationToken)
    {
        if (entry.IsDeleteMarker)
        {
            throw new ArgumentException("Delete markers do not have bodies.", nameof(entry));
        }

        var uri = BuildObjectUri(entry);
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"GetObject failed for '{entry.Key}' ({entry.RawVersionId ?? "no version"}) with HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(text)}",
                null,
                response.StatusCode);
        }

        var headers = CaptureHeaders(response);
        var responseETag = NormalizeResponseETag(headers, "etag");
        if (!string.IsNullOrEmpty(entry.ETag) && !string.Equals(responseETag, entry.ETag, StringComparison.Ordinal))
        {
            throw new ArchiveFatalException($"ETag mismatch for '{entry.Key}': listed '{entry.ETag}', GET returned '{responseETag}'.");
        }

        var responseLength = response.Content.Headers.ContentLength;
        if (!responseLength.HasValue)
        {
            throw new ArchiveFatalException($"GetObject response for '{entry.Key}' did not include Content-Length.");
        }

        if (entry.ContentLengthBytes is { } listedLength && responseLength.Value != listedLength)
        {
            throw new ArchiveFatalException($"Content-Length mismatch for '{entry.Key}': listed {listedLength}, GET returned {responseLength.Value}.");
        }

        var lastModified = response.Content.Headers.LastModified ?? response.Headers.Date;
        if (lastModified is null)
        {
            throw new ArchiveFatalException($"GetObject response for '{entry.Key}' did not include Last-Modified.");
        }

        if (lastModified.Value.ToUnixTimeSeconds() != entry.LastModifiedUtc.ToUnixTimeSeconds())
        {
            throw new ArchiveFatalException($"Last-Modified mismatch for '{entry.Key}': listed '{entry.LastModifiedUtc}', GET returned '{lastModified}'.");
        }

        ValidateReturnedVersionId(entry, headers);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream(entry.ContentLengthBytes is > 0 and <= int.MaxValue ? (int)entry.ContentLengthBytes.Value : 0);
        using var sha256 = SHA256.Create();
        await using (var crypto = new CryptoStream(memory, sha256, CryptoStreamMode.Write, leaveOpen: true))
        {
            await stream.CopyToAsync(crypto, cancellationToken).ConfigureAwait(false);
        }

        var body = memory.ToArray();
        if (entry.ContentLengthBytes is { } expectedLength && body.LongLength != expectedLength)
        {
            throw new ArchiveFatalException($"Downloaded byte count mismatch for '{entry.Key}': listed {expectedLength}, downloaded {body.LongLength}.");
        }

        var hash = Convert.ToHexString(sha256.Hash ?? throw new ArchiveFatalException("SHA-256 was not computed.")).ToLowerInvariant();
        var contentType = response.Content.Headers.ContentType?.ToString();
        if (string.IsNullOrWhiteSpace(contentType))
        {
            throw new ArchiveFatalException($"GetObject response for '{entry.Key}' did not include Content-Type.");
        }

        return new DownloadedObject(
            entry.SourceListOrdinal,
            entry.Key,
            entry.RawVersionId,
            contentType,
            responseETag ?? throw new ArchiveFatalException($"GetObject response for '{entry.Key}' did not include ETag."),
            lastModified.Value.ToUniversalTime(),
            body.LongLength,
            body,
            hash,
            headers);
    }

    public async Task<int> ProbeObjectSubresourceStatusAsync(ListedS3Entry entry, string subresource, CancellationToken cancellationToken)
    {
        var uri = BuildObjectUri(entry, subresource);
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return (int)response.StatusCode;
    }

    internal Uri BuildObjectUri(ListedS3Entry entry, string? subresource = null)
    {
        var escapedKey = EscapeS3KeyForPath(entry.Key);
        var query = new List<string>();
        if (!string.IsNullOrEmpty(subresource))
        {
            query.Add(subresource);
        }

        if (ShouldSendVersionId(entry))
        {
            query.Add("versionId=" + Uri.EscapeDataString(entry.RawVersionId!));
        }

        var absoluteUri = _bucketBaseUriText + escapedKey + (query.Count == 0 ? string.Empty : "?" + string.Join("&", query));
        var options = new UriCreationOptions
        {
            DangerousDisablePathAndQueryCanonicalization = true
        };

        if (!Uri.TryCreate(absoluteUri, options, out var uri))
        {
            throw new ArchiveFatalException($"Could not construct S3 object URI for key '{entry.Key}'.");
        }

        return uri;
    }

    private static string? NormalizeResponseETag(IReadOnlyDictionary<string, IReadOnlyList<string>> headers, string name)
    {
        return headers.TryGetValue(name, out var values) && values.Count > 0
            ? ListVersionsXmlParser.NormalizeETag(values[0])
            : null;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> CaptureHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            var normalizedName = header.Key.ToLowerInvariant();
            if (!headers.TryGetValue(normalizedName, out var values))
            {
                values = [];
                headers.Add(normalizedName, values);
            }

            values.AddRange(header.Value);
        }

        return new HeaderBag(headers.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<string>)pair.Value.ToArray(),
            StringComparer.Ordinal));
    }

    private static Uri BuildBucketBaseUri(Uri endpoint, string bucket)
    {
        return new Uri(BuildBucketBaseUriText(endpoint, bucket), UriKind.Absolute);
    }

    private static string BuildBucketBaseUriText(Uri endpoint, string bucket) =>
        endpoint.ToString().TrimEnd('/') + "/" + Uri.EscapeDataString(bucket) + "/";

    private static string EscapeS3KeyForPath(string key)
    {
        return string.Join("/", key.Split('/').Select(static segment => segment switch
        {
            "." => "%2E",
            ".." => "%2E%2E",
            _ => Uri.EscapeDataString(segment)
        }));
    }

    private static string Truncate(string value) => value.Length <= 512 ? value : value[..512];

    private static bool ShouldSendVersionId(ListedS3Entry entry) =>
        entry.RawVersionId is not null
        && (!string.Equals(entry.RawVersionId, "null", StringComparison.Ordinal) || !entry.IsLatest);

    private static void ValidateReturnedVersionId(ListedS3Entry entry, IReadOnlyDictionary<string, IReadOnlyList<string>> headers)
    {
        if (!headers.TryGetValue("x-amz-version-id", out var values) || values.Count == 0)
        {
            if (entry.RawVersionId is not null && !string.Equals(entry.RawVersionId, "null", StringComparison.Ordinal))
            {
                throw new ArchiveFatalException($"GetObject response for '{entry.Key}' did not include x-amz-version-id for requested version '{entry.RawVersionId}'.");
            }

            return;
        }

        var returned = values[0];
        if (entry.RawVersionId is not null && !string.Equals(returned, entry.RawVersionId, StringComparison.Ordinal))
        {
            throw new ArchiveFatalException($"VersionId mismatch for '{entry.Key}': requested/listed '{entry.RawVersionId}', GET returned '{returned}'.");
        }
    }

    public static bool IsTransient(Exception exception) =>
        exception is HttpRequestException { StatusCode: null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout }
        || exception is TaskCanceledException;
}
