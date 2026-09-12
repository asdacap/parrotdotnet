namespace Parrot.Llm.Wire;

// Caps the total bytes read from a provider response and disposes the owning
// HTTP response when the stream is closed. Reading past the limit is a hard
// error rather than a silent truncation.
internal sealed class BoundedStream(Stream inner, long limit, IDisposable owner) : Stream
{
    private long _remaining = limit;

    public IProviderAttemptDiagnostics? Attempt { get; init; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        if (buffer.Length > _remaining)
        {
            buffer = buffer[..(int)_remaining];
        }

        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Attempt?.RecordResponseBytes(read);
        _remaining -= read;

        if (_remaining <= 0 && read > 0)
        {
            var probe = new byte[1];

            var probeRead = await inner.ReadAsync(probe, cancellationToken).ConfigureAwait(false);
            Attempt?.RecordResponseBytes(probeRead);
            if (probeRead > 0)
            {
                throw new WireProtocolException("provider: response stream exceeds byte limit");
            }
        }

        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        if (count > _remaining)
        {
            count = (int)_remaining;
        }

        var read = inner.Read(buffer, offset, count);
        Attempt?.RecordResponseBytes(read);
        _remaining -= read;

        if (_remaining <= 0 && read > 0 && inner.ReadByte() >= 0)
        {
            Attempt?.RecordResponseBytes(1);
            throw new WireProtocolException("provider: response stream exceeds byte limit");
        }

        return read;
    }

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            owner.Dispose();
        }

        base.Dispose(disposing);
    }
}
