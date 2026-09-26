using System.Text;
using System.Text.Json;
using SecondKey.Artifacts.Json;

namespace SecondKey.Artifacts.Capture;

/// <summary>
/// Appends events to a <c>*.skcap</c> file. Thread-safe: concurrent requests through a
/// recording proxy are serialized here, and <c>seq</c> is assigned under the same lock that
/// writes the line, so file order and <c>seq</c> order can never disagree.
/// </summary>
public sealed class CaptureWriter : IAsyncDisposable
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();
    private readonly Stream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _seq;
    private long _count;

    private CaptureWriter(Stream stream) => _stream = stream;

    /// <summary>How many exchanges have been written.</summary>
    public long Count => Interlocked.Read(ref _count);

    public static async Task<CaptureWriter> CreateAsync(string path, CaptureHeader header, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        return await CreateAsync(stream, header, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CaptureWriter> CreateAsync(Stream stream, CaptureHeader header, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(header);
        var writer = new CaptureWriter(stream);
        await writer.WriteLineAsync(header, cancellationToken).ConfigureAwait(false);
        return writer;
    }

    /// <summary>Writes one exchange; <paramref name="create"/> receives the seq it is written under.</summary>
    public async Task<HttpExchange> WriteAsync(Func<long, HttpExchange> create, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(create);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var exchange = create(_seq + 1);
            await WriteUnlockedAsync(exchange, cancellationToken).ConfigureAwait(false);
            _seq = exchange.Seq;
            Interlocked.Increment(ref _count);
            return exchange;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.FlushAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    /// <summary>Serializes one event as a single line, exactly as the capture writer would.</summary>
    public static string Serialize(CaptureEvent captureEvent) =>
        JsonSerializer.Serialize(captureEvent, ArtifactJson.Lines);

    private async Task WriteLineAsync(CaptureEvent captureEvent, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteUnlockedAsync(captureEvent, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteUnlockedAsync(CaptureEvent captureEvent, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(Serialize(captureEvent));
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
