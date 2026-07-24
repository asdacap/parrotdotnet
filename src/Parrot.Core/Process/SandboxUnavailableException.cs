namespace Parrot.Process;

// The sandbox is the boundary, so its absence is fail-closed: the command does
// not run. Never caught into a fallback that runs unsandboxed.
public sealed class SandboxUnavailableException : Exception
{
    public SandboxUnavailableException(string message)
        : base(message)
    {
    }

    public SandboxUnavailableException()
    {
    }

    public SandboxUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
