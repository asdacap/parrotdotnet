namespace Parrot.Auth;

// Named snapshots of every stored credential, one file per preset in the same
// format as the credential store, so switching accounts replaces the whole store.
internal sealed class CredentialPresets(string directory)
{
    private const string Extension = ".json";

    public ValueTask<IReadOnlyList<string>> List(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(directory))
        {
            return ValueTask.FromResult<IReadOnlyList<string>>([]);
        }

        return ValueTask.FromResult<IReadOnlyList<string>>(
        [
            .. Directory.EnumerateFiles(directory, "*" + Extension)
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Order(StringComparer.Ordinal),
        ]);
    }

    public async ValueTask Save(string name, ICredentialStore current, CancellationToken cancellationToken)
    {
        using var preset = Open(name);
        await Replace(preset, await Snapshot(current, cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
    }

    // The first preset holding the same accounts as the current store; null when
    // none does or the store is empty.
    public async ValueTask<string?> Match(ICredentialStore current, CancellationToken cancellationToken)
    {
        var snapshot = await Snapshot(current, cancellationToken).ConfigureAwait(false);

        if (snapshot.Count == 0)
        {
            return null;
        }

        foreach (var name in await List(cancellationToken).ConfigureAwait(false))
        {
            using var preset = Open(name);
            var candidate = await Snapshot(preset, cancellationToken).ConfigureAwait(false);

            if (candidate.Count == snapshot.Count && snapshot.All(entry =>
                    candidate.TryGetValue(entry.Key, out var credential) && credential.Matches(entry.Value)))
            {
                return name;
            }
        }

        return null;
    }

    public async ValueTask Apply(string name, ICredentialStore current, CancellationToken cancellationToken)
    {
        if (!File.Exists(PathOf(name)))
        {
            throw new AuthException("auth: credential preset not found");
        }

        using var preset = Open(name);
        await Replace(current, await Snapshot(preset, cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<Dictionary<string, Credential>> Snapshot(
        ICredentialStore store, CancellationToken cancellationToken)
    {
        var snapshot = new Dictionary<string, Credential>(StringComparer.Ordinal);

        foreach (var name in await store.List(cancellationToken).ConfigureAwait(false))
        {
            if (await store.Get(name, cancellationToken).ConfigureAwait(false) is { } credential)
            {
                snapshot[name] = credential;
            }
        }

        return snapshot;
    }

    private static async ValueTask Replace(
        ICredentialStore target, Dictionary<string, Credential> snapshot, CancellationToken cancellationToken)
    {
        foreach (var name in await target.List(cancellationToken).ConfigureAwait(false))
        {
            if (!snapshot.ContainsKey(name))
            {
                await target.Delete(name, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var (name, credential) in snapshot)
        {
            await target.Set(name, credential, cancellationToken).ConfigureAwait(false);
        }
    }

    private string PathOf(string name) => Path.Combine(directory, name + Extension);

    private FileCredentialStore Open(string name) => new(PathOf(name));
}
