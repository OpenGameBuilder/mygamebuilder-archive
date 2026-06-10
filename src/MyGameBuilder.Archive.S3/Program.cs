using Microsoft.Extensions.Logging;

namespace MyGameBuilder.Archive.S3;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(LogLevel.Information)
                .AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                });
        });

        try
        {
            return await CliApp.RunAsync(args, loggerFactory).ConfigureAwait(false);
        }
        catch (ArchiveFatalException ex)
        {
            loggerFactory.CreateLogger("fatal").LogError("{Message}", ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("fatal").LogError(ex, "Unexpected failure");
            return 1;
        }
    }
}
