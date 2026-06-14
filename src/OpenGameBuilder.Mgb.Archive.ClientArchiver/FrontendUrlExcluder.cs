namespace OpenGameBuilder.Mgb.Archive.ClientArchiver;

public static class FrontendUrlExcluder
{
    public static bool IsExcluded(CdxCapture capture, IReadOnlyList<FrontendExclude> excludes)
    {
        if (excludes.Count == 0)
        {
            return false;
        }

        return IsExcludedUrl(capture.OriginalUrl, excludes)
            || GetRedirectCandidates(capture.RedirectUrl, capture.OriginalUrl).Any(candidate => IsExcludedUrl(candidate, excludes));
    }

    public static bool IsExcludedUrl(string url, IReadOnlyList<FrontendExclude> excludes)
    {
        if (excludes.Count == 0)
        {
            return false;
        }

        var canonical = UrlCanonicalizer.Canonicalize(url);
        var hostPath = UrlCanonicalizer.TryToHostPathPrefix(url);
        return excludes.Any(exclude => exclude.Kind switch
        {
            FrontendExcludeKind.Prefix =>
                canonical.StartsWith(exclude.CanonicalPrefix, StringComparison.Ordinal)
                || (hostPath is not null && hostPath.StartsWith(exclude.HostPathPrefix, StringComparison.Ordinal)),
            FrontendExcludeKind.Contains =>
                url.Contains(exclude.MatchText, StringComparison.OrdinalIgnoreCase)
                || canonical.Contains(exclude.MatchText, StringComparison.OrdinalIgnoreCase)
                || (hostPath is not null && hostPath.Contains(exclude.MatchText, StringComparison.OrdinalIgnoreCase)),
            _ => false
        });
    }

    public static bool IsExcludedReplayRedirect(string originalUrl, ReplayDownload download, IReadOnlyList<FrontendExclude> excludes)
    {
        if (excludes.Count == 0)
        {
            return false;
        }

        var locations = download.StatusCode is >= 300 and <= 399
            ? download.Headers
                .Where(header => header.Name.Equals("location", StringComparison.OrdinalIgnoreCase))
                .Select(header => header.Value)
            : [];
        return GetReplayRedirectCandidates(originalUrl, download.ReplayUri, locations)
            .Any(candidate => IsExcludedUrl(candidate, excludes));
    }

    private static IEnumerable<string> GetRedirectCandidates(string? redirectUrl, string originalUrl)
    {
        if (string.IsNullOrWhiteSpace(redirectUrl) || redirectUrl == "-")
        {
            yield break;
        }

        var trimmed = redirectUrl.Trim();
        yield return trimmed;

        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            yield return "http:" + trimmed;
            yield return "https:" + trimmed;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            yield break;
        }

        if (LooksLikeHostPath(trimmed))
        {
            yield return "http://" + trimmed;
            yield return "https://" + trimmed;
        }

        if (Uri.TryCreate(originalUrl, UriKind.Absolute, out var baseUri)
            && Uri.TryCreate(baseUri, trimmed, out var resolved))
        {
            yield return resolved.ToString();
        }
    }

    private static IEnumerable<string> GetReplayRedirectCandidates(string originalUrl, Uri replayUri, IEnumerable<string> locations)
    {
        foreach (var location in locations)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                continue;
            }

            var trimmed = location.Trim();
            yield return trimmed;

            if (Uri.TryCreate(replayUri, trimmed, out var resolved))
            {
                yield return resolved.ToString();

                var archivedUrl = TryExtractWaybackReplayOriginalUrl(resolved);
                if (archivedUrl is not null)
                {
                    yield return archivedUrl;
                }
            }

            foreach (var candidate in GetRedirectCandidates(trimmed, originalUrl))
            {
                yield return candidate;
            }
        }

        var finalReplayUrl = TryExtractWaybackReplayOriginalUrl(replayUri);
        if (finalReplayUrl is not null)
        {
            yield return finalReplayUrl;
        }
    }

    private static string? TryExtractWaybackReplayOriginalUrl(Uri uri)
    {
        var path = uri.AbsolutePath;
        var marker = "id_/";
        var markerIndex = path.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0 || !path.StartsWith("/web/", StringComparison.Ordinal))
        {
            return null;
        }

        var archivedPath = path[(markerIndex + marker.Length)..];
        if (archivedPath.Length == 0)
        {
            return null;
        }

        var archived = Uri.UnescapeDataString(archivedPath) + uri.Query;
        return Uri.TryCreate(archived, UriKind.Absolute, out _)
            ? archived
            : null;
    }

    private static bool LooksLikeHostPath(string value)
    {
        var firstSlash = value.IndexOf('/');
        var host = firstSlash < 0 ? value : value[..firstSlash];
        return host.Contains('.', StringComparison.Ordinal)
            && !host.Contains('\\', StringComparison.Ordinal)
            && !host.Contains(' ', StringComparison.Ordinal);
    }
}
