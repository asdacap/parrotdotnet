using System.Text.Json;
using Parrot.Llm.Wire;

namespace Parrot.Llm;

internal sealed class ModelsDevInformationProvider(HttpClient client)
{
    private const int MaximumResponseBytes = 16 << 20;
    private static readonly Uri Endpoint = new("https://models.dev/api.json");

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<LLMModel>>> Fetch(
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await HttpStreaming
                .Get(
                    client,
                    Endpoint,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    HttpStreaming.ModelsRefreshTimeout,
                    MaximumResponseBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            return ModelsDevCatalogueDecoder.Decode(body);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested && IsUnavailable(failure))
        {
            return new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal);
        }
    }

    private static bool IsUnavailable(Exception failure) =>
        failure is LLMProviderException or ProviderHttpException or JsonException or HttpRequestException or IOException
            or OperationCanceledException;
}
