using Xunit;
using ZstdSharp;

namespace OpenGameBuilder.Mgb.Archive.S3Archiver.Tests;

public sealed class FileSplitterTests
{
    [Fact]
    public async Task SplitCreatesNumberedPartsWithOriginalBytes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mgb-file-splitter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var inputPath = Path.Combine(directory, "archive.sqlite.zst");
        var bytes = Enumerable.Range(0, 23).Select(static value => (byte)value).ToArray();
        await File.WriteAllBytesAsync(inputPath, bytes, TestContext.Current.CancellationToken);

        var parts = await FileSplitter.SplitAsync(
            inputPath,
            inputPath,
            partSizeBytes: 5,
            replace: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                inputPath + ".part-000",
                inputPath + ".part-001",
                inputPath + ".part-002",
                inputPath + ".part-003",
                inputPath + ".part-004"
            ],
            parts);
        Assert.Equal([5, 5, 5, 5, 3], parts.Select(path => (int)new FileInfo(path).Length));

        using var combined = new MemoryStream();
        foreach (var part in parts)
        {
            await using var stream = File.OpenRead(part);
            await stream.CopyToAsync(combined, TestContext.Current.CancellationToken);
        }

        Assert.Equal(bytes, combined.ToArray());
    }

    [Fact]
    public async Task SplitRefusesToOverwriteExistingPartWithoutReplace()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mgb-file-splitter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var inputPath = Path.Combine(directory, "archive.sqlite.zst");
        await File.WriteAllBytesAsync(inputPath, [1, 2, 3], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(inputPath + ".part-000", [9], TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<ArchiveFatalException>(() =>
            FileSplitter.SplitAsync(inputPath, inputPath, 2, replace: false, TestContext.Current.CancellationToken));

        Assert.Contains("Output part already exists", ex.Message);
    }

    [Fact]
    public async Task SplitZstdCompressedCreatesCompressedPartsWithZstPrefix()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mgb-file-splitter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var inputPath = Path.Combine(directory, "archive.sqlite");
        var outputPrefix = inputPath + ".zst";
        var bytes = Enumerable.Range(0, 2048).Select(static value => (byte)(value % 251)).ToArray();
        await File.WriteAllBytesAsync(inputPath, bytes, TestContext.Current.CancellationToken);

        var parts = await FileSplitter.SplitZstdCompressedAsync(
            inputPath,
            outputPrefix,
            partSizeBytes: 128,
            replace: false,
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(parts);
        Assert.All(parts, part => Assert.StartsWith(outputPrefix + ".part-", part, StringComparison.Ordinal));
        Assert.All(parts, part => Assert.InRange(new FileInfo(part).Length, 1, 128));

        using var combined = new MemoryStream();
        foreach (var part in parts)
        {
            await using var stream = File.OpenRead(part);
            await stream.CopyToAsync(combined, TestContext.Current.CancellationToken);
        }

        combined.Position = 0;
        using var zstd = new DecompressionStream(combined);
        using var decompressed = new MemoryStream();
        await zstd.CopyToAsync(decompressed, TestContext.Current.CancellationToken);

        Assert.Equal(bytes, decompressed.ToArray());
    }
}
