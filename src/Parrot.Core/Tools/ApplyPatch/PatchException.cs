namespace Parrot.Tools.ApplyPatch;

public sealed class PatchException : Exception
{
    public PatchException()
    {
    }

    public PatchException(string message)
        : base(message)
    {
    }

    public PatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
