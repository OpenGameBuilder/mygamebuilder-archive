using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace MyGameBuilder.Archive.S3;

public sealed class CaptureWorkflow(
    ArchiveOptions options,
    S3ArchiveClient s3Client,
    DiagnosticsWriter diagnostics,
    ILogger<CaptureWorkflow> logger)
{
    private string InProgressPath => options.OutputPath + ".inprogress.sqlite";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        PreparePaths();

        using var store = new SqliteArchiveStore(InProgressPath);
        store.Initialize();

        if (!options.Resume || !store.IsListingComplete())
        {
            logger.LogInformation("Enumerating all visible S3 versions in bucket {Bucket}", options.Bucket);
            store.ResetListing();
            var summary = await EnumerateAsync(store, cancellationToken).ConfigureAwait(false);
            store.CompleteListing(summary.Fingerprint);
            logger.LogInformation(
                "Enumeration complete: {Count} entries ({Live} live, {DeleteMarkers} delete markers), {Bytes} listed bytes, fingerprint {Fingerprint}",
                summary.EntryCount,
                summary.LiveEntryCount,
                summary.DeleteMarkerCount,
                summary.ListedContentBytes,
                summary.Fingerprint);
            diagnostics.Write("listing", "info", "Enumeration complete.", detail: summary);
        }
        else
        {
            logger.LogInformation("Resuming from complete listing: {Count} entries, fingerprint {Fingerprint}", store.GetListingCount(), store.GetListingFingerprint());
        }

        await ProbeUnavailableMetadataApisAsync(store, cancellationToken).ConfigureAwait(false);
        await DownloadAllAsync(store, cancellationToken).ConfigureAwait(false);

        var initialFingerprint = store.GetListingFingerprint()
            ?? throw new ArchiveFatalException("Capture listing fingerprint is missing.");
        logger.LogInformation("Re-enumerating bucket before finalization");
        var finalSummary = await ComputeCurrentListingSummaryAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(initialFingerprint, finalSummary.Fingerprint, StringComparison.Ordinal))
        {
            diagnostics.Write(
                "final-relist",
                "fatal",
                "Bucket listing changed between initial capture and final validation.",
                detail: new
                {
                    initialFingerprint,
                    finalFingerprint = finalSummary.Fingerprint,
                    initialCount = store.GetListingCount(),
                    finalSummary.EntryCount,
                    finalSummary.LiveEntryCount,
                    finalSummary.DeleteMarkerCount,
                    finalSummary.ListedContentBytes
                });
            throw new ArchiveFatalException("Bucket listing changed during capture; final archive was not produced.");
        }

        logger.LogInformation("Materializing final archive tables");
        store.MaterializeFinalTables(diagnostics);
        store.FinalizeArchiveInfo(options);

        var validation = await ArchiveValidator.ValidateAsync(InProgressPath, cancellationToken).ConfigureAwait(false);
        foreach (var warning in validation.Warnings)
        {
            diagnostics.Write("validate", "warning", warning);
            logger.LogWarning("{Warning}", warning);
        }

        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                diagnostics.Write("validate", "fatal", error);
            }

            throw new ArchiveFatalException("Archive validation failed; final archive was not produced.");
        }

        logger.LogInformation("Validation passed; dropping capture staging tables");
        store.DropStagingTables();
        store.Dispose();
        Sqlite.ClearPools();
        EnsureNoSqliteSidecars(InProgressPath);

        PublishFinalFile();
        logger.LogInformation("Created immutable SQLite archive at {Output}", options.OutputPath);
    }

    private async Task<ListingSummary> EnumerateAsync(SqliteArchiveStore store, CancellationToken cancellationToken)
    {
        using var hasher = new ListingHasher();
        string? keyMarker = null;
        string? versionIdMarker = null;
        long nextOrdinal = 0;
        long liveCount = 0;
        long deleteMarkerCount = 0;
        long listedBytes = 0;
        var pageCount = 0;

        while (true)
        {
            var page = await s3Client.ListObjectVersionsAsync(keyMarker, versionIdMarker, nextOrdinal, cancellationToken).ConfigureAwait(false);
            foreach (var entry in page.Entries)
            {
                hasher.Add(entry);
                if (entry.IsDeleteMarker)
                {
                    deleteMarkerCount++;
                }
                else
                {
                    liveCount++;
                    listedBytes += entry.ContentLengthBytes ?? 0;
                }
            }

            store.InsertListingPage(page.Entries);
            nextOrdinal += page.Entries.Count;
            pageCount++;

            if (pageCount % 100 == 0 || !page.IsTruncated)
            {
                logger.LogInformation(
                    "Listed {Count} entries across {Pages} pages ({Live} live, {DeleteMarkers} delete markers, {Bytes} bytes)",
                    nextOrdinal,
                    pageCount,
                    liveCount,
                    deleteMarkerCount,
                    listedBytes);
            }

            if (!page.IsTruncated)
            {
                return new ListingSummary(hasher.GetHashAndReset(), nextOrdinal, liveCount, deleteMarkerCount, listedBytes);
            }

            keyMarker = page.NextKeyMarker;
            versionIdMarker = page.NextVersionIdMarker;
        }
    }

    private async Task<ListingSummary> ComputeCurrentListingSummaryAsync(CancellationToken cancellationToken)
    {
        using var hasher = new ListingHasher();
        string? keyMarker = null;
        string? versionIdMarker = null;
        long nextOrdinal = 0;
        long liveCount = 0;
        long deleteMarkerCount = 0;
        long listedBytes = 0;
        var pageCount = 0;

        while (true)
        {
            var page = await s3Client.ListObjectVersionsAsync(keyMarker, versionIdMarker, nextOrdinal, cancellationToken).ConfigureAwait(false);
            foreach (var entry in page.Entries)
            {
                hasher.Add(entry);
                if (entry.IsDeleteMarker)
                {
                    deleteMarkerCount++;
                }
                else
                {
                    liveCount++;
                    listedBytes += entry.ContentLengthBytes ?? 0;
                }
            }

            nextOrdinal += page.Entries.Count;
            pageCount++;
            if (pageCount % 500 == 0 || !page.IsTruncated)
            {
                logger.LogInformation(
                    "Final re-list checked {Count} entries across {Pages} pages",
                    nextOrdinal,
                    pageCount);
            }

            if (!page.IsTruncated)
            {
                return new ListingSummary(hasher.GetHashAndReset(), nextOrdinal, liveCount, deleteMarkerCount, listedBytes);
            }

            keyMarker = page.NextKeyMarker;
            versionIdMarker = page.NextVersionIdMarker;
        }
    }

    private async Task DownloadAllAsync(SqliteArchiveStore store, CancellationToken cancellationToken)
    {
        var liveCount = store.GetLiveCount();
        var downloaded = store.GetDownloadedLiveCount();
        var downloadedBytes = store.GetDownloadedBytes();
        var listedBytes = store.GetListedContentBytes();
        logger.LogInformation(
            "Downloading object bodies: {Downloaded}/{Total} objects and {DownloadedBytes}/{ListedBytes} bytes already complete",
            downloaded,
            liveCount,
            downloadedBytes,
            listedBytes);

        while (downloaded < liveCount)
        {
            var pending = store.GetPendingDownloads(Math.Max(options.Concurrency * 4, 64));
            if (pending.Count == 0)
            {
                downloaded = store.GetDownloadedLiveCount();
                continue;
            }

            var channel = Channel.CreateBounded<DownloadedObject>(new BoundedChannelOptions(options.Concurrency * 2)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            var writer = Task.Run(async () =>
            {
                await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    store.InsertDownload(item);
                    downloaded++;
                    downloadedBytes += item.ContentLengthBytes;
                    if (downloaded % 10_000 == 0 || downloaded == liveCount)
                    {
                        logger.LogInformation(
                            "Downloaded {Downloaded}/{Total} live objects ({DownloadedBytes}/{ListedBytes} bytes)",
                            downloaded,
                            liveCount,
                            downloadedBytes,
                            listedBytes);
                    }
                }
            }, cancellationToken);

            try
            {
                await Parallel.ForEachAsync(
                    pending,
                    new ParallelOptions { MaxDegreeOfParallelism = options.Concurrency, CancellationToken = cancellationToken },
                    async (entry, token) =>
                    {
                        var download = await DownloadWithRetriesAsync(entry, token).ConfigureAwait(false);
                        await channel.Writer.WriteAsync(download, token).ConfigureAwait(false);
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

    private async Task<DownloadedObject> DownloadWithRetriesAsync(ListedS3Entry entry, CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await s3Client.DownloadObjectAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts && S3ArchiveClient.IsTransient(ex))
            {
                var delay = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 250));
                diagnostics.Write(
                    "download",
                    "warning",
                    "Transient download failure; retrying.",
                    entry.Key,
                    entry.RawVersionId,
                    new { attempt, delayMilliseconds = delay.TotalMilliseconds, error = ex.Message });
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                diagnostics.Write(
                    "download",
                    "fatal",
                    "Permanent download failure.",
                    entry.Key,
                    entry.RawVersionId,
                    new { attempt, error = ex.Message });
                throw;
            }
        }

        throw new ArchiveFatalException($"Retry loop ended unexpectedly for '{entry.Key}'.");
    }

    private async Task ProbeUnavailableMetadataApisAsync(SqliteArchiveStore store, CancellationToken cancellationToken)
    {
        if (store.GetFirstLiveEntry() is not { } firstLive)
        {
            logger.LogInformation("No live objects found; skipping object tagging/ACL probe");
            return;
        }

        await ProbeSubresourceAsync(store, firstLive, "tagging", "anonymous_object_tagging_probe_status", cancellationToken).ConfigureAwait(false);
        await ProbeSubresourceAsync(store, firstLive, "acl", "anonymous_object_acl_probe_status", cancellationToken).ConfigureAwait(false);
    }

    private async Task ProbeSubresourceAsync(SqliteArchiveStore store, ListedS3Entry entry, string subresource, string stateName, CancellationToken cancellationToken)
    {
        var status = await s3Client.ProbeObjectSubresourceStatusAsync(entry, subresource, cancellationToken).ConfigureAwait(false);
        store.SetCaptureState(stateName, status.ToString(System.Globalization.CultureInfo.InvariantCulture));
        diagnostics.Write(
            "metadata-probe",
            status is 403 or 405 ? "info" : "warning",
            $"Anonymous object {subresource} probe returned HTTP {status}.",
            entry.Key,
            entry.RawVersionId);

        if (status == 200)
        {
            throw new ArchiveFatalException($"Anonymous object {subresource} metadata is accessible. Refusing to produce an archive until {subresource} capture is implemented.");
        }

        logger.LogInformation("Anonymous object {Subresource} probe returned HTTP {Status}", subresource, status);
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
}
