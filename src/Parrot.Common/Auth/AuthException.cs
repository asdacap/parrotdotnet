namespace Parrot.Auth;

// A credential or OAuth failure that is meaningful in the CLI, since it ends up
// there. A component-specific type, per MIGRATION.md section 4.
public sealed class AuthException : Exception
{
    public AuthException(string message)
        : base(message)
    {
    }

    public AuthException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AuthException()
    {
    }
}
