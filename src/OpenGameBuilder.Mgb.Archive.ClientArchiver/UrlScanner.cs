using System.Text;
using System.Text.RegularExpressions;

namespace OpenGameBuilder.Mgb.Archive.ClientArchiver;

public static partial class UrlScanner
{
    public static IReadOnlyList<string> Scan(byte[] body, string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            return [];
        }

        var text = Encoding.Latin1.GetString(body);
        var urls = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in AbsoluteUrlRegex().Matches(text))
        {
            AddUrl(urls, TrimUrl(match.Value));
        }

        foreach (Match match in ProtocolRelativeUrlRegex().Matches(text))
        {
            AddUrl(urls, baseUri.Scheme + ":" + TrimUrl(match.Value));
        }

        foreach (Match match in AttributeUrlRegex().Matches(text))
        {
            AddResolvedUrl(urls, baseUri, match.Groups["url"].Value);
        }

        foreach (Match match in CssUrlRegex().Matches(text))
        {
            AddResolvedUrl(urls, baseUri, match.Groups["url"].Value);
        }

        foreach (Match match in AppHostAssetPathRegex().Matches(text))
        {
            AddResolvedAppHostUrl(urls, baseUri, match.Value);
        }

        return urls.Order(StringComparer.Ordinal).ToArray();
    }

    private static void AddResolvedUrl(HashSet<string> urls, Uri baseUri, string value)
    {
        var trimmed = TrimUrl(value);
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return;
        }

        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            AddUrl(urls, baseUri.Scheme + ":" + trimmed);
            return;
        }

        if (Uri.TryCreate(baseUri, trimmed, out var resolved))
        {
            AddUrl(urls, resolved.ToString());
        }
    }

    private static void AddResolvedAppHostUrl(HashSet<string> urls, Uri baseUri, string value)
    {
        var trimmed = TrimUrl(value);
        if (trimmed.StartsWith("apphost/", StringComparison.OrdinalIgnoreCase))
        {
            AddUrl(urls, $"{baseUri.Scheme}://{baseUri.Host}/" + trimmed);
            return;
        }

        if (baseUri.Host.Equals("s3.amazonaws.com", StringComparison.OrdinalIgnoreCase)
            && baseUri.AbsolutePath.StartsWith("/apphost/", StringComparison.OrdinalIgnoreCase))
        {
            AddUrl(urls, $"{baseUri.Scheme}://{baseUri.Host}/apphost/" + trimmed);
            return;
        }

        AddResolvedUrl(urls, baseUri, trimmed);
    }

    private static void AddUrl(HashSet<string> urls, string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && !string.IsNullOrWhiteSpace(uri.Host))
        {
            urls.Add(uri.ToString());
        }
    }

    private static string TrimUrl(string value) =>
        value.Trim().Trim('\'', '"', '`', '<', '>', '(', ')', '[', ']', '{', '}').TrimEnd('.', ',', ';');

    [GeneratedRegex("""https?://[^\s"'<>\\)\]\}]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteUrlRegex();

    [GeneratedRegex("""(?<!:)//[A-Za-z0-9.-]+(?::[0-9]+)?/[^\s"'<>\\)\]\}]*""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProtocolRelativeUrlRegex();

    [GeneratedRegex("""(?:href|src|data|poster|action)\s*=\s*["']?(?<url>[^\s"'<>]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AttributeUrlRegex();

    [GeneratedRegex("""url\(\s*["']?(?<url>[^"'\)\s]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssUrlRegex();

    [GeneratedRegex("""(?<![A-Za-z0-9_./-])(?:apphost/)?(?:game_music|sounds|images|carousel_images|mascot_images)/[A-Za-z0-9._~!$&'()*+,;=:@%/-]+\.(?:mp3|wav|ogg|png|jpe?g|gif|ico|swf|js|css|xml|html?)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AppHostAssetPathRegex();
}
