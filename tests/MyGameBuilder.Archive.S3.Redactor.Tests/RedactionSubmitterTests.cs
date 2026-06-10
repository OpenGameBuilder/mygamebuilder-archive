using Microsoft.Data.Sqlite;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace MyGameBuilder.Archive.S3.Redactor.Tests;

public sealed class RedactionSubmitterTests
{
    [Fact]
    public void SubmitRedactsManualTilesAndPropagatedScreenshotsWithoutChangingSource()
    {
        var directory = TestFiles.CreateDirectory();
        var archivePath = Path.Combine(directory, "archive.sqlite");
        var reviewPath = Path.Combine(directory, "review.sqlite");
        var outputPath = Path.Combine(directory, "archive.redacted.sqlite");
        var tileBody = TestFiles.PngWithUniqueColors(3);
        var screenshotBody = TestFiles.PngWithUniqueColors(4);
        TestFiles.CreateArchive(
            archivePath,
            [
                TestEntry.Tile(1, "alice", "project1", "face", tileBody),
                TestEntry.Actor(2, "alice", "project1", "person", TestFiles.ActorReferencingTile("face")),
                TestEntry.Map(3, "alice", "project1", "level1", TestFiles.MapReferencingActor("person")),
                TestEntry.Screenshot(4, "alice", "project1", "level1", screenshotBody)
            ]);

        var archive = new ArchiveDb(archivePath);
        var store = new ReviewStore(reviewPath);
        store.Initialize(archivePath, threshold: 2);
        store.SetScanTotal(archive.CountManualReviewPngEntries());
        foreach (var entry in archive.GetManualReviewPngEntriesAfter(0, 100))
        {
            var inspection = PngInspector.Inspect(entry.Body);
            store.RecordScanned(entry, inspection, accepted: inspection.VisibleUniqueColorCount >= 2, error: null);
        }

        store.SetDecision(1, ReviewStatus.Redacted);
        var submitter = new RedactionSubmitter(archive, store, new ScreenshotPropagation(archive));

        var result = submitter.Submit(outputPath, threshold: 2);

        Assert.Equal(1, result.ManualRedactedCount);
        Assert.Equal(1, result.PropagatedScreenshotCount);
        Assert.Equal(2, result.TotalRedactedCount);
        Assert.Equal(tileBody, ReadBody(archivePath, 1));
        Assert.Equal(screenshotBody, ReadBody(archivePath, 4));
        AssertBlackPng(ReadBody(outputPath, 1));
        AssertBlackPng(ReadBody(outputPath, 4));
        Assert.Equal(1, PngInspector.Inspect(ReadBody(outputPath, 1)).VisibleUniqueColorCount);
    }

    private static byte[] ReadBody(string path, long entryId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM s3_entry WHERE entry_id = $entry_id;";
        command.Parameters.AddWithValue("$entry_id", entryId);
        return (byte[])command.ExecuteScalar()!;
    }

    private static void AssertBlackPng(byte[] body)
    {
        using var image = Image.Load<Rgba32>(body);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    Assert.Equal(new Rgba32(0, 0, 0, 255), row[x]);
                }
            }
        });
    }
}
