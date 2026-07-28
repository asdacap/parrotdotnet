namespace Parrot.Statuses;

internal sealed class StatusRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IStatusProvider> _providers = new(StringComparer.Ordinal);

    public StatusRegistry(params IStatusProvider[] providers)
    {
        foreach (var provider in providers)
        {
            Register(provider);
        }
    }

    public void Register(IStatusProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ValidateKey(provider.Key, "provider");

        lock (_gate)
        {
            if (!_providers.TryAdd(provider.Key, provider))
            {
                throw new StatusRegistryException($"status: duplicate provider '{provider.Key}'");
            }
        }
    }

    public async Task<string> Observe(
        StatusQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        KeyValuePair<string, IStatusProvider>[] providers;

        lock (_gate)
        {
            providers = [.. _providers];
        }

        Array.Sort(providers, static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        var observations = providers.Select(provider => Observe(provider, query, cancellationToken)).ToArray();
        var sections = await Task.WhenAll(observations).ConfigureAwait(false);

        return string.Join(
            "\n\n",
            sections
                .Where(section => section.Available && !string.IsNullOrWhiteSpace(section.Text))
                .Select(section => section.Text));
    }

    private static void ValidateKey(string key, string subject)
    {
        var separator = key.IndexOf(':', StringComparison.Ordinal);

        if (separator <= 0
            || separator == key.Length - 1
            || key.Any(char.IsWhiteSpace))
        {
            throw new StatusRegistryException($"status: {subject} requires a stable namespaced key");
        }
    }

    private static async Task<StatusObservation> Observe(
        KeyValuePair<string, IStatusProvider> provider,
        StatusQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.Value.Observe(query, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            throw new StatusRegistryException($"{provider.Key}: {failure.Message}", failure);
        }
    }
}
