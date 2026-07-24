using System.Text.Json;

namespace Parrot.Auth;

// Secrets live in the private data directory, written whole-file so a reader
// never observes a partial map.
internal sealed class FileCredentialStore(string path) : ICredentialStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    public async ValueTask<string?> Get(string providerId, CancellationToken cancellationToken)
    {
        var stored = await Read(cancellationToken).ConfigureAwait(false);
        return stored.TryGetValue(providerId, out var secret) ? secret : null;
    }

    public async ValueTask Set(string providerId, string secret, CancellationToken cancellationToken) =>
        await Mutate(stored => stored[providerId] = secret, cancellationToken).ConfigureAwait(false);

    public async ValueTask Delete(string providerId, CancellationToken cancellationToken) =>
        await Mutate(stored => _ = stored.Remove(providerId), cancellationToken).ConfigureAwait(false);

    private async ValueTask Mutate(Action<Dictionary<string, string>> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var stored = await Read(cancellationToken).ConfigureAwait(false);
            change(stored);

            _ = Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var temporary = path + ".tmp";

            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(stored, CredentialJsonContext.Default.DictionaryStringString),
                cancellationToken).ConfigureAwait(false);

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private async ValueTask<Dictionary<string, string>> Read(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize(text, CredentialJsonContext.Default.DictionaryStringString)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
