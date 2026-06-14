using Microsoft.Extensions.Logging;

namespace OpenGameBuilder.Mgb.Archive.S3Archiver;

public static class CliApp
{
    private static readonly Uri DefaultEndpoint = new("https://s3.amazonaws.com");

    public static async Task<int> RunAsync(string[] args, ILoggerFactory loggerFactory, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            WriteUsage(Console.Out);
            return args.Length == 0 ? 2 : 0;
        }

        return args[0] switch
        {
            "capture" => await RunCaptureAsync(ParseCapture(args.Skip(1).ToArray()), loggerFactory, cancellationToken).ConfigureAwait(false),
            "validate" => await RunValidateAsync(ParseValidate(args.Skip(1).ToArray()), loggerFactory, cancellationToken).ConfigureAwait(false),
            "simplify-unversioned" => await RunSimplifyUnversionedAsync(ParseSimplifyUnversioned(args.Skip(1).ToArray()), loggerFactory, cancellationToken).ConfigureAwait(false),
            "split-file" => await RunSplitFileAsync(ParseSplitFile(args.Skip(1).ToArray()), loggerFactory, cancellationToken).ConfigureAwait(false),
            _ => throw new ArchiveFatalException($"Unknown command '{args[0]}'.")
        };
    }

    private static async Task<int> RunCaptureAsync(ArchiveOptions options, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var diagnostics = DiagnosticsWriter.Create(options.WorkDirectory);
        try
        {
            using var httpClient = new HttpClient();
            var s3Client = new S3ArchiveClient(httpClient, options.Endpoint, options.Bucket, loggerFactory.CreateLogger<S3ArchiveClient>());
            var workflow = new CaptureWorkflow(
                options,
                s3Client,
                diagnostics,
                loggerFactory.CreateLogger<CaptureWorkflow>());

            await workflow.RunAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            await diagnostics.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<int> RunValidateAsync(ValidateOptions options, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var isSimplified = UnversionedArchiveMigrator.IsSimplifiedArchive(options.DatabasePath);
        var result = isSimplified
            ? await UnversionedArchiveMigrator.ValidateSimplifiedAsync(options.DatabasePath, cancellationToken).ConfigureAwait(false)
            : await ArchiveValidator.ValidateAsync(options.DatabasePath, cancellationToken).ConfigureAwait(false);
        foreach (var warning in result.Warnings)
        {
            loggerFactory.CreateLogger("validate").LogWarning("{Warning}", warning);
        }

        if (result.IsValid)
        {
            loggerFactory.CreateLogger("validate").LogInformation(
                "{Kind} archive validation passed for {Database}",
                isSimplified ? "Simplified unversioned" : "Canonical version-aware",
                options.DatabasePath);
            return 0;
        }

        foreach (var error in result.Errors)
        {
            loggerFactory.CreateLogger("validate").LogError("{Error}", error);
        }

        return 2;
    }

    private static async Task<int> RunSimplifyUnversionedAsync(SimplifyUnversionedOptions options, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("simplify-unversioned");
        var analysis = await UnversionedArchiveMigrator.AnalyzeAsync(options.DatabasePath, cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Archive versioning analysis: {Objects} objects, {Entries} entries, {LiveEntries} live entries, {DeleteMarkers} delete markers, {NonNullVersions} non-null version IDs, {MultiEntryObjects} objects without exactly one entry",
            analysis.ObjectCount,
            analysis.EntryCount,
            analysis.LiveEntryCount,
            analysis.DeleteMarkerCount,
            analysis.NonNullVersionIdCount,
            analysis.ObjectsWithoutExactlyOneEntryCount);

        if (analysis.CaseInsensitiveKeyCollisionGroupCount > 0)
        {
            logger.LogWarning(
                "Archive contains {Count} case-insensitive key collision groups. S3 keys are case-sensitive; the simplified archive preserves exact binary key identity.",
                analysis.CaseInsensitiveKeyCollisionGroupCount);
        }

        if (!analysis.IsUnversioned)
        {
            logger.LogWarning(
                "Archive is versioned or contains versioning artifacts. Migration was not performed. nonLatest={NonLatest}, nonZeroVersionOrder={NonZeroVersionOrder}, liveWithoutBody={LiveWithoutBody}",
                analysis.NonLatestEntryCount,
                analysis.NonZeroVersionOrderCount,
                analysis.LiveEntryWithoutBodyCount);
            return options.OutputPath is null ? 0 : 2;
        }

        logger.LogInformation("Archive is unversioned: every S3 key has exactly one current live object and no versioning artifacts.");
        if (options.OutputPath is null)
        {
            return 0;
        }

        await UnversionedArchiveMigrator.ConvertAsync(options.DatabasePath, options.OutputPath, options.Replace, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Created simplified unversioned archive at {Output}", options.OutputPath);
        return 0;
    }

    private static async Task<int> RunSplitFileAsync(SplitFileOptions options, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("split-file");
        var outputPaths = options.CompressZstd
            ? await FileSplitter.SplitZstdCompressedAsync(
                options.InputPath,
                options.OutputPrefix,
                options.PartSizeBytes,
                options.Replace,
                cancellationToken).ConfigureAwait(false)
            : await FileSplitter.SplitAsync(
                options.InputPath,
                options.OutputPrefix,
                options.PartSizeBytes,
                options.Replace,
                cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "{Action} {Input} into {Count} part(s) with prefix {OutputPrefix}",
            options.CompressZstd ? "Compressed and split" : "Split",
            options.InputPath,
            outputPaths.Count,
            options.OutputPrefix);
        foreach (var outputPath in outputPaths)
        {
            logger.LogInformation("Created {Part}", outputPath);
        }

        return 0;
    }

    private static ArchiveOptions ParseCapture(string[] args)
    {
        var values = ParseOptions(args);
        var bucket = GetRequired(values, "bucket");
        var output = Path.GetFullPath(GetRequired(values, "output"));
        var endpoint = new Uri(GetOptional(values, "endpoint") ?? DefaultEndpoint.ToString());
        var outputDirectory = Path.GetDirectoryName(output) ?? Environment.CurrentDirectory;
        var workDirectory = Path.GetFullPath(GetOptional(values, "work-dir") ?? Path.Combine(outputDirectory, "archive-work"));
        var concurrencyText = GetOptional(values, "concurrency") ?? "16";

        if (!int.TryParse(concurrencyText, out var concurrency) || concurrency < 1 || concurrency > 256)
        {
            throw new ArchiveFatalException("--concurrency must be an integer between 1 and 256.");
        }

        return new ArchiveOptions(
            bucket,
            endpoint,
            output,
            workDirectory,
            concurrency,
            HasFlag(values, "resume"),
            HasFlag(values, "replace"));
    }

    private static ValidateOptions ParseValidate(string[] args)
    {
        var values = ParseOptions(args);
        return new ValidateOptions(Path.GetFullPath(GetRequired(values, "database")));
    }

    private static SimplifyUnversionedOptions ParseSimplifyUnversioned(string[] args)
    {
        var values = ParseOptions(args);
        var output = GetOptional(values, "output");
        return new SimplifyUnversionedOptions(
            Path.GetFullPath(GetRequired(values, "database")),
            output is null ? null : Path.GetFullPath(output),
            HasFlag(values, "replace"));
    }

    private static SplitFileOptions ParseSplitFile(string[] args)
    {
        var values = ParseOptions(args);
        var input = Path.GetFullPath(GetRequired(values, "input"));
        var compressZstd = HasFlag(values, "zstd") || HasFlag(values, "compress-zstd");
        var defaultOutputPrefix = compressZstd && !input.EndsWith(".zst", StringComparison.OrdinalIgnoreCase)
            ? input + ".zst"
            : input;
        var outputPrefix = Path.GetFullPath(GetOptional(values, "output-prefix") ?? defaultOutputPrefix);
        var partSizeText = GetOptional(values, "part-size-bytes") ??
            FileSplitter.DefaultPartSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!long.TryParse(partSizeText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var partSizeBytes) ||
            partSizeBytes <= 0)
        {
            throw new ArchiveFatalException("--part-size-bytes must be a positive integer.");
        }

        return new SplitFileOptions(input, outputPrefix, partSizeBytes, HasFlag(values, "replace"), compressZstd);
    }

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArchiveFatalException($"Unexpected positional argument '{arg}'.");
            }

            var name = arg[2..];
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArchiveFatalException("Empty option name.");
            }

            if (name is "resume" or "replace" or "zstd" or "compress-zstd")
            {
                values[name] = null;
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArchiveFatalException($"Option '--{name}' requires a value.");
            }

            values[name] = args[++index];
        }

        return values;
    }

    private static string GetRequired(Dictionary<string, string?> values, string name)
    {
        if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArchiveFatalException($"Missing required option '--{name}'.");
        }

        return value;
    }

    private static string? GetOptional(Dictionary<string, string?> values, string name)
    {
        values.TryGetValue(name, out var value);
        return value;
    }

    private static bool HasFlag(Dictionary<string, string?> values, string name) => values.ContainsKey(name);

    private static bool IsHelp(string value) => value is "-h" or "--help" or "help";

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("OpenGameBuilder MGB Archive S3 archiver");
        writer.WriteLine();
        writer.WriteLine("Commands:");
        writer.WriteLine("  capture --bucket JGI_test1 --output <archive.sqlite> [--work-dir <path>] [--endpoint <uri>] [--concurrency N] [--resume] [--replace]");
        writer.WriteLine("  validate --database <archive.sqlite>");
        writer.WriteLine("  simplify-unversioned --database <archive.sqlite> [--output <simple.sqlite>] [--replace]");
        writer.WriteLine("  split-file --input <archive.sqlite[.zst]> [--zstd] [--output-prefix <path>] [--part-size-bytes 1900000000] [--replace]");
    }
}
