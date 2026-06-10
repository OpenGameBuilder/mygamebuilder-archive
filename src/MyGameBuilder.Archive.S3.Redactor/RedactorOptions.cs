namespace MyGameBuilder.Archive.S3.Redactor;

public sealed record RedactorOptions(
    string? ArchivePath,
    string? ReviewPath,
    string? OutputPath,
    int UniqueColorThreshold)
{
    public const int DefaultUniqueColorThreshold = 100;

    public string EffectiveReviewPath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ReviewPath))
            {
                return Path.GetFullPath(ReviewPath);
            }

            if (string.IsNullOrWhiteSpace(ArchivePath))
            {
                return Path.GetFullPath("mgb-redactor.review.sqlite");
            }

            return Path.ChangeExtension(Path.GetFullPath(ArchivePath), ".redactor-review.sqlite");
        }
    }

    public string EffectiveOutputPath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(OutputPath))
            {
                return Path.GetFullPath(OutputPath);
            }

            if (string.IsNullOrWhiteSpace(ArchivePath))
            {
                return Path.GetFullPath("mgb-redacted.sqlite");
            }

            var source = Path.GetFullPath(ArchivePath);
            return Path.Combine(
                Path.GetDirectoryName(source) ?? Environment.CurrentDirectory,
                Path.GetFileNameWithoutExtension(source) + ".redacted" + Path.GetExtension(source));
        }
    }

    public static RedactorOptions Parse(string[] args)
    {
        string? archivePath = null;
        string? reviewPath = null;
        string? outputPath = null;
        var threshold = DefaultUniqueColorThreshold;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var value = i + 1 < args.Length ? args[i + 1] : null;
            switch (arg)
            {
                case "--archive" when value is not null:
                    archivePath = value;
                    i++;
                    break;
                case "--review" when value is not null:
                    reviewPath = value;
                    i++;
                    break;
                case "--output" when value is not null:
                    outputPath = value;
                    i++;
                    break;
                case "--unique-color-threshold" when value is not null && int.TryParse(value, out var parsed):
                    threshold = parsed;
                    i++;
                    break;
            }
        }

        return new RedactorOptions(archivePath, reviewPath, outputPath, Math.Max(1, threshold));
    }
}
