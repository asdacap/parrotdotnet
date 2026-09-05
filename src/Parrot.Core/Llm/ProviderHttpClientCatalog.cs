using System.Net.Security;

namespace Parrot.Llm;

internal sealed class ProviderHttpClientCatalog(HttpClient standardClient) : IDisposable
{
    private readonly Dictionary<string, ProviderHttpClient> _insecureClients = new(StringComparer.OrdinalIgnoreCase);

    internal int InsecureClientCount => _insecureClients.Count;

    public HttpClient Resolve(string baseUrl, bool allowInvalidTlsCertificate)
    {
        if (!allowInvalidTlsCertificate || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || endpoint.Host.Length == 0)
        {
            return standardClient;
        }

        var authority = endpoint.GetLeftPart(UriPartial.Authority);
        if (_insecureClients.TryGetValue(authority, out var existing))
        {
            return existing.Client;
        }

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                errors == SslPolicyErrors.None || HasAuthority(request.RequestUri, authority),
        };
        ProviderHttpClient providerClient;
        try
        {
            providerClient = new ProviderHttpClient(handler);
        }
        catch
        {
            handler.Dispose();
            throw;
        }

        _insecureClients[authority] = providerClient;
        return providerClient.Client;
    }

    public void Dispose()
    {
        foreach (var client in _insecureClients.Values)
        {
            client.Dispose();
        }

        _insecureClients.Clear();
    }

    internal static bool HasAuthority(Uri? endpoint, string authority) =>
        endpoint is not null && endpoint.Scheme == Uri.UriSchemeHttps &&
        string.Equals(endpoint.GetLeftPart(UriPartial.Authority), authority, StringComparison.OrdinalIgnoreCase);
}
