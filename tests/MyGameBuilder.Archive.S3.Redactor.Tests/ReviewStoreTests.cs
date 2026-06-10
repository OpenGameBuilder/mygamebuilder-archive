using Xunit;

namespace MyGameBuilder.Archive.S3.Redactor.Tests;

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
    }
}
