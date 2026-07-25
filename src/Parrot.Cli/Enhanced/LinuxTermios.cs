using System.Runtime.InteropServices;

namespace Parrot.Cli.Enhanced;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LinuxTermios
{
    public uint InputFlags;
    public uint OutputFlags;
    public uint ControlFlags;
    public uint LocalFlags;
    public byte Line;
    public fixed byte ControlCharacters[32];
    public uint InputSpeed;
    public uint OutputSpeed;
}
