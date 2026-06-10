using System.Text;
using System.Text.RegularExpressions;

namespace MyGameBuilder.Archive.Frontend;

public static class UrlScanner
{
    private static readonly Regex AbsoluteUrlRegex = new(
        """(?i)(?:https?|ftp):(?:\\?/\\?/|//)[^\s"'<>`{}\[\]|^]+""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex ProtocolRelativeUrlRegex = new(
        """(?i)(?<![:\w])//[a-z0-9][a-z0-9.-]*(?::\d+)?/[^\s"'<>`{}\[\]|^]*""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex RelativeUrlRegex = new(
        """(?i)(?<![\w:])(?:\.{1,2}/|/)[^\s"'<>`{}\[\]|^]+|(?<![\w:])[\w.-]+/(?:[\w .~!$&()*+,;=:@%-]+/)*[\w .~!$&()*+,;=:@%-]+\.(?:html?|css|js|png|gif|jpe?g|svg|swf|mp3|xml|json|txt|ico)(?:\?[^\s"'<>`{}\[\]|^]*)?""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    public static IReadOnlyList<UrlMatch> Scan(byte[] body, string sourceUrl)
    {
        var matches = new Dictionary<(string Raw, string ResolvedKey), UrlMatch>();
        foreach (var text in GetTextVariants(body))
        {
            AddMatches(AbsoluteUrlRegex, text, sourceUrl, matches);
            AddMatches(ProtocolRelativeUrlRegex, text, sourceUrl, matches);
            AddMatches(RelativeUrlRegex, text, sourceUrl, matches);
        }

        return matches.Values
            .OrderBy(static match => match.ResolvedCanonicalUrl ?? match.ResolvedUrl ?? match.RawText, StringComparer.Ordinal)
            .ThenBy(static match => match.RawText, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> GetTextVariants(byte[] body)
    {
        var latin1 = Encoding.Latin1.GetString(body);
        yield return latin1;

        if (IsLikelyUtf8(body))
        {
            yield return Encoding.UTF8.GetString(body);
        }

        var slashUnescaped = latin1.Replace(@"\/", "/", StringComparison.Ordinal);
        if (!string.Equals(slashUnescaped, latin1, StringComparison.Ordinal))
        {
            yield return slashUnescaped;
        }

        if (latin1.Contains('%', StringComparison.Ordinal))
        {
            string? unescaped = null;
            try
            {
                unescaped = Uri.UnescapeDataString(latin1);
            }
            catch (UriFormatException)
            {
            }

            if (unescaped is not null)
            {
                yield return unescaped;
            }
        }
    }

    private static void AddMatches(
        Regex regex,
        string text,
        string sourceUrl,
        IDictionary<(string Raw, string ResolvedKey), UrlMatch> matches)
    {
        foreach (Match match in regex.Matches(text))
        {
            var raw = TrimCandidate(match.Value);
            if (raw.Length < 2 || IsNoise(raw))
            {
                continue;
            }

            var resolved = UrlCanonicalizer.Resolve(raw, sourceUrl);
            var canonical = resolved is null ? null : UrlCanonicalizer.Canonicalize(resolved);
            var key = canonical ?? resolved ?? string.Empty;
            matches.TryAdd((raw, key), new UrlMatch(raw, resolved, canonical));
        }
    }

    private static string TrimCandidate(string value)
    {
        var trimmed = value.Trim();
        while (trimmed.Length > 0 && IsTrailingJunk(trimmed[^1]))
        {
            trimmed = trimmed[..^1];
        }

        while (trimmed.Length > 0 && IsLeadingJunk(trimmed[0]))
        {
            trimmed = trimmed[1..];
        }

        return trimmed;
    }

    private static bool IsTrailingJunk(char value) =>
        value is '.' or ',' or ';' or ':' or ')' or ']' or '}' or '"' or '\'' or '\\';

    private static bool IsLeadingJunk(char value) =>
        value is '"' or '\'' or '(' or '[' or '{';

    private static bool IsNoise(string value) =>
        value.StartsWith("//", StringComparison.Ordinal)
        && !Regex.IsMatch(value, """(?i)^//[a-z0-9][a-z0-9.-]*\.[a-z]{2,}""");

    private static bool IsLikelyUtf8(byte[] body)
    {
        try
        {
            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(body);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
