using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace MyGameBuilder.Archive.Frontend;

public sealed class CaptureWorkflow(
    FrontendArchiveOptions options,
    WaybackCdxClient waybackClient,
    ILogger<CaptureWorkflow> logger)
{
    private string InProgressPath => options.OutputPath + ".inprogress.sqlite";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        PreparePaths();

        var seedFile = await SeedFileParser.ReadAsync(options.SeedsPath, cancellationToken).ConfigureAwait(false);
        using var store = new SqliteFrontendArchiveStore(InProgressPath);
        store.Initialize();
        store.PrepareSeedFile(seedFile, options.SeedsPath);

        await EnumerateAsync(store, seedFile.Excludes, cancellationToken).ConfigureAwait(false);
        await DownloadAllAsync(store, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Finalizing frontend archive metadata");
        store.FinalizeArchiveInfo(options);

        var validation = await FrontendArchiveValidator.ValidateAsync(InProgressPath, cancellationToken).ConfigureAwait(false);
        foreach (var warning in validation.Warnings)
        {
            logger.LogWarning("{Warning}", warning);
        }

        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                logger.LogError("{Error}", error);
            }

            throw new ArchiveFatalException("Frontend archive validation failed; final archive was not produced.");
        }

        store.PrepareStandaloneFile();
        store.Dispose();
        Sqlite.ClearPools();
        EnsureNoSqliteSidecars(InProgressPath);

        PublishFinalFile();
        logger.LogInformation("Created immutable frontend SQLite archive at {Output}", options.OutputPath);
    }

    private async Task EnumerateAsync(SqliteFrontendArchiveStore store, IReadOnlyList<FrontendExclude> excludes, CancellationToken cancellationToken)
    {
        foreach (var storedSeed in store.GetIncompleteSeeds())
        {
            var resumeKey = storedSeed.ResumeKey;
            long seedRows = 0;
            long excludedRows = 0;
            logger.LogInformation(
                "Enumerating Wayback CDX seed {Line}: {Seed}",
                storedSeed.Seed.LineNumber,
                storedSeed.Seed.RawText);

            while (true)
            {
                var page = await waybackClient.GetCdxPageAsync(storedSeed.Seed, resumeKey, cancellationToken).ConfigureAwait(false);
                var accepted = page.Captures
                    .Where(capture => !IsExcluded(capture, excludes))
                    .ToArray();
                seedRows += accepted.Length;
                excludedRows += page.Captures.Count - accepted.Length;
                store.InsertCdxPage(storedSeed.SeedId, accepted, page.ResumeKey);

                logger.LogInformation(
                    "Seed line {Line}: stored {Stored} captures from this page ({Excluded} excluded), resume={Resume}",
                    storedSeed.Seed.LineNumber,
                    accepted.Length,
                    page.Captures.Count - accepted.Length,
                    page.ResumeKey is null ? "no" : "yes");

                if (page.ResumeKey is null)
                {
                    store.MarkSeedComplete(storedSeed.SeedId);
                    logger.LogInformation(
                        "Completed seed line {Line}: {Stored} stored captures, {Excluded} excluded captures",
                        storedSeed.Seed.LineNumber,
                        seedRows,
                        excludedRows);
                    break;
                }

                resumeKey = page.ResumeKey;
            }
        }

        logger.LogInformation("CDX enumeration complete: {Count} captures", store.GetCaptureCount());
    }

    private async Task DownloadAllAsync(SqliteFrontendArchiveStore store, CancellationToken cancellationToken)
    {
        var total = store.GetCaptureCount();
        var completed = store.GetDownloadedCaptureCount();
        logger.LogInformation("Downloading Wayback replay bodies: {Completed}/{Total} captures already attempted", completed, total);

        while (completed < total)
        {
            var pending = store.GetPendingReplays(Math.Max(options.Concurrency * 4, 64));
            if (pending.Count == 0)
            {
                completed = store.GetDownloadedCaptureCount();
                break;
            }

            var channel = Channel.CreateBounded<ReplayWorkResult>(new BoundedChannelOptions(options.Concurrency * 2)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            var writer = Task.Run(async () =>
            {
                await foreach (var result in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (result.Download is not null)
                    {
                        var matches = UrlScanner.Scan(result.Download.Body, result.Capture.OriginalUrl);
                        store.InsertReplay(result.Capture.CaptureId, result.Download, matches);
                    }
                    else
                    {
                        store.MarkReplayFailure(result.Capture.CaptureId, result.ReplayUri, result.Error ?? "Unknown replay failure.");
                        logger.LogWarning(
                            "Replay failed for {Url} at {Timestamp}: {Error}",
                            result.Capture.OriginalUrl,
                            result.Capture.Timestamp,
                            result.Error);
                    }

                    completed++;
                    if (completed % 1_000 == 0 || completed == total)
                    {
                        logger.LogInformation(
                            "Attempted replay download for {Completed}/{Total} captures ({Contents} unique bodies, {Errors} replay errors)",
                            completed,
                            total,
                            store.GetContentCount(),
                            store.GetReplayErrorCount());
                    }
                }
            }, cancellationToken);

            try
            {
                await Parallel.ForEachAsync(
                    pending,
                    new ParallelOptions { MaxDegreeOfParallelism = options.Concurrency, CancellationToken = cancellationToken },
                    async (capture, token) =>
                    {
                        var result = await DownloadWithRetriesAsync(capture, token).ConfigureAwait(false);
                        await channel.Writer.WriteAsync(result, token).ConfigureAwait(false);
                    }).ConfigureAwait(false);

                channel.Writer.TryComplete();
                await writer.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
                throw;
            }
        }
    }

    private async Task<ReplayWorkResult> DownloadWithRetriesAsync(PendingReplayCapture capture, CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        var replayUri = waybackClient.BuildReplayUri(capture.Timestamp, capture.OriginalUrl);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var download = await waybackClient.DownloadReplayAsync(capture, cancellationToken).ConfigureAwait(false);
                return ReplayWorkResult.Success(capture, download);
            }
            catch (Exception ex) when (attempt < maxAttempts && WaybackCdxClient.IsTransient(ex))
            {
                var delay = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 250));
                logger.LogWarning(
                    "Transient replay failure for {Url} at {Timestamp}; retrying attempt {Attempt}/{MaxAttempts} after {DelayMs} ms: {Error}",
                    capture.OriginalUrl,
                    capture.Timestamp,
                    attempt,
                    maxAttempts,
                    delay.TotalMilliseconds,
                    ex.Message);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ReplayWorkResult.Failure(capture, replayUri, ex.Message);
            }
        }

        return ReplayWorkResult.Failure(capture, replayUri, "Retry loop ended unexpectedly.");
    }

    private void PreparePaths()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath) ?? ".");
        Directory.CreateDirectory(options.WorkDirectory);

        if (File.Exists(options.OutputPath) && !options.Replace)
        {
            throw new ArchiveFatalException($"Output archive already exists: {options.OutputPath}. Use --replace to overwrite it.");
        }

        if (File.Exists(InProgressPath) && !options.Resume && !options.Replace)
        {
            throw new ArchiveFatalException($"In-progress archive already exists: {InProgressPath}. Use --resume or --replace.");
        }

        if (options.Replace)
        {
            DeleteIfExists(InProgressPath);
        }
    }

    private void PublishFinalFile()
    {
        if (options.Replace)
        {
            DeleteIfExists(options.OutputPath);
        }

        File.Move(InProgressPath, options.OutputPath);
        File.SetAttributes(options.OutputPath, File.GetAttributes(options.OutputPath) | FileAttributes.ReadOnly);
    }

    private static bool IsExcluded(CdxCapture capture, IReadOnlyList<FrontendExclude> excludes)
    {
        if (excludes.Count == 0)
        {
            return false;
        }

        var canonical = UrlCanonicalizer.Canonicalize(capture.OriginalUrl);
        var hostPath = UrlCanonicalizer.TryToHostPathPrefix(capture.OriginalUrl);
        return excludes.Any(exclude =>
            canonical.StartsWith(exclude.CanonicalPrefix, StringComparison.Ordinal)
            || (hostPath is not null && hostPath.StartsWith(exclude.HostPathPrefix, StringComparison.Ordinal)));
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
    }

    private static void EnsureNoSqliteSidecars(string databasePath)
    {
        var sidecars = new[] { databasePath + "-wal", databasePath + "-shm" }
            .Where(File.Exists)
            .ToArray();
        if (sidecars.Length != 0)
        {
            throw new ArchiveFatalException("SQLite sidecar files remained after finalization: " + string.Join(", ", sidecars));
        }
    }

    private sealed record ReplayWorkResult(
        PendingReplayCapture Capture,
        Uri ReplayUri,
        ReplayDownload? Download,
        string? Error)
    {
        public static ReplayWorkResult Success(PendingReplayCapture capture, ReplayDownload download) =>
            new(capture, download.ReplayUri, download, null);

        public static ReplayWorkResult Failure(PendingReplayCapture capture, Uri replayUri, string error) =>
            new(capture, replayUri, null, error);
    }
}
