using ZstdSharp;

namespace OpenGameBuilder.Mgb.Archive.S3Archiver;

public static class FileSplitter
{
    public const long DefaultPartSizeBytes = 1_900_000_000;

    private const int BufferSize = 1024 * 1024;

    public static async Task<IReadOnlyList<string>> SplitAsync(
        string inputPath,
        string outputPrefix,
        long partSizeBytes,
        bool replace,
        CancellationToken cancellationToken = default)
    {
        if (partSizeBytes <= 0)
        {
            throw new ArchiveFatalException("--part-size-bytes must be greater than zero.");
        }

        var fullInputPath = Path.GetFullPath(inputPath);
        var fullOutputPrefix = Path.GetFullPath(outputPrefix);
        if (!File.Exists(fullInputPath))
        {
            throw new ArchiveFatalException($"Input file was not found: {fullInputPath}");
        }

        PrepareOutputPrefix(fullOutputPrefix, replace);
        var inputInfo = new FileInfo(fullInputPath);
        var partCount = checked((int)Math.Max(1, (inputInfo.Length + partSizeBytes - 1) / partSizeBytes));
        var outputPaths = Enumerable
            .Range(0, partCount)
            .Select(index => PartPath(fullOutputPrefix, index))
            .ToArray();

        await using var input = new FileStream(
            fullInputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        var buffer = new byte[BufferSize];
        var completedPaths = new List<string>(partCount);

        try
        {
            for (var partIndex = 0; partIndex < partCount; partIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outputPath = outputPaths[partIndex];
                await using var output = new FileStream(
                    outputPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.SequentialScan | FileOptions.Asynchronous);
                completedPaths.Add(outputPath);

                var remaining = Math.Min(partSizeBytes, input.Length - input.Position);
                while (remaining > 0)
                {
                    var readSize = (int)Math.Min(buffer.Length, remaining);
                    var read = await input.ReadAsync(buffer.AsMemory(0, readSize), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException($"Unexpected end of input while writing {outputPath}.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    remaining -= read;
                }
            }
        }
        catch
        {
            foreach (var outputPath in completedPaths)
            {
                TryDelete(outputPath);
            }

            throw;
        }

        return outputPaths;
    }

    public static async Task<IReadOnlyList<string>> SplitZstdCompressedAsync(
        string inputPath,
        string outputPrefix,
        long partSizeBytes,
        bool replace,
        CancellationToken cancellationToken = default)
    {
        if (partSizeBytes <= 0)
        {
            throw new ArchiveFatalException("--part-size-bytes must be greater than zero.");
        }

        var fullInputPath = Path.GetFullPath(inputPath);
        var fullOutputPrefix = Path.GetFullPath(outputPrefix);
        if (!File.Exists(fullInputPath))
        {
            throw new ArchiveFatalException($"Input file was not found: {fullInputPath}");
        }

        PrepareOutputPrefix(fullOutputPrefix, replace);
        await using var input = new FileStream(
            fullInputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        var completedPaths = new List<string>();

        try
        {
            await using var splitOutput = new SplitPartOutputStream(fullOutputPrefix, partSizeBytes, completedPaths);
            using (var zstd = new CompressionStream(splitOutput, 3, BufferSize, leaveOpen: true))
            {
                await input.CopyToAsync(zstd, BufferSize, cancellationToken).ConfigureAwait(false);
            }

            await splitOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            foreach (var outputPath in completedPaths)
            {
                TryDelete(outputPath);
            }

            throw;
        }

        return completedPaths;
    }

    private static void PrepareOutputPrefix(string outputPrefix, bool replace)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPrefix) ?? Environment.CurrentDirectory);
        var existingParts = FindPartPaths(outputPrefix);
        if (!replace && existingParts.Count > 0)
        {
            throw new ArchiveFatalException($"Output part already exists: {existingParts[0]}. Use --replace to overwrite parts.");
        }

        if (replace)
        {
            foreach (var existingPart in existingParts)
            {
                File.Delete(existingPart);
            }
        }
    }

    private static IReadOnlyList<string> FindPartPaths(string outputPrefix)
    {
        var directory = Path.GetDirectoryName(outputPrefix) ?? Environment.CurrentDirectory;
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(directory, Path.GetFileName(outputPrefix) + ".part-*", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string PartPath(string outputPrefix, int index) =>
        outputPrefix + ".part-" + index.ToString("000", System.Globalization.CultureInfo.InvariantCulture);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup after a failed split.
        }
    }

    private sealed class SplitPartOutputStream : Stream
    {
        private readonly string _outputPrefix;
        private readonly long _partSizeBytes;
        private readonly List<string> _completedPaths;
        private FileStream? _current;
        private int _nextPartIndex;
        private long _currentPartBytes;

        public SplitPartOutputStream(string outputPrefix, long partSizeBytes, List<string> completedPaths)
        {
            _outputPrefix = outputPrefix;
            _partSizeBytes = partSizeBytes;
            _completedPaths = completedPaths;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _current?.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _current?.FlushAsync(cancellationToken) ?? Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (buffer.Length > 0)
            {
                EnsureCurrentPart();
                var writeLength = (int)Math.Min(buffer.Length, _partSizeBytes - _currentPartBytes);
                _current!.Write(buffer[..writeLength]);
                _currentPartBytes += writeLength;
                buffer = buffer[writeLength..];
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (buffer.Length > 0)
            {
                EnsureCurrentPart();
                var writeLength = (int)Math.Min(buffer.Length, _partSizeBytes - _currentPartBytes);
                await _current!.WriteAsync(buffer[..writeLength], cancellationToken).ConfigureAwait(false);
                _currentPartBytes += writeLength;
                buffer = buffer[writeLength..];
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _current?.Dispose();
                _current = null;
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_current is not null)
            {
                await _current.DisposeAsync().ConfigureAwait(false);
                _current = null;
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }

        private void EnsureCurrentPart()
        {
            if (_current is not null && _currentPartBytes < _partSizeBytes)
            {
                return;
            }

            _current?.Dispose();
            var outputPath = PartPath(_outputPrefix, _nextPartIndex++);
            _current = new FileStream(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.SequentialScan);
            _completedPaths.Add(outputPath);
            _currentPartBytes = 0;
        }
    }
}
