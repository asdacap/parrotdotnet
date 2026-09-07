using System.Net;
using System.Text;
using Parrot.Auth;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal sealed class ChatGptModelCatalogueTests
{
    [Test]
    [Arguments("gpt-5.5", true)]
    [Arguments("gpt-5.3-codex-spark", true)]
    [Arguments("gpt-5.4", true)]
    [Arguments("gpt-5.4-mini", true)]
    [Arguments("gpt-6-astra", true)]
    [Arguments("gpt-6", true)]
    [Arguments("gpt-6.0-astra", true)]
    [Arguments("gpt-7", true)]
    [Arguments("gpt-10", true)]
    [Arguments("gpt-5.5-astra", true)]
    [Arguments("gpt-5.9", true)]
    [Arguments("gpt-5.10", true)]
    [Arguments("gpt-5.10-astra", true)]
    [Arguments("gpt-5.40", true)]
    [Arguments("gpt-6garbage", true)]
    [Arguments("gpt-6.", true)]
    [Arguments("gpt-6.1.2", true)]
    [Arguments("gpt-5", false)]
    [Arguments("gpt-5.4-astra", false)]
    [Arguments("gpt-5.04-astra", false)]
    [Arguments("gpt-4.1", false)]
    [Arguments("gpt-4.99", false)]
    [Arguments("gpt-5.5-pro", false)]
    [Arguments("gpt-5.6", false)]
    [Arguments("not-a-gpt-model", false)]
    public async Task External_models_follow_opencode_codex_version_filter(string modelId, bool expected)
    {
        var filtered = ChatGptModelCatalogue.FilterExternal([new LLMModel(modelId, "openai")]);

        _ = await Assert.That(filtered.Count == 1).IsEqualTo(expected);
        if (expected)
        {
            _ = await Assert.That(filtered[0].ProviderId).IsEqualTo(ChatGptProvider.ProviderId);
        }
    }

    [Test]
    public async Task Provider_unions_filtered_external_and_live_models_with_precedence_limits_and_prices(
        CancellationToken cancellationToken)
    {
        using var handler = new ModelsHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        var external = ChatGptModelCatalogue.FilterExternal(
        [
            new LLMModel("gpt-5.5-astra", "openai")
            {
                ContextWindow = 1_050_000,
                MaxInputTokens = 922_000,
                MaxOutputTokens = 128_000,
                InputPrice = 0.000005,
                CachedInputPrice = 0.0000005,
                OutputPrice = 0.00003,
                Fields = ModelMetadataFields.ContextWindow
                    | ModelMetadataFields.MaxInputTokens
                    | ModelMetadataFields.MaxOutputTokens
                    | ModelMetadataFields.InputPrice
                    | ModelMetadataFields.CachedInputPrice
                    | ModelMetadataFields.OutputPrice,
            },
            new LLMModel("gpt-6-astra", "openai")
            {
                InputPrice = 0.00001,
                Fields = ModelMetadataFields.InputPrice,
            },
            new LLMModel("gpt-4.1", "openai")
            {
                Name = "External old model",
                Fields = ModelMetadataFields.Name,
            },
        ]);
        ILLMProvider provider = new ChatGptProvider(
            new FixedOAuthTokenSource(),
            client,
            [
                new LLMModel("gpt-5.5-astra", ChatGptProvider.ProviderId)
                {
                    InputPrice = 0.000007,
                    Fields = ModelMetadataFields.InputPrice,
                },
            ],
            [
                new LLMModel("gpt-5.5-astra", ChatGptProvider.ProviderId)
                {
                    OutputPrice = 0.00004,
                    Fields = ModelMetadataFields.OutputPrice,
                },
            ],
            external,
            false,
            new ResponsesWebSocketConnector());

        var models = await provider.ListModels(cancellationToken);
        var codex = models.Single(model => model.Id == "gpt-5.5-astra");
        var liveOld = models.Single(model => model.Id == "gpt-4.1");

        _ = await Assert.That(string.Join(",", models.Select(model => model.Id)))
            .IsEqualTo("gpt-4.1,gpt-5.5-astra,gpt-6-astra");
        _ = await Assert.That(liveOld.Name).IsEqualTo("Live old model");
        _ = await Assert.That(codex.ContextWindow).IsEqualTo(400_000);
        _ = await Assert.That(codex.MaxInputTokens).IsEqualTo(272_000);
        _ = await Assert.That(codex.MaxOutputTokens).IsEqualTo(128_000);
        _ = await Assert.That(codex.InputPrice).IsEqualTo(0.000007);
        _ = await Assert.That(codex.CachedInputPrice).IsEqualTo(0.0000005);
        _ = await Assert.That(codex.OutputPrice).IsEqualTo(0.00004);
        _ = await Assert.That(models.Single(model => model.Id == "gpt-6-astra").InputPrice).IsEqualTo(0.00001);
    }

    [Test]
    public async Task Registry_projects_models_dev_openai_catalogue_into_chatgpt(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "parrot-chatgpt-models-dev", Guid.NewGuid().ToString("n"));
        _ = Directory.CreateDirectory(directory);
        var configurationPath = Path.Combine(directory, "config.yaml");
        await File.WriteAllTextAsync(configurationPath, string.Empty, cancellationToken);

        try
        {
            var credentials = new InMemoryCredentialStore();
            await credentials.Set(
                ChatGptProvider.ProviderId,
                Credential.ForOAuth(OAuthCredential.Create(
                    "token",
                    "refresh",
                    DateTimeOffset.UtcNow.AddHours(1),
                    "account")),
                cancellationToken);
            using var handler = new RegistryHandler();
            using var client = new HttpClient(handler, disposeHandler: false);
            using var clients = new ProviderHttpClientCatalog(client);
            var registry = await new ProviderRegistryBuilder(
                Configuration.Load(configurationPath, Path.Combine(directory, "predefined_config.yaml")),
                credentials,
                clients,
                new SystemBrowserOpener(static _ => null),
                new ModelsDevInformationProvider(client)).Build(cancellationToken);

            _ = await Assert.That(string.Join(",", registry.Models(ChatGptProvider.ProviderId).Select(model => model.Id)))
                .IsEqualTo("gpt-4.1,gpt-5.4,gpt-5.5-astra");
            _ = await Assert.That(registry.Models("openai").Select(model => model.Id)).Contains("gpt-4.1");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FixedOAuthTokenSource : IOAuthTokenSource
    {
        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public Task<OAuthAccess> Token(CancellationToken cancellationToken) =>
            Task.FromResult(new OAuthAccess("token", "account"));
    }

    private sealed class RegistryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri == new Uri("https://models.dev/api.json"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"openai":{"id":"openai","models":{
                          "old":{"id":"gpt-4.1"},
                          "legacy":{"id":"gpt-5.4"},
                          "future":{"id":"gpt-5.5-astra"}
                        }}}
                        """,
                        Encoding.UTF8,
                        "application/json"),
                });
            }

            if (request.RequestUri?.AbsolutePath == "/backend-api/codex/models")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"models":[{"slug":"gpt-4.1","context_window":100000,"visibility":"list"}]}""", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class ModelsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"models":[
                      {"slug":"gpt-4.1","display_name":"Live old model","context_window":100000,"visibility":"list"},
                      {"slug":"gpt-5.5-astra","display_name":"Live 5.5","context_window":900000,"visibility":"list"}
                    ]}
                    """,
                    Encoding.UTF8,
                    "application/json"),
            });
    }
}
