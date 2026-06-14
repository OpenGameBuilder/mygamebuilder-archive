using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace OpenGameBuilder.Mgb.Archive.S3Archiver;

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

        if (await ResumeCompletedOutputIfPresentAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (await PublishFinalizedInProgressArchiveIfPresentAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

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
        if (store.IsFinalRelistVerified(initialFingerprint))
        {
            logger.LogInformation("Skipping final re-list; fingerprint {Fingerprint} was already verified in a previous run", initialFingerprint);
        }
        else
        {
            logger.LogInformation("Re-enumerating bucket before finalization");
            var finalSummary = await RunTimedAsync(
                "Final re-list",
                () => ComputeCurrentListingSummaryAsync(cancellationToken)).ConfigureAwait(false);
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

            store.MarkFinalRelistVerified(finalSummary.Fingerprint);
        }

        if (store.IsFinalArchiveMaterialized())
        {
            logger.LogInformation("Skipping final table materialization; completed rows are already present in the in-progress archive");
        }
        else
        {
            await RunTimedAsync(
                "Materializing final archive tables",
                () =>
                {
                    store.MaterializeFinalTables(diagnostics);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
        }

        await RunTimedAsync(
            "Writing final archive metadata",
            () =>
            {
                store.FinalizeArchiveInfo(options);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

        if (store.IsValidationPassed())
        {
            logger.LogInformation("Skipping full archive validation; it already passed in a previous run");
        }
        else
        {
            var validation = await RunTimedAsync(
                "Validating final archive",
                () => ArchiveValidator.ValidateAsync(InProgressPath, cancellationToken)).ConfigureAwait(false);
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

            store.MarkValidationPassed();
        }

        logger.LogInformation("Validation passed; dropping capture staging tables");
        await RunTimedAsync(
            "Dropping capture staging tables",
            () =>
            {
                store.DropStagingTables();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        store.Dispose();
        Sqlite.ClearPools();
        await RunTimedAsync(
            "Checkpointing SQLite and switching to standalone journal mode",
            () =>
            {
                FinalizeStandaloneSqliteFile(InProgressPath);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        await RunTimedAsync(
            "Checking for SQLite sidecar files",
            () =>
            {
                EnsureNoSqliteSidecars(InProgressPath);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

        await RunTimedAsync(
            "Publishing final archive file",
            () =>
            {
                PublishFinalFile();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
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
        var missingContentTypes = store.GetDownloadedMissingContentTypeCount();
        var listedBytes = store.GetListedContentBytes();
        logger.LogInformation(
            "Downloading object bodies: {Downloaded}/{Total} objects and {DownloadedBytes}/{ListedBytes} bytes already complete ({MissingContentTypes} missing Content-Type)",
            downloaded,
            liveCount,
            downloadedBytes,
            listedBytes,
            missingContentTypes);

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
                    if (item.ContentType is null)
                    {
                        missingContentTypes++;
                        if (missingContentTypes <= 10 || missingContentTypes % 1_000 == 0)
                        {
                            logger.LogInformation(
                                "Recorded {MissingContentTypes} live objects with no Content-Type header; latest key: {Key}",
                                missingContentTypes,
                                item.Key);
                        }
                    }

                    if (downloaded % 10_000 == 0 || downloaded == liveCount)
                    {
                        logger.LogInformation(
                            "Downloaded {Downloaded}/{Total} live objects ({DownloadedBytes}/{ListedBytes} bytes, {MissingContentTypes} missing Content-Type)",
                            downloaded,
                            liveCount,
                            downloadedBytes,
                            listedBytes,
                            missingContentTypes);
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

        if (File.Exists(options.OutputPath) && !options.Replace && !options.Resume)
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

    private async Task<bool> ResumeCompletedOutputIfPresentAsync(CancellationToken cancellationToken)
    {
        if (!options.Resume || options.Replace || !File.Exists(options.OutputPath))
        {
            return false;
        }

        logger.LogInformation("Output archive already exists at {Output}; validating it for --resume", options.OutputPath);
        var validation = await RunTimedAsync(
            "Validating existing output archive",
            () => ArchiveValidator.ValidateAsync(options.OutputPath, cancellationToken)).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                diagnostics.Write("resume", "fatal", "Existing output archive failed validation.", detail: error);
            }

            throw new ArchiveFatalException($"Output archive already exists but did not validate: {options.OutputPath}. Use --replace to rebuild it.");
        }

        foreach (var warning in validation.Warnings)
        {
            diagnostics.Write("resume", "warning", warning);
            logger.LogWarning("{Warning}", warning);
        }

        logger.LogInformation("Existing output archive is valid; no resume work remains");
        return true;
    }

    private async Task<bool> PublishFinalizedInProgressArchiveIfPresentAsync(CancellationToken cancellationToken)
    {
        if (!options.Resume || options.Replace || !File.Exists(InProgressPath) || HasTable(InProgressPath, "capture_listing"))
        {
            return false;
        }

        logger.LogInformation("In-progress archive has no capture staging tables; validating it before publishing");
        var validation = await RunTimedAsync(
            "Validating finalized in-progress archive",
            () => ArchiveValidator.ValidateAsync(InProgressPath, cancellationToken)).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return false;
        }

        foreach (var warning in validation.Warnings)
        {
            diagnostics.Write("resume", "warning", warning);
            logger.LogWarning("{Warning}", warning);
        }

        await RunTimedAsync(
            "Checkpointing SQLite and switching to standalone journal mode",
            () =>
            {
                FinalizeStandaloneSqliteFile(InProgressPath);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        await RunTimedAsync(
            "Checking for SQLite sidecar files",
            () =>
            {
                EnsureNoSqliteSidecars(InProgressPath);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        await RunTimedAsync(
            "Publishing final archive file",
            () =>
            {
                PublishFinalFile();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        logger.LogInformation("Created immutable SQLite archive at {Output}", options.OutputPath);
        return true;
    }

    private async Task<T> RunTimedAsync<T>(string operation, Func<Task<T>> action)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("{Operation} started", operation);
        try
        {
            var result = await action().ConfigureAwait(false);
            logger.LogInformation("{Operation} completed in {Elapsed}", operation, stopwatch.Elapsed);
            diagnostics.Write("finalize", "info", operation + " completed.", detail: new { elapsedMilliseconds = stopwatch.ElapsedMilliseconds });
            return result;
        }
        catch
        {
            logger.LogWarning("{Operation} failed after {Elapsed}", operation, stopwatch.Elapsed);
            throw;
        }
    }

    private async Task RunTimedAsync(string operation, Func<Task> action)
    {
        await RunTimedAsync(
            operation,
            async () =>
            {
                await action().ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
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

    private static bool HasTable(string databasePath, string tableName)
    {
        using var connection = Sqlite.OpenReadOnly(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0) != 0;
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

    private static void FinalizeStandaloneSqliteFile(string databasePath)
    {
        using (var connection = Sqlite.Open(databasePath))
        {
            RequireWalCheckpoint(connection);
            var journalMode = Sqlite.ExecuteScalar(connection, "PRAGMA journal_mode = DELETE;") as string;
            if (!string.Equals(journalMode, "delete", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArchiveFatalException($"SQLite refused to switch the final archive to DELETE journal mode; current mode is '{journalMode}'.");
            }

            Sqlite.ExecuteNonQuery(connection, "PRAGMA optimize;");
        }

        Sqlite.ClearPools();
        foreach (var sidecar in new[] { databasePath + "-wal", databasePath + "-shm" })
        {
            if (!File.Exists(sidecar))
            {
                continue;
            }

            var info = new FileInfo(sidecar);
            if (info.Length == 0)
            {
                info.Delete();
            }
        }
    }

    private static void RequireWalCheckpoint(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new ArchiveFatalException("SQLite did not return a WAL checkpoint result.");
        }

        var busy = reader.GetInt64(0);
        if (busy != 0)
        {
            throw new ArchiveFatalException("SQLite WAL checkpoint could not complete because another connection is using the archive.");
        }
    }
}
