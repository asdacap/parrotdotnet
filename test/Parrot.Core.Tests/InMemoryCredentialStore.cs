using System.Collections.Concurrent;
using Parrot.Auth;

namespace Parrot.Core.Tests;

// A store that keeps credentials in memory, for exercising a token source
// without touching the filesystem.
internal sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<string, Credential> _values = new(StringComparer.Ordinal);

    public int Writes { get; private set; }

    public ValueTask<Credential?> Get(string name, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_values.TryGetValue(name, out var value) ? value : null);

    public ValueTask Set(string name, Credential credential, CancellationToken cancellationToken)
    {
        credential.Validate();
        _values[name] = credential;
        Writes++;
        return ValueTask.CompletedTask;
    }

    public ValueTask Delete(string name, CancellationToken cancellationToken)
    {
        _ = _values.TryRemove(name, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<string>> List(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<string>>([.. _values.Keys]);
}
