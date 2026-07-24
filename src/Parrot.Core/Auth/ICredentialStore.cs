namespace Parrot.Auth;

// Storage and nothing else: it knows nothing of OAuth, expiry, or refresh
// (architecture principle 4). Keyed by an arbitrary name; the provider registry
// uses the provider id as the name.
internal interface ICredentialStore
{
    // Null when no credential is stored under the name.
    ValueTask<Credential?> Get(string name, CancellationToken cancellationToken);

    ValueTask Set(string name, Credential credential, CancellationToken cancellationToken);

    ValueTask Delete(string name, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<string>> List(CancellationToken cancellationToken);
}
