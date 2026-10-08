using System.Collections.Immutable;

namespace Parrot.Statuses;

internal sealed class StatusRegistry
{
    private readonly ImmutableArray<IStatusProvider> _providers;

    public StatusRegistry(params IStatusProvider[] providers)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ValidateKey(provider.Key, "provider");
            if (!keys.Add(provider.Key))
            {
                throw new StatusRegistryException($"status: duplicate provider '{provider.Key}'");
            }
        }

        _providers = [.. providers.OrderBy(static provider => provider.Key, StringComparer.Ordinal)];
    }

    public Task<string> Observe(
        StatusQuery query,
        IStatusProvider? profile,
        CancellationToken cancellationToken) =>
        ObserveWithProvider(query, profile, null, cancellationToken);

    public async Task<string> ObserveWithProvider(
        StatusQuery query,
        IStatusProvider? profile,
        IStatusProvider? additional,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var providers = _providers;
        foreach (var (extra, subject) in new (IStatusProvider? Provider, string Subject)[] { (profile, "profile provider"), (additional, "additional provider") })
        {
            if (extra is null)
            {
                continue;
            }

            ValidateKey(extra.Key, subject);
            if (providers.Any(item => string.Equals(item.Key, extra.Key, StringComparison.Ordinal)))
            {
                throw new StatusRegistryException($"status: duplicate provider '{extra.Key}'");
            }

            providers = [.. providers.Append(extra).OrderBy(static provider => provider.Key, StringComparer.Ordinal)];
        }

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
        IStatusProvider provider,
        StatusQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.Observe(query, cancellationToken).ConfigureAwait(false);
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
