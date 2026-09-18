using System.Net;
using System.Text;
using Parrot.Auth;
using Parrot.Llm;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal sealed class ProviderUsageTests
{
    [Test]
    [Arguments("chatgpt", true)]
    [Arguments("kimi", true)]
    [Arguments("opencode-go", true)]
    [Arguments("compatible", true)]
    [Arguments("chatgpt", false)]
    [Arguments("kimi", false)]
    [Arguments("opencode-go", false)]
    [Arguments("compatible", false)]
    public async Task Composed_usage_preserves_credentials_requests_decoding_and_retry_capabilities(
        string providerId,
        bool hasCredential,
        CancellationToken cancellationToken)
    {
        var responseBody = providerId switch
        {
            "chatgpt" => """
                {"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":25,"reset_at":1735689600,"limit_window_seconds":18000},"secondary_window":{"used_percent":60,"reset_at":1736294400,"limit_window_seconds":604800}},"credits":{"has_credits":true,"balance":"12.50"}}
                """,
            "kimi" => """{"data":{"available_balance":12.50}}""",
            "opencode-go" => """
                {"useBalance":true,"rollingUsage":{"remainingPercent":75,"resetAt":"2025-01-01T00:00:00Z","window":18000},"weeklyUsage":{"usedPercent":60,"resetAt":"2025-01-08T00:00:00Z","window":604800}}
                """,
            _ => "{}",
        };
        using var handler = new UsageHandler(responseBody);
        using var client = new HttpClient(handler, disposeHandler: false);
        var credential = hasCredential ? "usage-token" : string.Empty;
        IReadOnlyList<LLMModel> models = [new LLMModel("declared-model", providerId)];
        var options = new OpenAICompatibleOptions
        {
            Id = providerId,
            BaseUrl = "https://example.test/v1",
            ApiKeySource = new FixedApiKeySource(credential),
            Models = models,
        };
        ILLMProvider provider = providerId switch
        {
            "chatgpt" => new ChatGptProvider(
                new FixedOAuthTokenSource(credential), client, models, [], [], true, new ResponsesWebSocketConnector()),
            "kimi" => new KimiProvider(options, client),
            "opencode-go" => new OpenCodeGoProvider(options, client),
            _ => new OpenAICompatibleProvider(options, client),
        };
        ILLMProvider retryingProvider = new RetryingProvider(provider);

        _ = await Assert.That(retryingProvider.Id).IsEqualTo(provider.Id);
        _ = await Assert.That(provider.SeedModels().Single().Id).IsEqualTo("declared-model");
        _ = await Assert.That(retryingProvider.SeedModels().SequenceEqual(provider.SeedModels())).IsTrue();
        _ = await Assert.That(handler.RequestCount).IsEqualTo(0);

        if (providerId == "compatible")
        {
            _ = await Assert.That(provider.UsageReporter).IsNull();
            _ = await Assert.That(retryingProvider.UsageReporter).IsNull();
            return;
        }

        var reporter = retryingProvider.UsageReporter
            ?? throw new InvalidOperationException("The provider lost its usage capability.");
        if (!hasCredential)
        {
            _ = await Assert.That(async () => await reporter.Usage(cancellationToken)).Throws<LLMProviderException>();
            _ = await Assert.That(handler.RequestCount).IsEqualTo(0);
            return;
        }

        var usage = await reporter.Usage(cancellationToken);
        var expectedEndpoint = providerId switch
        {
            "chatgpt" => "https://chatgpt.com/backend-api/wham/usage",
            "kimi" => "https://example.test/v1/users/me/balance",
            _ => "https://example.test/v1/usage",
        };
        var primaryWindow = new UsageWindow(25, DateTimeOffset.FromUnixTimeSeconds(1735689600), 18000);
        var secondaryWindow = new UsageWindow(60, DateTimeOffset.FromUnixTimeSeconds(1736294400), 604800);
        var expectedUsage = providerId switch
        {
            "chatgpt" => new SubscriptionUsage
            {
                PlanType = "plus",
                PrimaryWindow = primaryWindow,
                SecondaryWindow = secondaryWindow,
                Credits = new UsageCredits(true, "12.50"),
            },
            "kimi" => new SubscriptionUsage { Credits = new UsageCredits(true, "12.50") },
            _ => new SubscriptionUsage
            {
                PrimaryWindow = primaryWindow,
                SecondaryWindow = secondaryWindow,
                Credits = new UsageCredits(true, "75% remaining"),
            },
        };

        _ = await Assert.That(usage).IsEqualTo(expectedUsage);
        _ = await Assert.That(handler.RequestCount).IsEqualTo(1);
        _ = await Assert.That(handler.Endpoint).IsEqualTo(expectedEndpoint);
        _ = await Assert.That(handler.Method).IsEqualTo(HttpMethod.Get);
        _ = await Assert.That(handler.Headers["Authorization"]).IsEqualTo("Bearer usage-token");
        _ = await Assert.That(handler.Headers.ContainsKey("session-id")).IsFalse();
        _ = await Assert.That(handler.Headers.ContainsKey("x-opencode-session")).IsFalse();
        if (providerId == "opencode-go")
        {
            _ = await Assert.That(handler.Headers["User-Agent"]).IsEqualTo($"parrot/{BuildInfo.Version}");
        }

        if (providerId == "chatgpt")
        {
            _ = await Assert.That(handler.Headers["ChatGPT-Account-Id"]).IsEqualTo("usage-account");
            _ = await Assert.That(handler.Headers["originator"]).IsEqualTo("parrot");
            _ = await Assert.That(handler.Headers["User-Agent"]).IsEqualTo("parrot");
        }
    }

    private sealed class FixedApiKeySource(string credential) : IApiKeySource
    {
        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) =>
            ValueTask.FromResult(credential.Length > 0);

        public ValueTask<string> ApiKey(CancellationToken cancellationToken) => ValueTask.FromResult(credential);
    }

    private sealed class FixedOAuthTokenSource(string credential) : IOAuthTokenSource
    {
        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) =>
            ValueTask.FromResult(credential.Length > 0);

        public Task<OAuthAccess> Token(CancellationToken cancellationToken) =>
            Task.FromResult(new OAuthAccess(credential, "usage-account"));
    }

    private sealed class UsageHandler(string responseBody) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public string Endpoint { get; private set; } = string.Empty;

        public HttpMethod Method { get; private set; } = HttpMethod.Post;

        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Endpoint = request.RequestUri?.AbsoluteUri ?? string.Empty;
            Method = request.Method;
            foreach (var header in request.Headers)
            {
                Headers.Add(header.Key, string.Join(",", header.Value));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
