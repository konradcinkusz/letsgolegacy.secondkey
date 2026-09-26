namespace SecondKey.Capture;

/// <summary>
/// Passes everything written through to the client while keeping a copy of the first
/// <c>limit</c> bytes for the recording. The client always gets the whole response.
/// </summary>
internal sealed class TeeStream : Stream
{
    private readonly Stream _inner;
    private readonly long _limit;
    private readonly MemoryStream _copy = new();

    public TeeStream(Stream inner, long limit)
    {
        _inner = inner;
        _limit = limit;
    }

    public long TotalWritten { get; private set; }

    public bool Truncated => TotalWritten > _copy.Length;

    public ReadOnlySpan<byte> Captured => _copy.GetBuffer().AsSpan(0, (int)_copy.Length);

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        Keep(buffer.AsSpan(offset, count));
        _inner.Write(buffer, offset, count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Keep(buffer.Span);
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _copy.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Keep(ReadOnlySpan<byte> bytes)
    {
        TotalWritten += bytes.Length;
        var room = _limit - _copy.Length;
        if (room > 0)
        {
            _copy.Write(bytes[..(int)Math.Min(room, bytes.Length)]);
        }
    }
}
