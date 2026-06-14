using Xunit;

namespace OpenGameBuilder.Mgb.Archive.S3Redactor.Tests;

public sealed class ReviewStoreTests
{
    [Fact]
    public void DecisionsAndCurrentPositionResumeFromSidecar()
    {
        var directory = TestFiles.CreateDirectory();
        var reviewPath = Path.Combine(directory, "review.sqlite");
        var store = new ReviewStore(reviewPath);
        store.Initialize(Path.Combine(directory, "archive.sqlite"), threshold: 2);
        store.SetScanTotal(2);
        var first = new PngArchiveEntry(1, 1, "alice/project1/tile/a", "alice", "project1", "tile", "a", "image/png", TestFiles.PngWithUniqueColors(2));
        var second = new PngArchiveEntry(2, 2, "alice/project1/tile/b", "alice", "project1", "tile", "b", "image/png", TestFiles.PngWithUniqueColors(2));
        store.RecordScanned(first, PngInspector.Inspect(first.Body), accepted: true, error: null);
        store.RecordScanned(second, PngInspector.Inspect(second.Body), accepted: true, error: null);

        var initialState = store.GetStateDto(new RedactorOptions(Path.Combine(directory, "archive.sqlite"), reviewPath, null, 2));

        Assert.Null(initialState.Previous);
        Assert.Equal(1, initialState.Current?.EntryId);
        Assert.Equal(2, initialState.Next?.EntryId);
        Assert.Equal([1L, 2L], initialState.ReviewWindow.Select(candidate => candidate.EntryId));

        store.SetDecision(1, ReviewStatus.Approved);

        var resumed = new ReviewStore(reviewPath);
        resumed.Initialize(Path.Combine(directory, "archive.sqlite"), threshold: 2);
        var state = resumed.GetStateDto(new RedactorOptions(Path.Combine(directory, "archive.sqlite"), reviewPath, null, 2));

        Assert.Equal(2, state.Counts.Total);
        Assert.Equal(1, state.Counts.Approved);
        Assert.Equal(2, state.Scan.Processed);
        Assert.True(state.Scan.Complete);
        Assert.Equal(1, state.CurrentIndex);
        Assert.Equal(2, state.Current?.EntryId);
        Assert.Equal(1, state.Previous?.EntryId);
        Assert.Null(state.Next);
    }

    [Fact]
    public void BatchPersistsDecisionsAndFinalCurrentPosition()
    {
        var directory = TestFiles.CreateDirectory();
        var reviewPath = Path.Combine(directory, "review.sqlite");
        var archivePath = Path.Combine(directory, "archive.sqlite");
        var store = new ReviewStore(reviewPath);
        store.Initialize(archivePath, threshold: 2);
        store.SetScanTotal(3);
        for (var entryId = 1; entryId <= 3; entryId++)
        {
            var entry = new PngArchiveEntry(entryId, entryId, $"alice/project1/tile/{entryId}", "alice", "project1", "tile", entryId.ToString(), "image/png", TestFiles.PngWithUniqueColors(2));
            store.RecordScanned(entry, PngInspector.Inspect(entry.Body), accepted: true, error: null);
        }

        store.ApplyBatch(
            [
                new DecisionRequest(1, ReviewStatus.Approved),
                new DecisionRequest(2, ReviewStatus.Redacted)
            ],
            currentIndex: 2);

        var state = store.GetStateDto(new RedactorOptions(archivePath, reviewPath, null, 2));

        Assert.Equal(2, state.CurrentIndex);
        Assert.Equal(3, state.Current?.EntryId);
        Assert.Equal(1, state.Counts.Approved);
        Assert.Equal(1, state.Counts.Redacted);
        Assert.Equal(1, state.Counts.Unreviewed);
    }

    [Fact]
    public async Task ConcurrentStateReadsAndBatchWritesDoNotLock()
    {
        var directory = TestFiles.CreateDirectory();
        var reviewPath = Path.Combine(directory, "review.sqlite");
        var archivePath = Path.Combine(directory, "archive.sqlite");
        var options = new RedactorOptions(archivePath, reviewPath, null, 2);
        var store = new ReviewStore(reviewPath);
        store.Initialize(archivePath, threshold: 2);
        store.SetScanTotal(12);
        for (var entryId = 1; entryId <= 12; entryId++)
        {
            var entry = new PngArchiveEntry(entryId, entryId, $"alice/project1/tile/{entryId}", "alice", "project1", "tile", entryId.ToString(), "image/png", TestFiles.PngWithUniqueColors(2));
            store.RecordScanned(entry, PngInspector.Inspect(entry.Body), accepted: true, error: null);
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var readTasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < 30; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    store.GetStateDto(options, i % 12);
                }
            }, cancellationToken));
        var writeTask = Task.Run(() =>
        {
            for (var i = 0; i < 30; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entryId = (i % 12) + 1;
                store.ApplyBatch([new DecisionRequest(entryId, ReviewStatus.Approved)], entryId - 1);
            }
        }, cancellationToken);

        await Task.WhenAll(readTasks.Append(writeTask));

        var state = store.GetStateDto(options);

        Assert.Equal(12, state.Counts.Total);
    }
}
