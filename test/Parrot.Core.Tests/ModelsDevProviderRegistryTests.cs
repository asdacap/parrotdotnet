using System.Net;
using System.Text;
using Parrot.Auth;
using Parrot.Config;
using Parrot.Llm;

namespace Parrot.Core.Tests;

internal sealed class ModelsDevProviderRegistryTests
{
    [Test]
    public async Task Build_fetches_once_and_keeps_matching_external_membership_after_live_refresh(
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "parrot-models-dev", Guid.NewGuid().ToString("n"));
        _ = Directory.CreateDirectory(directory);
        var configurationPath = Path.Combine(directory, "config.yaml");
        const string configuration = """
            providers:
              first:
                base_url: https://first.test/v1
                headers:
                  X-Provider: first-secret
              second:
                base_url: https://second.test/v1
            """;
        await File.WriteAllTextAsync(configurationPath, configuration, cancellationToken);

        try
        {
            var credentials = new InMemoryCredentialStore();
            await credentials.Set("first", Credential.ForApiKey("first-key"), cancellationToken);
            await credentials.Set("second", Credential.ForApiKey("second-key"), cancellationToken);
            using var handler = new CatalogueHandler();
            using var client = new HttpClient(handler, disposeHandler: false);
            using var httpClients = new ProviderHttpClientCatalog(client);
            var registry = await new ProviderRegistryBuilder(
                Configuration.Load(configurationPath, Path.Combine(directory, "predefined_config.yaml")),
                credentials,
                httpClients,
                new SystemBrowserOpener(static _ => null),
                new ModelsDevInformationProvider(client)).Build(cancellationToken);

            _ = await registry.AvailableModels(cancellationToken);

            _ = await Assert.That(handler.ModelsDevRequests).IsEqualTo(1);
            _ = await Assert.That(handler.ModelsDevAuthorization).IsNull();
            _ = await Assert.That(handler.ModelsDevProviderHeader).IsNull();
            _ = await Assert.That(string.Join(",", registry.Models("first").Select(model => model.Id)))
                .IsEqualTo("external-first,live-first");
            _ = await Assert.That(string.Join(",", registry.Models("second").Select(model => model.Id)))
                .IsEqualTo("external-second,live-second");
            _ = await Assert.That(registry.List().Select(provider => provider.Id)).DoesNotContain("ghost");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CatalogueHandler : HttpMessageHandler
    {
        public int ModelsDevRequests { get; private set; }

        public string? ModelsDevAuthorization { get; private set; }

        public string? ModelsDevProviderHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri == new Uri("https://models.dev/api.json"))
            {
                ModelsDevRequests++;
                ModelsDevAuthorization = request.Headers.Authorization?.ToString();
                ModelsDevProviderHeader = request.Headers.TryGetValues("X-Provider", out var values)
                    ? values.Single()
                    : null;
                return Task.FromResult(JsonResponse(
                    """
                    {
                      "first":{"id":"first","models":{"external":{"id":"external-first"}}},
                      "second":{"id":"second","models":{"external":{"id":"external-second"}}},
                      "ghost":{"id":"ghost","models":{"external":{"id":"external-ghost"}}}
                    }
                    """));
            }

            var modelId = request.RequestUri?.Host switch
            {
                "first.test" => "live-first",
                "second.test" => "live-second",
                _ => string.Empty,
            };
            return Task.FromResult(modelId.Length > 0
                ? JsonResponse($"{{\"data\":[{{\"id\":\"{modelId}\"}}]}}")
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }

        private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
