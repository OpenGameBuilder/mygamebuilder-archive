namespace OpenGameBuilder.Mgb.Archive.ClientArchiver;

public static class UrlCanonicalizer
{
    public static string Canonicalize(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            return url;
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = "http",
            Host = NormalizeHost(uri.Host),
            Fragment = string.Empty
        };

        if (IsDefaultPort(uri.Port))
        {
            builder.Port = -1;
        }

        return builder.Uri.AbsoluteUri;
    }

    public static string ToHostPathPrefix(Uri uri)
    {
        var port = IsDefaultPort(uri.Port) ? string.Empty : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return NormalizeHost(uri.Host) + port + uri.PathAndQuery;
    }

    public static string? TryToHostPathPrefix(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? ToHostPathPrefix(uri)
            : null;
    }

    private static string NormalizeHost(string host)
    {
        var normalized = host.ToLowerInvariant();
        return normalized.StartsWith("www.", StringComparison.Ordinal)
            ? normalized["www.".Length..]
            : normalized;
    }

    private static bool IsDefaultPort(int port) =>
        port == -1
        || port == 80
        || port == 443;
}
