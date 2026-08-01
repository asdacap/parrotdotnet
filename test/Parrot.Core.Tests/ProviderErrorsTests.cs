using Parrot.Llm;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal sealed class ProviderErrorsTests
{
    [Test]
    [Arguments("usage_limit_reached", "", true, false)]
    [Arguments("", "insufficient_quota", true, false)]
    [Arguments("engine_overloaded_error", "", false, true)]
    [Arguments("", "server_is_overloaded", false, true)]
    [Arguments("rate_limit_error", "429", false, false)]
    public async Task Structured_fields_classify_permanent_and_transient_failures(
        string type, string code, bool usageLimit, bool overloaded)
    {
        var failure = new ProviderHttpException(429, type, code, "message text");

        _ = await Assert.That(ProviderErrors.IsUsageLimit(failure)).IsEqualTo(usageLimit);
        _ = await Assert.That(ProviderErrors.IsEngineOverloaded(failure)).IsEqualTo(overloaded);
    }

    [Test]
    [Arguments(" CONTEXT_LENGTH_EXCEEDED ", "", true)]
    [Arguments("", " context_window_exceeded ", true)]
    [Arguments("", "", false)]
    public async Task Context_length_is_classified_only_from_normalized_structured_fields(
        string type,
        string code,
        bool expected)
    {
        var http = new ProviderHttpException(429, type, code, "context length exceeded");
        var response = new ProviderResponseException(type, code, "context length exceeded");

        _ = await Assert.That(ProviderErrors.IsContextLengthExceeded(http)).IsEqualTo(expected);
        _ = await Assert.That(ProviderErrors.IsContextLengthExceeded(response)).IsEqualTo(expected);
    }

    [Test]
    public async Task A_status_alone_is_not_a_usage_limit()
    {
        var failure = new ProviderHttpException(429, string.Empty, string.Empty, "Too Many Requests");

        _ = await Assert.That(ProviderErrors.IsUsageLimit(failure)).IsFalse();
    }

    [Test]
    public async Task Service_unavailable_is_overloaded_regardless_of_vendor_payload()
    {
        var failure = new ProviderHttpException(
            503,
            string.Empty,
            "biscuit_baker_service_me_circuit_open",
            "Service Unavailable");

        _ = await Assert.That(ProviderErrors.IsEngineOverloaded(failure)).IsTrue();
    }

    [Test]
    public async Task Response_bodies_are_bounded_without_splitting_utf8()
    {
        var body = new string('界', 30_000);

        var bounded = ProviderErrors.BoundResponseBody(body);

        _ = await Assert.That(System.Text.Encoding.UTF8.GetByteCount(bounded)).IsLessThanOrEqualTo(64 << 10);
        _ = await Assert.That(bounded).EndsWith("… [truncated]");
        _ = await Assert.That(bounded).DoesNotContain("�");
    }

    [Test]
    public async Task Structured_provider_failures_expose_their_response_body()
    {
        var http = new ProviderHttpException(400, "type", "code", "detail", "http body");
        var response = new ProviderResponseException("type", "code", "detail", "stream body");

        _ = await Assert.That(ProviderErrors.ReadResponseBody(http)).IsEqualTo("http body");
        _ = await Assert.That(ProviderErrors.ReadResponseBody(response)).IsEqualTo("stream body");
        _ = await Assert.That(ProviderErrors.ReadResponseBody(new IOException("network"))).IsEmpty();
    }
}
