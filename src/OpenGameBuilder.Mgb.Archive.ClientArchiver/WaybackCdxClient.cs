using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenGameBuilder.Mgb.Archive.ClientArchiver;

public sealed class WaybackCdxClient
{
    private const int PageSize = 5_000;
    private const string UserAgent = "OpenGameBuilder.Mgb.Archive.ClientArchiver/0.1 (+https://github.com/OpenGameBuilder/mygamebuilder-archive)";
    private static readonly string[] Fields = ["timestamp", "original", "mimetype", "statuscode", "digest", "length", "redirect"];
    private readonly HttpClient _httpClient;
    private readonly Uri _cdxEndpoint;
    private readonly Uri _waybackEndpoint;
    private readonly ILogger<WaybackCdxClient> _logger;

    public WaybackCdxClient(HttpClient httpClient, Uri cdxEndpoint, Uri waybackEndpoint, ILogger<WaybackCdxClient> logger)
    {
        _httpClient = httpClient;
        _cdxEndpoint = cdxEndpoint;
        _waybackEndpoint = waybackEndpoint;
        _logger = logger;

        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }
    }

    public Uri BuildCdxPageUri(FrontendSeed seed, string? resumeKey)
    {
        var query = new List<string>
        {
            "output=json",
            "showResumeKey=true",
            "limit=" + PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "fl=" + Uri.EscapeDataString(string.Join(",", Fields)),
            "url=" + Uri.EscapeDataString(BuildCdxSearchUrl(seed))
        };

        var matchType = seed.Kind switch
        {
            FrontendSeedKind.Domain => "domain",
            FrontendSeedKind.Prefix => "prefix",
            _ => null
        };

        if (matchType is not null)
        {
            query.Add("matchType=" + matchType);
        }

        if (!string.IsNullOrEmpty(resumeKey))
        {
            query.Add("resumeKey=" + Uri.EscapeDataString(resumeKey));
        }

        return new Uri(_cdxEndpoint.ToString().TrimEnd('?') + "?" + string.Join("&", query));
    }

    public async Task<CdxPage> GetCdxPageAsync(FrontendSeed seed, string? resumeKey, CancellationToken cancellationToken)
    {
        var uri = BuildCdxPageUri(seed, resumeKey);
        _logger.LogDebug("Querying CDX {Uri}", uri);
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var message = $"CDX query failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body)}";
            if (IsTransientHttpStatusCode(response.StatusCode))
            {
                throw new HttpRequestException(message, inner: null, response.StatusCode);
            }

            throw new ArchiveFatalException(message);
        }

        return ParseCdxJson(body);
    }

    public async Task<ReplayDownload> DownloadReplayAsync(PendingReplayCapture capture, CancellationToken cancellationToken)
    {
        var uri = BuildReplayUri(capture.Timestamp, capture.OriginalUrl);
        return await DownloadAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReplayDownload> DownloadDirectAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme is not "http" and not "https")
        {
            throw new ArchiveFatalException($"Direct frontend capture only supports HTTP(S) URLs: {uri}");
        }

        return await DownloadAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ReplayDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var (body, bodyReadError) = await ReadBodyPreservingPartialAsync(response, cancellationToken).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

        return new ReplayDownload(
            response.RequestMessage?.RequestUri ?? uri,
            (int)response.StatusCode,
            response.ReasonPhrase,
            body,
            hash,
            bodyReadError,
            CaptureHeaders(response));
    }

    public Uri BuildReplayUri(string timestamp, string originalUrl)
    {
        var text = _waybackEndpoint.ToString().TrimEnd('/') + "/" + timestamp + "id_/" + originalUrl;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            throw new ArchiveFatalException($"Could not construct Wayback replay URL for '{originalUrl}' at {timestamp}.");
        }

        return uri;
    }

    public static string BuildCdxSearchUrl(FrontendSeed seed)
    {
        return seed.Kind switch
        {
            FrontendSeedKind.Domain => seed.Value.Trim().TrimEnd('/') + "/*",
            FrontendSeedKind.Prefix => BuildPrefixSearchUrl(seed.Value),
            _ => seed.Value
        };
    }

    public static bool IsTransient(Exception exception) =>
        exception is HttpRequestException httpRequestException && IsTransientHttpStatusCode(httpRequestException.StatusCode)
        || exception is TaskCanceledException;

    private static bool IsTransientHttpStatusCode(HttpStatusCode? statusCode) =>
        statusCode is null
        || statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)statusCode.Value is >= 500 and <= 599;

    private static CdxPage ParseCdxJson(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
        {
            return new CdxPage(Array.Empty<CdxCapture>(), null);
        }

        var captures = new List<CdxCapture>();
        string? resumeKey = null;
        var sawResumeSeparator = false;

        foreach (var row in document.RootElement.EnumerateArray().Skip(1))
        {
            if (row.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var length = row.GetArrayLength();
            if (length == 0)
            {
                sawResumeSeparator = true;
                continue;
            }

            if (sawResumeSeparator && length == 1)
            {
                resumeKey = GetString(row[0]);
                break;
            }

            if (length < Fields.Length)
            {
                throw new ArchiveFatalException("CDX JSON row had fewer fields than expected.");
            }

            var timestamp = GetRequiredString(row[0], "timestamp");
            var original = GetRequiredString(row[1], "original");
            captures.Add(new CdxCapture(
                timestamp,
                original,
                GetString(row[2]),
                GetString(row[3]),
                GetString(row[4]),
                ParseLength(GetString(row[5])),
                GetString(row[6])));
        }

        return new CdxPage(captures, resumeKey);
    }

    private static HeaderBag CaptureHeaders(HttpResponseMessage response)
    {
        var headers = new List<ReplayHeader>();
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            var normalizedName = header.Key.ToLowerInvariant();
            headers.AddRange(header.Value.Select(value => new ReplayHeader(normalizedName, value)));
        }

        return new HeaderBag(headers);
    }

    private static async Task<(byte[] Body, string? ReadError)> ReadBodyPreservingPartialAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];

        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (memory.Length > 0 && !IsCancellation(ex))
            {
                return (memory.ToArray(), ex.Message);
            }

            if (read == 0)
            {
                return (memory.ToArray(), null);
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsCancellation(Exception exception) =>
        exception is OperationCanceledException;

    private static string BuildPrefixSearchUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return value;
        }

        var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return uri.Host.ToLowerInvariant() + port + uri.PathAndQuery;
    }

    private static string GetRequiredString(JsonElement element, string name) =>
        GetString(element) ?? throw new ArchiveFatalException($"CDX JSON row was missing required field '{name}'.");

    private static string? GetString(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => element.ToString()
        };

    private static long? ParseLength(string? value) =>
        long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static string Truncate(string value) => value.Length <= 512 ? value : value[..512];
}
