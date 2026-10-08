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
                {"useBalance":true,"usage":{"rolling":{"percent":25,"resetsAt":"2025-01-01T00:00:00Z"},"weekly":{"percent":60,"resetsAt":"2025-01-08T00:00:00Z"}}}
                """,
            _ => "{}",
        };
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        });
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
            "kimi" => new UsageReportingProvider(new OpenAICompatibleProvider(options, client), new KimiUsageReporter(options, client)),
            "opencode-go" => new UsageReportingProvider(new OpenAICompatibleProvider(options, client), new OpenCodeGoUsageReporter(options, client)),
            _ => new OpenAICompatibleProvider(options, client),
        };
        ILLMProvider retryingProvider = new RetryingProvider(provider);

        _ = await Assert.That(retryingProvider.Id).IsEqualTo(provider.Id);
        _ = await Assert.That(provider.SeedModels().Single().Id).IsEqualTo("declared-model");
        _ = await Assert.That(retryingProvider.SeedModels().SequenceEqual(provider.SeedModels())).IsTrue();
        _ = await Assert.That(handler.Requests.Count).IsEqualTo(0);

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
            _ = await Assert.That(handler.Requests.Count).IsEqualTo(0);
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
            "opencode-go" => new SubscriptionUsage
            {
                PrimaryWindow = new UsageWindow(25, DateTimeOffset.FromUnixTimeSeconds(1735689600), 0),
                SecondaryWindow = new UsageWindow(60, DateTimeOffset.FromUnixTimeSeconds(1736294400), 0),
                Credits = new UsageCredits(true, "75% remaining"),
            },
            _ => new SubscriptionUsage
            {
                PrimaryWindow = primaryWindow,
                SecondaryWindow = secondaryWindow,
                Credits = new UsageCredits(true, "75% remaining"),
            },
        };

        _ = await Assert.That(usage).IsEqualTo(expectedUsage);
        _ = await Assert.That(handler.Requests.Count).IsEqualTo(1);
        var recorded = handler.Requests.Single();
        _ = await Assert.That(recorded.Uri.AbsoluteUri).IsEqualTo(expectedEndpoint);
        _ = await Assert.That(recorded.Method).IsEqualTo("GET");
        _ = await Assert.That(recorded.Headers["authorization"]).IsEqualTo("Bearer usage-token");
        _ = await Assert.That(recorded.Headers.ContainsKey("session-id")).IsFalse();
        _ = await Assert.That(recorded.Headers.ContainsKey("x-opencode-session")).IsFalse();
        if (providerId == "opencode-go")
        {
            _ = await Assert.That(recorded.Headers["user-agent"]).IsEqualTo($"parrot/{BuildInfo.Version}");
        }

        if (providerId == "chatgpt")
        {
            _ = await Assert.That(recorded.Headers["chatgpt-account-id"]).IsEqualTo("usage-account");
            _ = await Assert.That(recorded.Headers["originator"]).IsEqualTo("parrot");
            _ = await Assert.That(recorded.Headers["user-agent"]).IsEqualTo("parrot");
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
}
