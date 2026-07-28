using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class UnixRawTerminalTests
{
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

    [Test]
    public async Task Linux_raw_mode_prevents_kernel_echo_from_a_pseudo_terminal()
    {
        Skip.Unless(OperatingSystem.IsLinux(), "Linux is required.");

        const byte sentinel = (byte)'q';
        using var terminal = OpenLinuxPseudoTerminal();
        var original = terminal.GetSlaveAttributes();
        try
        {
            terminal.SetSlaveAttributes(UnixRawTerminal.MakeLinuxRaw(original));
            terminal.WriteMaster(sentinel);

            _ = await Assert.That(terminal.ReadSlave()).IsEqualTo(sentinel);
            _ = await Assert.That(terminal.MasterHasOutput(200)).IsFalse();
        }
        finally
        {
            terminal.SetSlaveAttributes(original);
        }
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
