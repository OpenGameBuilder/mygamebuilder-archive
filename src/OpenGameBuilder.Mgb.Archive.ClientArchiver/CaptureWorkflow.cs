using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace OpenGameBuilder.Mgb.Archive.ClientArchiver;

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
        if (options.RetryReplayErrors)
        {
            var reset = store.ResetReplayErrors();
            logger.LogInformation("Reset {Count} recorded replay errors so they can be retried", reset);
        }

        await EnumerateAsync(store, seedFile.Excludes, cancellationToken).ConfigureAwait(false);
        var prunedErrorOnlyResources = store.PruneResourcesWithOnlyErrorStatuses();
        if (prunedErrorOnlyResources != 0)
        {
            logger.LogInformation(
                "Pruned {Count} resources whose CDX captures all had HTTP error status codes",
                prunedErrorOnlyResources);
        }

        var discoveredUrls = await DownloadAllAsync(store, seedFile.Excludes, cancellationToken).ConfigureAwait(false);
        await CaptureLiveUrlsAsync(store, seedFile, discoveredUrls, cancellationToken).ConfigureAwait(false);

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
                var page = await GetCdxPageWithRetriesAsync(storedSeed.Seed, resumeKey, cancellationToken).ConfigureAwait(false);
                var accepted = page.Captures
                    .Where(capture => !FrontendUrlExcluder.IsExcluded(capture, excludes))
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

    private async Task<CdxPage> GetCdxPageWithRetriesAsync(FrontendSeed seed, string? resumeKey, CancellationToken cancellationToken)
    {
        const int maxAttempts = 8;
        Exception? finalTransientException = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await waybackClient.GetCdxPageAsync(seed, resumeKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (WaybackCdxClient.IsTransient(ex))
            {
                finalTransientException = ex;
                if (attempt == maxAttempts)
                {
                    break;
                }

                var delay = ComputeRetryDelay(attempt, TimeSpan.FromSeconds(30));
                logger.LogWarning(
                    "Transient CDX failure for seed line {Line} ({Seed}); retrying attempt {Attempt}/{MaxAttempts} after {DelayMs} ms: {Error}",
                    seed.LineNumber,
                    seed.RawText,
                    attempt,
                    maxAttempts,
                    delay.TotalMilliseconds,
                    ex.Message);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new ArchiveFatalException(
            $"CDX query failed for seed line {seed.LineNumber} ({seed.RawText}) after {maxAttempts} attempts. "
            + "The in-progress archive can be continued with --resume after web.archive.org is reachable. "
            + "Last error: "
            + (finalTransientException?.Message ?? "unknown transient failure"));
    }

    private async Task<IReadOnlySet<string>> DownloadAllAsync(SqliteFrontendArchiveStore store, IReadOnlyList<FrontendExclude> excludes, CancellationToken cancellationToken)
    {
        var total = store.GetCaptureCount();
        var completed = store.GetDownloadedCaptureCount();
        var deferredTransientFailures = new HashSet<long>();
        var discoveredUrls = new HashSet<string>(StringComparer.Ordinal);
        logger.LogInformation("Downloading Wayback replay bodies: {Completed}/{Total} captures already attempted", completed, total);

        while (true)
        {
            var pending = store.GetPendingReplays(Math.Max(options.Concurrency * 4, 64), deferredTransientFailures);
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
                        if (FrontendUrlExcluder.IsExcludedReplayRedirect(result.Capture.OriginalUrl, result.Download, excludes))
                        {
                            store.DeleteCapture(result.Capture.CaptureId);
                            logger.LogInformation(
                                "Skipped replay redirect for {Url} at {Timestamp} because it targets an excluded URL",
                                result.Capture.OriginalUrl,
                                result.Capture.Timestamp);
                        }
                        else
                        {
                            store.InsertReplay(result.Capture.CaptureId, result.Download);
                            if (result.Download.StatusCode is >= 200 and <= 299)
                            {
                                foreach (var url in UrlScanner.Scan(result.Download.Body, result.Capture.OriginalUrl))
                                {
                                    discoveredUrls.Add(url);
                                }
                            }
                        }
                    }
                    else if (result.IsTransientExhausted)
                    {
                        deferredTransientFailures.Add(result.Capture.CaptureId);
                        logger.LogWarning(
                            "Replay download for {Url} at {Timestamp} exhausted transient retries and was left pending for a future --resume run: {Error}",
                            result.Capture.OriginalUrl,
                            result.Capture.Timestamp,
                            result.Error);
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

        var pendingAfterRun = store.GetPendingReplayCount();
        if (deferredTransientFailures.Count != 0 || pendingAfterRun != 0)
        {
            throw new ArchiveFatalException(
                $"{pendingAfterRun} replay downloads remain pending after this run; "
                + $"{deferredTransientFailures.Count} exhausted transient retries in this process. "
                + "They were left pending instead of being recorded as missing data; rerun capture with --resume when web.archive.org is healthier.");
        }

        return discoveredUrls;
    }

    private async Task CaptureLiveUrlsAsync(
        SqliteFrontendArchiveStore store,
        FrontendSeedFile seedFile,
        IReadOnlySet<string> discoveredUrls,
        CancellationToken cancellationToken)
    {
        var urls = BuildLiveCaptureUrlList(seedFile, discoveredUrls);
        if (urls.Count == 0)
        {
            logger.LogInformation("No direct live URLs to capture");
            return;
        }

        var timestamp = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        logger.LogInformation("Capturing {Count} direct live URLs with timestamp {Timestamp}", urls.Count, timestamp);

        var channel = Channel.CreateBounded<LiveCaptureWorkResult>(new BoundedChannelOptions(options.Concurrency * 2)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        var stored = 0;
        var skipped = 0;
        var failed = 0;
        var writer = Task.Run(async () =>
        {
            await foreach (var result in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (result.Download is not null)
                {
                    if (FrontendUrlExcluder.IsExcludedReplayRedirect(result.OriginalUrl, result.Download, seedFile.Excludes))
                    {
                        skipped++;
                        logger.LogInformation(
                            "Skipped direct live URL {Url} because it redirects into an excluded scope",
                            result.OriginalUrl);
                    }
                    else if (result.Download.StatusCode is >= 400 and <= 599)
                    {
                        skipped++;
                        logger.LogInformation(
                            "Skipped direct live URL {Url} because it returned HTTP {StatusCode}",
                            result.OriginalUrl,
                            result.Download.StatusCode);
                    }
                    else
                    {
                        store.InsertDirectCapture(result.OriginalUrl, timestamp, result.Download);
                        stored++;
                    }
                }
                else
                {
                    store.InsertDirectCaptureFailure(result.OriginalUrl, timestamp, result.Uri, result.Error ?? "Unknown direct live capture failure.");
                    failed++;
                    logger.LogWarning("Direct live capture failed for {Url}: {Error}", result.OriginalUrl, result.Error);
                }
            }
        }, cancellationToken);

        try
        {
            await Parallel.ForEachAsync(
                urls,
                new ParallelOptions { MaxDegreeOfParallelism = options.Concurrency, CancellationToken = cancellationToken },
                async (url, token) =>
                {
                    var result = await DownloadDirectWithRetriesAsync(url, seedFile.Excludes, token).ConfigureAwait(false);
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

        logger.LogInformation(
            "Direct live capture complete: {Stored} stored, {Skipped} skipped, {Failed} failures recorded",
            stored,
            skipped,
            failed);
    }

    private IReadOnlyList<string> BuildLiveCaptureUrlList(FrontendSeedFile seedFile, IReadOnlySet<string> discoveredUrls)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var seed in seedFile.Seeds.Where(seed => seed.Kind == FrontendSeedKind.Url))
        {
            Add(seed.Value);
        }

        foreach (var url in discoveredUrls)
        {
            Add(url);
        }

        return urls;

        void Add(string url)
        {
            var trimmed = url.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                || uri.Scheme is not "http" and not "https"
                || FrontendUrlExcluder.IsExcludedUrl(trimmed, seedFile.Excludes)
                || !seen.Add(trimmed))
            {
                return;
            }

            urls.Add(trimmed);
        }
    }

    private async Task<LiveCaptureWorkResult> DownloadDirectWithRetriesAsync(
        string url,
        IReadOnlyList<FrontendExclude> excludes,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        var uri = new Uri(url);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var download = await DownloadDirectFollowingRedirectsAsync(url, uri, excludes, cancellationToken).ConfigureAwait(false);
                return LiveCaptureWorkResult.Success(url, uri, download);
            }
            catch (Exception ex) when (WaybackCdxClient.IsTransient(ex))
            {
                if (attempt == maxAttempts)
                {
                    return LiveCaptureWorkResult.Failure(url, uri, ex.Message);
                }

                var delay = ComputeRetryDelay(attempt, TimeSpan.FromSeconds(8));
                logger.LogWarning(
                    "Transient direct live capture failure for {Url}; retrying attempt {Attempt}/{MaxAttempts} after {DelayMs} ms: {Error}",
                    url,
                    attempt,
                    maxAttempts,
                    delay.TotalMilliseconds,
                    ex.Message);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return LiveCaptureWorkResult.Failure(url, uri, ex.Message);
            }
        }

        return LiveCaptureWorkResult.Failure(url, uri, "Retry loop ended unexpectedly.");
    }

    private async Task<ReplayDownload> DownloadDirectFollowingRedirectsAsync(
        string originalUrl,
        Uri startUri,
        IReadOnlyList<FrontendExclude> excludes,
        CancellationToken cancellationToken)
    {
        const int maxRedirects = 8;
        var uri = startUri;
        for (var redirectCount = 0; redirectCount <= maxRedirects; redirectCount++)
        {
            var download = await waybackClient.DownloadDirectAsync(uri, cancellationToken).ConfigureAwait(false);
            if (download.StatusCode is < 300 or > 399
                || FrontendUrlExcluder.IsExcludedReplayRedirect(originalUrl, download, excludes))
            {
                return download;
            }

            var location = download.Headers
                .Where(header => header.Name.Equals("location", StringComparison.OrdinalIgnoreCase))
                .Select(header => header.Value)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(location))
            {
                return download;
            }

            if (!Uri.TryCreate(uri, location.Trim(), out var nextUri)
                || nextUri.Scheme is not "http" and not "https")
            {
                return download;
            }

            uri = nextUri;
        }

        throw new ArchiveFatalException($"Direct live capture exceeded {maxRedirects} redirects for {originalUrl}.");
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
            catch (Exception ex) when (WaybackCdxClient.IsTransient(ex))
            {
                if (attempt == maxAttempts)
                {
                    return ReplayWorkResult.TransientExhausted(capture, replayUri, ex.Message);
                }

                var delay = ComputeRetryDelay(attempt, TimeSpan.FromSeconds(8));
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

    private static TimeSpan ComputeRetryDelay(int attempt, TimeSpan maxDelay)
    {
        var milliseconds = Math.Min(maxDelay.TotalMilliseconds, 500 * Math.Pow(2, attempt - 1));
        return TimeSpan.FromMilliseconds(milliseconds + Random.Shared.Next(0, 500));
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
        string? Error,
        bool IsTransientExhausted)
    {
        public static ReplayWorkResult Success(PendingReplayCapture capture, ReplayDownload download) =>
            new(capture, download.ReplayUri, download, null, IsTransientExhausted: false);

        public static ReplayWorkResult Failure(PendingReplayCapture capture, Uri replayUri, string error) =>
            new(capture, replayUri, null, error, IsTransientExhausted: false);

        public static ReplayWorkResult TransientExhausted(PendingReplayCapture capture, Uri replayUri, string error) =>
            new(capture, replayUri, null, error, IsTransientExhausted: true);
    }

    private sealed record LiveCaptureWorkResult(
        string OriginalUrl,
        Uri Uri,
        ReplayDownload? Download,
        string? Error)
    {
        public static LiveCaptureWorkResult Success(string originalUrl, Uri uri, ReplayDownload download) =>
            new(originalUrl, uri, download, null);

        public static LiveCaptureWorkResult Failure(string originalUrl, Uri uri, string error) =>
            new(originalUrl, uri, null, error);
    }
}
