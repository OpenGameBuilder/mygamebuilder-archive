using System.Security.Cryptography;

namespace OpenGameBuilder.Mgb.Archive.ClientArchiver;

public static class SeedFileParser
{
    public static async Task<FrontendSeedFile> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new ArchiveFatalException($"Seed file was not found: {fullPath}");
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var (seeds, excludes) = Parse(text);
        return new FrontendSeedFile(seeds, excludes, hash);
    }

    public static (IReadOnlyList<FrontendSeed> Seeds, IReadOnlyList<FrontendExclude> Excludes) Parse(string text)
    {
        var seeds = new List<FrontendSeed>();
        var excludes = new List<FrontendExclude>();
        using var reader = new StringReader(text);
        var lineNumber = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var firstSpace = trimmed.IndexOfAny([' ', '\t']);
            if (firstSpace <= 0)
            {
                throw new ArchiveFatalException($"Invalid seed line {lineNumber}: expected '<domain|prefix|url> <value>'.");
            }

            var kindText = trimmed[..firstSpace].Trim();
            var value = trimmed[firstSpace..].Trim();
            if (value.Length == 0)
            {
                throw new ArchiveFatalException($"Invalid seed line {lineNumber}: seed value is empty.");
            }

            if (kindText.Equals("exclude", StringComparison.OrdinalIgnoreCase)
                || kindText.Equals("exclude-prefix", StringComparison.OrdinalIgnoreCase))
            {
                excludes.Add(CreatePrefixExclude(lineNumber, value, trimmed));
                continue;
            }

            if (kindText.Equals("exclude-contains", StringComparison.OrdinalIgnoreCase)
                || kindText.Equals("exclude-substring", StringComparison.OrdinalIgnoreCase))
            {
                excludes.Add(CreateContainsExclude(lineNumber, value, trimmed));
                continue;
            }

            var kind = kindText.ToLowerInvariant() switch
            {
                "domain" => FrontendSeedKind.Domain,
                "prefix" => FrontendSeedKind.Prefix,
                "url" => FrontendSeedKind.Url,
                _ => throw new ArchiveFatalException($"Invalid seed line {lineNumber}: unknown seed kind '{kindText}'.")
            };

            ValidateSeed(lineNumber, kind, value);
            seeds.Add(new FrontendSeed(lineNumber, kind, value, trimmed));
        }

        if (seeds.Count == 0)
        {
            throw new ArchiveFatalException("Seed file did not contain any seeds.");
        }

        return (seeds, excludes);
    }

    private static void ValidateSeed(int lineNumber, FrontendSeedKind kind, string value)
    {
        switch (kind)
        {
            case FrontendSeedKind.Domain:
                if (value.Contains("://", StringComparison.Ordinal) || value.Contains('/', StringComparison.Ordinal))
                {
                    throw new ArchiveFatalException($"Invalid seed line {lineNumber}: domain seeds should be bare host names.");
                }

                break;

            case FrontendSeedKind.Prefix:
            case FrontendSeedKind.Url:
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
                {
                    throw new ArchiveFatalException($"Invalid seed line {lineNumber}: {kind.ToString().ToLowerInvariant()} seed must be an absolute URL.");
                }

                break;
        }
    }

    private static FrontendExclude CreatePrefixExclude(int lineNumber, string value, string rawText)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArchiveFatalException($"Invalid seed line {lineNumber}: exclude-prefix seed must be an absolute URL.");
        }

        return new FrontendExclude(
            lineNumber,
            FrontendExcludeKind.Prefix,
            value,
            rawText,
            UrlCanonicalizer.Canonicalize(value),
            UrlCanonicalizer.ToHostPathPrefix(uri),
            string.Empty);
    }

    private static FrontendExclude CreateContainsExclude(int lineNumber, string value, string rawText)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArchiveFatalException($"Invalid seed line {lineNumber}: exclude-contains value is empty.");
        }

        var matchText = value.ToLowerInvariant();
        return new FrontendExclude(
            lineNumber,
            FrontendExcludeKind.Contains,
            value,
            rawText,
            matchText,
            matchText,
            matchText);
    }
}
