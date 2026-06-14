using Microsoft.Extensions.Logging;

namespace OpenGameBuilder.Mgb.Archive.S3Archiver;

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
        finally
        {
            PromptBeforeExit();
        }
    }

    private static void PromptBeforeExit()
    {
        if (!Environment.UserInteractive || Console.IsInputRedirected)
        {
            return;
        }

        Console.WriteLine();
        Console.Write("Press any key to exit...");
        Console.ReadKey(intercept: true);
        Console.WriteLine();
    }
}
