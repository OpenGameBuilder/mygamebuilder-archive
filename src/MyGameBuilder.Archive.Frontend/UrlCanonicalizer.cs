namespace MyGameBuilder.Archive.Frontend;

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
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant(),
            Fragment = string.Empty
        };

        if (IsDefaultPort(builder.Scheme, builder.Port))
        {
            builder.Port = -1;
        }

        return builder.Uri.AbsoluteUri;
    }

    public static string? Resolve(string rawUrl, string baseUrl)
    {
        var normalized = NormalizeEscapes(rawUrl);
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            {
                normalized = baseUri.Scheme + ":" + normalized;
            }
            else
            {
                normalized = "http:" + normalized;
            }
        }

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var absolute))
        {
            return absolute.AbsoluteUri;
        }

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var source)
            && Uri.TryCreate(source, normalized, out var resolved)
            && !string.IsNullOrWhiteSpace(resolved.Host))
        {
            return resolved.AbsoluteUri;
        }

        return null;
    }

    public static string ToHostPathPrefix(Uri uri)
    {
        var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return uri.Host.ToLowerInvariant() + port + uri.PathAndQuery;
    }

    public static string? TryToHostPathPrefix(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? ToHostPathPrefix(uri)
            : null;
    }

    public static string NormalizeEscapes(string value)
    {
        var normalized = value.Replace(@"\/", "/", StringComparison.Ordinal);
        normalized = normalized
            .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase)
            .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase);

        if (normalized.Contains('%', StringComparison.Ordinal))
        {
            try
            {
                var unescaped = Uri.UnescapeDataString(normalized);
                if (LooksAbsoluteOrProtocolRelative(unescaped))
                {
                    normalized = unescaped;
                }
            }
            catch (UriFormatException)
            {
            }
        }

        return normalized;
    }

    private static bool LooksAbsoluteOrProtocolRelative(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("//", StringComparison.Ordinal);

    private static bool IsDefaultPort(string scheme, int port) =>
        port == -1
        || (port == 80 && string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase))
        || (port == 443 && string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase));
}
