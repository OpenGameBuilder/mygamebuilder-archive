using Microsoft.Extensions.Logging;

namespace MyGameBuilder.Archive.Frontend;

public static class CliApp
{
    private static readonly Uri DefaultCdxEndpoint = new("https://web.archive.org/cdx/search/cdx");
    private static readonly Uri DefaultWaybackEndpoint = new("https://web.archive.org/web");

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
            "export-urls" => await RunExportUrlsAsync(ParseExportUrls(args.Skip(1).ToArray()), loggerFactory, cancellationToken).ConfigureAwait(false),
            _ => throw new ArchiveFatalException($"Unknown command '{args[0]}'.")
        };
    }

    private static async Task<int> RunCaptureAsync(FrontendArchiveOptions options, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();
        var waybackClient = new WaybackCdxClient(
            httpClient,
            options.CdxEndpoint,
            options.WaybackEndpoint,
            loggerFactory.CreateLogger<WaybackCdxClient>());
        var workflow = new CaptureWorkflow(options, waybackClient, loggerFactory.CreateLogger<CaptureWorkflow>());
        await workflow.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunValidateAsync(ValidateOptions options, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var result = await FrontendArchiveValidator.ValidateAsync(options.DatabasePath, cancellationToken).ConfigureAwait(false);
        foreach (var warning in result.Warnings)
        {
            loggerFactory.CreateLogger("validate").LogWarning("{Warning}", warning);
        }

        if (result.IsValid)
        {
            loggerFactory.CreateLogger("validate").LogInformation("Frontend archive validation passed for {Database}", options.DatabasePath);
            return 0;
        }

        foreach (var error in result.Errors)
        {
            loggerFactory.CreateLogger("validate").LogError("{Error}", error);
        }

        return 2;
    }

    private static async Task<int> RunExportUrlsAsync(ExportUrlsOptions options, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        await UrlExporter.ExportAsync(options.DatabasePath, options.OutputPath, cancellationToken).ConfigureAwait(false);
        loggerFactory.CreateLogger("export-urls").LogInformation("Exported discovered URLs to {Output}", options.OutputPath);
        return 0;
    }

    private static FrontendArchiveOptions ParseCapture(string[] args)
    {
        var values = ParseOptions(args);
        var seeds = Path.GetFullPath(GetRequired(values, "seeds"));
        var output = Path.GetFullPath(GetRequired(values, "output"));
        var outputDirectory = Path.GetDirectoryName(output) ?? Environment.CurrentDirectory;
        var workDirectory = Path.GetFullPath(GetOptional(values, "work-dir") ?? Path.Combine(outputDirectory, "archive-work"));
        var concurrencyText = GetOptional(values, "concurrency") ?? "4";

        if (!int.TryParse(concurrencyText, out var concurrency) || concurrency < 1 || concurrency > 32)
        {
            throw new ArchiveFatalException("--concurrency must be an integer between 1 and 32.");
        }

        return new FrontendArchiveOptions(
            seeds,
            output,
            workDirectory,
            concurrency,
            HasFlag(values, "resume"),
            HasFlag(values, "replace"),
            new Uri(GetOptional(values, "cdx-endpoint") ?? DefaultCdxEndpoint.ToString()),
            new Uri(GetOptional(values, "wayback-endpoint") ?? DefaultWaybackEndpoint.ToString()));
    }

    private static ValidateOptions ParseValidate(string[] args)
    {
        var values = ParseOptions(args);
        return new ValidateOptions(Path.GetFullPath(GetRequired(values, "database")));
    }

    private static ExportUrlsOptions ParseExportUrls(string[] args)
    {
        var values = ParseOptions(args);
        return new ExportUrlsOptions(
            Path.GetFullPath(GetRequired(values, "database")),
            Path.GetFullPath(GetRequired(values, "output")));
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

            if (name is "resume" or "replace")
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
        writer.WriteLine("MyGameBuilder Archive frontend Wayback archiver");
        writer.WriteLine();
        writer.WriteLine("Commands:");
        writer.WriteLine("  capture --seeds <seeds.txt> --output <frontend.sqlite> [--work-dir <path>] [--concurrency N] [--resume] [--replace]");
        writer.WriteLine("  validate --database <frontend.sqlite>");
        writer.WriteLine("  export-urls --database <frontend.sqlite> --output <urls.json|urls.csv>");
        writer.WriteLine();
        writer.WriteLine("Seed lines:");
        writer.WriteLine("  domain mygamebuilder.com");
        writer.WriteLine("  prefix https://s3.amazonaws.com/apphost/");
        writer.WriteLine("  url https://example.com/file.js");
        writer.WriteLine("  exclude https://mygamebuilder.com/forum/");
    }
}
