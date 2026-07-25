using System.Runtime.InteropServices;

namespace Parrot.Cli.Enhanced;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DarwinTermios
{
    public ulong InputFlags;
    public ulong OutputFlags;
    public ulong ControlFlags;
    public ulong LocalFlags;
    public fixed byte ControlCharacters[20];
    public ulong InputSpeed;
    public ulong OutputSpeed;
}
