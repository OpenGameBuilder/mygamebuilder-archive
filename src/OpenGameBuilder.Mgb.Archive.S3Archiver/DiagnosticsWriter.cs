using System.Text.Json;
using System.Threading.Channels;

namespace OpenGameBuilder.Mgb.Archive.S3Archiver;

public sealed class DiagnosticsWriter : IAsyncDisposable
{
    private readonly Channel<object> _channel = Channel.CreateUnbounded<object>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    private readonly StreamWriter _writer;
    private readonly Task _pump;

    private DiagnosticsWriter(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
        _pump = Task.Run(PumpAsync);
    }

    public static DiagnosticsWriter Create(string workDirectory)
    {
        Directory.CreateDirectory(workDirectory);
        var path = Path.Combine(workDirectory, "diagnostics.jsonl");
        return new DiagnosticsWriter(path);
    }

    public void Write(string phase, string severity, string message, string? key = null, string? rawVersionId = null, object? detail = null)
    {
        _channel.Writer.TryWrite(new
        {
            utc = DateTimeOffset.UtcNow,
            phase,
            severity,
            key,
            rawVersionId,
            message,
            detail
        });
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _pump.ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await _writer.WriteLineAsync(JsonSerializer.Serialize(item)).ConfigureAwait(false);
        }
    }
}
