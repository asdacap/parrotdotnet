namespace Parrot.Auth;

internal interface ICredentialStore
{
    ValueTask<string?> Get(string providerId, CancellationToken cancellationToken);

    ValueTask Set(string providerId, string secret, CancellationToken cancellationToken);

    ValueTask Delete(string providerId, CancellationToken cancellationToken);
}
