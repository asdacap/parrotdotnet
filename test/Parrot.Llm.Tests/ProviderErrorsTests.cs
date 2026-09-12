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
    public async Task Context_length_is_classified_from_normalized_structured_fields(
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
    [Arguments("180000", "180514", "147746", "32768", 32768, 24866)]
    [Arguments("180000", "212768", "180000", "32768", 32768, 0)]
    [Arguments("180000", "203768", "171000", "32768", 32768, 450)]
    [Arguments("180000", "204197", "171429", "32768", 32768, 0)]
    [Arguments("180000", "180514", "147746", "32768", 24000, 0)]
    [Arguments("180000", "180514", "147746", "32768", 24866, 0)]
    [Arguments("180000", "180514", "147746", "32768", 0, 0)]
    [Arguments("180000", "180515", "147746", "32768", 32768, 0)]
    [Arguments("180000", "170000", "147746", "22254", 32768, 0)]
    [Arguments("99999999999999999999", "180514", "147746", "32768", 32768, 0)]
    [Arguments("180000", "180514", "not a number", "32768", 32768, 0)]
    public async Task Explicit_context_counts_only_reduce_to_positive_capacity(
        string context, string total, string input, string completion, int maximumTokens, int expected)
    {
        var detail = $"litellm.BadRequestError: OpenAIException - Requested token count exceeds the model's maximum context length of {context} tokens. You requested a total of {total} tokens: {input} tokens from the input messages and {completion} tokens for the completion. Please reduce your prompt.";
        var failures = new Exception[]
        {
            new ProviderHttpException(400, "null", "400", detail + " Fallback failed: " + detail),
            new ProviderResponseException(string.Empty, "context_length_exceeded", detail),
        };
        foreach (var failure in failures)
        {
            var reduced = ProviderErrors.TryReduceContextBudget(failure, maximumTokens, out var reducedMaximumTokens);

            _ = await Assert.That(reduced).IsEqualTo(expected > 0);
            _ = await Assert.That(reducedMaximumTokens).IsEqualTo(expected);
        }
    }

    [Test]
    [Arguments("context length exceeded")]
    [Arguments("Requested token count exceeds the model's maximum context length of 180000 tokens.")]
    [Arguments("unrelated 180000 180514 147746 32768")]
    public async Task Unrecognized_context_details_do_not_change_the_budget(string detail)
    {
        var failure = new ProviderHttpException(400, string.Empty, "400", detail);

        _ = await Assert.That(ProviderErrors.TryReduceContextBudget(failure, 32768, out var maximumTokens)).IsFalse();
        _ = await Assert.That(maximumTokens).IsEqualTo(0);
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
