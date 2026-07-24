using Parrot.Auth;

namespace Parrot.Cli.Tests;

// A slash context carries these for the commands that need them. Driving the
// loop reaches none of those, so a stub that throws states it -- where one
// returning defaults would hide a command quietly asking for a credential.
internal sealed class UnusedCredentials : ICredentialStore
{
    public ValueTask<Credential?> Get(string name, CancellationToken cancellationToken) =>
        throw new NotSupportedException("driving the loop reads no credential");

    public ValueTask Set(string name, Credential credential, CancellationToken cancellationToken) =>
        throw new NotSupportedException("driving the loop stores no credential");

    public ValueTask Delete(string name, CancellationToken cancellationToken) =>
        throw new NotSupportedException("driving the loop deletes no credential");

    public ValueTask<IReadOnlyList<string>> List(CancellationToken cancellationToken) =>
        throw new NotSupportedException("driving the loop lists no credentials");
}
