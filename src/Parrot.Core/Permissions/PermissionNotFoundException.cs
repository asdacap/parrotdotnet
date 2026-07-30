namespace Parrot.Permissions;

public sealed class PermissionNotFoundException : PermissionException
{
    public PermissionNotFoundException()
    {
    }

    public PermissionNotFoundException(string message)
        : base(message)
    {
    }

    public PermissionNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
