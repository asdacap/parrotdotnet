using System.Threading.Channels;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal sealed class IdleTimeoutStreamTests
{
    [Test]
    public async Task Bytes_refresh_idle_allowance_and_probe_is_bounded(CancellationToken cancellationToken)
    {
        var clock = new ControlledTimeProvider();
        using var source = new PendingStream();
        using var owner = new MemoryStream();
        await using var bounded = new BoundedStream(new IdleTimeoutStream(source, TimeSpan.FromMinutes(5), clock), 3, owner);
        var buffer = new byte[1];
        for (var index = 0; index < 2; index++)
        {
            var read = bounded.ReadAsync(buffer.AsMemory(), cancellationToken).AsTask();
            await clock.WaitForTimer(cancellationToken);
            clock.Advance(TimeSpan.FromMinutes(4));
            source.Send((byte)':');
            _ = await Assert.That(await read).IsEqualTo(1);
        }

        source.Send((byte)'\n');
        var probe = bounded.ReadAsync(buffer.AsMemory(), cancellationToken).AsTask();
        await clock.WaitForTimer(cancellationToken);
        clock.Advance(TimeSpan.FromMinutes(5));
        _ = await Assert.That(async () => await probe.WaitAsync(cancellationToken)).Throws<WireProtocolException>();
    }

    [Test]
    public async Task First_read_expires_without_bytes(CancellationToken cancellationToken)
    {
        var clock = new ControlledTimeProvider();
        using var source = new PendingStream();
        await using var stream = new IdleTimeoutStream(source, TimeSpan.FromMinutes(5), clock);
        var read = stream.ReadAsync(new byte[1].AsMemory(), cancellationToken).AsTask();
        await clock.WaitForTimer(cancellationToken);
        clock.Advance(TimeSpan.FromMinutes(5));
        _ = await Assert.That(async () => await read.WaitAsync(cancellationToken)).Throws<WireProtocolException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Caller_cancellation_is_preserved_including_disabled_timeout(bool disabled, CancellationToken cancellationToken)
    {
        var clock = new ControlledTimeProvider();
        using var source = new PendingStream();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var stream = new IdleTimeoutStream(source, disabled ? TimeSpan.Zero : TimeSpan.FromMinutes(5), clock);
        var read = stream.ReadAsync(new byte[1].AsMemory(), caller.Token).AsTask();
        if (disabled)
        {
            clock.Advance(TimeSpan.FromDays(1));
            _ = await Assert.That(read.IsCompleted).IsFalse();
        }

        await caller.CancelAsync();
        clock.Advance(TimeSpan.FromMinutes(5));
        _ = await Assert.That(async () => await read.WaitAsync(cancellationToken)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Empty_reads_eof_and_disposal_remain_immediate(CancellationToken cancellationToken)
    {
        var clock = new ControlledTimeProvider();
        using var source = new MemoryStream();
        var stream = new IdleTimeoutStream(source, TimeSpan.FromMinutes(5), clock);
        _ = await Assert.That(await stream.ReadAsync(Memory<byte>.Empty, cancellationToken)).IsEqualTo(0);
        _ = await Assert.That(await stream.ReadAsync(new byte[1].AsMemory(), cancellationToken)).IsEqualTo(0);
        await stream.DisposeAsync();
        clock.Advance(TimeSpan.FromDays(1));
        _ = await Assert.That(source.CanRead).IsFalse();
    }

    private sealed class PendingStream : Stream
    {
        private readonly Channel<byte> _bytes = Channel.CreateUnbounded<byte>();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Send(byte value) => _ = _bytes.Writer.TryWrite(value);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var value = await _bytes.Reader.ReadAsync(cancellationToken);
            buffer.Span[0] = value;
            return 1;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
