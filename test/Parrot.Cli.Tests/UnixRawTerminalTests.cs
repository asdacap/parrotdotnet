using System.Diagnostics;
using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class UnixRawTerminalTests
{
    private const int ReadTimeoutMilliseconds = 2000;
    private const int EchoTimeoutMilliseconds = 200;

    [Test]
    public async Task Linux_raw_mode_disables_echo_and_canonical_input()
    {
        const uint echo = 0x00000008U;
        const uint canonical = 0x00000002U;
        const uint extended = 0x00008000U;
        const uint signals = 0x00000001U;
        var original = new LinuxTermios
        {
            InputFlags = 0x00000532U,
            ControlFlags = 0,
            LocalFlags = echo | canonical | extended | signals,
        };

        var raw = UnixRawTerminal.MakeLinuxRaw(original);

        _ = await Assert.That(raw.LocalFlags & (echo | canonical | extended | signals)).IsEqualTo(0U);
        _ = await Assert.That(raw.InputFlags).IsEqualTo(0U);
        _ = await Assert.That(raw.ControlFlags & 0x00000030U).IsEqualTo(0x00000030U);
    }

    // The kernel echo assertion alone cannot catch the bug this guards: .NET's StdInReader
    // echoes to stdout, not to the descriptor it reads. What catches it is the byte arriving
    // without a newline, which no canonical-mode emulation would deliver.
    [Test]
    [Timeout(5000)]
    public async Task Linux_raw_terminal_reads_a_pseudo_terminal_byte_without_line_buffering_or_echo(
        CancellationToken cancellationToken)
    {
        Skip.Unless(OperatingSystem.IsLinux(), "Linux is required.");

        const byte sentinel = (byte)'q';
        var buffer = new byte[8];
        using var pseudoTerminal = OpenLinuxPseudoTerminal();
        var original = pseudoTerminal.GetSlaveAttributes();
        int count;

        using (var raw = UnixRawTerminal.Open(pseudoTerminal.SlaveDescriptor)
            ?? throw new IOException("Raw mode is unavailable on the pseudo-terminal slave."))
        {
            pseudoTerminal.WriteMaster(sentinel);
            count = await ReadUntilInput(raw, buffer, cancellationToken);
        }

        _ = await Assert.That(count).IsEqualTo(1);
        _ = await Assert.That(buffer[0]).IsEqualTo(sentinel);
        _ = await Assert.That(pseudoTerminal.MasterHasOutput(EchoTimeoutMilliseconds)).IsFalse();
        _ = await Assert.That(pseudoTerminal.GetSlaveAttributes().LocalFlags).IsEqualTo(original.LocalFlags);
    }

    [Test]
    public async Task Read_retries_temporary_unavailability_with_exponential_backoff()
    {
        Queue<(nint Count, int Error)> results = new(
        [
            (-1, 4),
            (-1, 11),
            (-1, 11),
            (-1, 11),
            (2, 0),
        ]);
        List<TimeSpan> delays = [];

        var count = UnixRawTerminal.ReadInputWithRetry(results.Dequeue, delays.Add, 11);

        _ = await Assert.That(count).IsEqualTo(2);
        _ = await Assert.That(results).IsEmpty();
        _ = await Assert.That(delays.SequenceEqual(
            [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40)])).IsTrue();
    }

    [Test]
    public async Task Read_stops_after_five_temporary_unavailability_retries()
    {
        var readCount = 0;
        List<TimeSpan> delays = [];

        var failure = await Assert.That(() => UnixRawTerminal.ReadInputWithRetry(
                () =>
                {
                    readCount++;
                    return (-1, 11);
                },
                delays.Add,
                11))
            .Throws<IOException>();

        _ = await Assert.That(failure).IsNotNull();
        _ = await Assert.That(failure?.Message ?? string.Empty).Contains("errno 11");
        _ = await Assert.That(readCount).IsEqualTo(6);
        _ = await Assert.That(delays.SequenceEqual(
            [
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(40),
                TimeSpan.FromMilliseconds(80),
                TimeSpan.FromMilliseconds(160),
            ])).IsTrue();
    }

    [Test]
    public async Task Darwin_raw_mode_disables_echo_and_canonical_input()
    {
        const ulong echo = 0x00000008UL;
        const ulong canonical = 0x00000100UL;
        const ulong extended = 0x00000400UL;
        const ulong signals = 0x00000080UL;
        var original = new DarwinTermios
        {
            InputFlags = 0x00000332UL,
            ControlFlags = 0,
            LocalFlags = echo | canonical | extended | signals,
        };

        var raw = UnixRawTerminal.MakeDarwinRaw(original);

        _ = await Assert.That(raw.LocalFlags & (echo | canonical | extended | signals)).IsEqualTo(0UL);
        _ = await Assert.That(raw.InputFlags).IsEqualTo(0UL);
        _ = await Assert.That(raw.ControlFlags & 0x00000300UL).IsEqualTo(0x00000300UL);
    }

    // VMIN=0/VTIME=1 returns zero bytes after 100ms of silence; that is a poll tick, not end of input.
    private static async Task<int> ReadUntilInput(
        IRawTerminal raw, byte[] buffer, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(ReadTimeoutMilliseconds);
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < timeout)
        {
            var count = await raw.Read(buffer, cancellationToken).AsTask().WaitAsync(timeout, cancellationToken);
            if (count > 0)
            {
                return count;
            }
        }

        return 0;
    }

    private static LinuxPseudoTerminal OpenLinuxPseudoTerminal()
    {
        try
        {
            return LinuxPseudoTerminal.Open();
        }
        catch (IOException exception)
        {
            Skip.Test($"Linux pseudo-terminal support is unavailable: {exception.Message}");
            throw;
        }
    }
}
