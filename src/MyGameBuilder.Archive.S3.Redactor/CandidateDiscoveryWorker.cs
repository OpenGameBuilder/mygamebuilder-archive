namespace MyGameBuilder.Archive.S3.Redactor;

public sealed class CandidateDiscoveryWorker : BackgroundService
{
    private const int BatchSize = 50;
    private readonly RedactorRuntime _runtime;
    private readonly ILogger<CandidateDiscoveryWorker> _logger;

    public CandidateDiscoveryWorker(RedactorRuntime runtime, ILogger<CandidateDiscoveryWorker> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_runtime.IsReady)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var lastScannedEntryId = _runtime.ReviewStore.GetLastScannedEntryId();
            var batch = _runtime.Archive.GetManualReviewPngEntriesAfter(lastScannedEntryId, BatchSize);
            if (batch.Count == 0)
            {
                return;
            }

            foreach (var entry in batch)
            {
                stoppingToken.ThrowIfCancellationRequested();
                try
                {
                    var inspection = PngInspector.Inspect(entry.Body, _runtime.Options.UniqueColorThreshold);
                    var accepted = inspection.VisibleUniqueColorCount >= _runtime.Options.UniqueColorThreshold;
                    _runtime.ReviewStore.RecordScanned(entry, inspection, accepted, error: null);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to inspect PNG entry {EntryId}.", entry.EntryId);
                    _runtime.ReviewStore.RecordScanned(entry, inspection: null, accepted: false, error: ex.Message);
                }
            }

            await Task.Yield();
        }
    }
}
