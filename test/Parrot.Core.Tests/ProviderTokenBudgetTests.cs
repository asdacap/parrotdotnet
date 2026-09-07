using Parrot.Agent;
using Parrot.Llm;

namespace Parrot.Core.Tests;

internal sealed class ProviderTokenBudgetTests
{
    private const string Selector = "provider/model";

    [Test]
    [Arguments(1000L, 1500, 1000L, 1500L)]
    [Arguments(1000L, 1500, 1200L, 1700L)]
    [Arguments(1000L, 1500, 200L, 700L)]
    [Arguments(1000L, 500, 1200L, 1200L)]
    [Arguments(1000L, 1500, long.MaxValue, long.MaxValue)]
    public async Task Calibration_tracks_estimate_changes_without_counting_output_twice(
        long previousEstimate, int actualInput, long currentEstimate, long expected)
    {
        var budget = new ProviderTokenBudget();
        budget.ObserveUsage(Selector, previousEstimate, LLMEvent.Completed("stop", actualInput, 0, 200, "reply", []));

        _ = await Assert.That(budget.EstimateInputTokens(Selector, currentEstimate)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task Missing_usage_preserves_last_valid_observation(int missingInput)
    {
        var budget = new ProviderTokenBudget();
        budget.ObserveUsage(Selector, 1000, LLMEvent.Completed("stop", missingInput, 0, 200, "reply", []));
        _ = await Assert.That(budget.EstimateInputTokens(Selector, 1200)).IsEqualTo(1200L);

        budget.ObserveUsage(Selector, 1000, LLMEvent.Completed("stop", 1500, 0, 200, "reply", []));
        budget.ObserveUsage(Selector, 1200, LLMEvent.Completed("stop", missingInput, 0, 200, "reply", []));
        _ = await Assert.That(budget.EstimateInputTokens(Selector, 1400)).IsEqualTo(1900L);
    }

    [Test]
    public async Task Model_switch_and_compaction_reset_discard_inapplicable_calibration()
    {
        var budget = new ProviderTokenBudget();
        _ = await Assert.That(budget.EstimateInputTokens(Selector, -1)).IsEqualTo(0L);
        budget.ObserveUsage(Selector, 1000, LLMEvent.Completed("stop", 1500, 0, 200, "reply", []));
        _ = await Assert.That(budget.EstimateInputTokens("other/model", 1200)).IsEqualTo(1200L);

        budget.ObserveUsage("other/model", 1200, LLMEvent.Completed("stop", 2000, 0, 200, "reply", []));
        _ = await Assert.That(budget.EstimateInputTokens(Selector, 1400)).IsEqualTo(1400L);
        _ = await Assert.That(budget.EstimateInputTokens("other/model", 1400)).IsEqualTo(2200L);

        budget.Reset();
        _ = await Assert.That(budget.EstimateInputTokens("other/model", 200)).IsEqualTo(200L);
    }

    [Test]
    [Arguments(0, 0, 0, 1000L, 32768)]
    [Arguments(0, 8192, 0, 1000L, 8192)]
    [Arguments(0, 65536, 0, 1000L, 32768)]
    [Arguments(200000, 0, 0, 1000L, 32768)]
    [Arguments(2000, 0, 0, 1001L, 948)]
    [Arguments(2000, 100, 0, 1001L, 100)]
    [Arguments(2000, 0, 1001, 1001L, 948)]
    public async Task Output_budget_obeys_context_margin_and_model_limits(
        int contextWindow, int modelOutputLimit, int modelInputLimit, long estimate, int expected)
    {
        var model = new LLMModel("model", "provider")
        {
            ContextWindow = contextWindow,
            MaxOutputTokens = modelOutputLimit,
            MaxInputTokens = modelInputLimit,
        };
        var budget = new ProviderTokenBudget();

        _ = await Assert.That(budget.CalculateMaximumOutputTokens(Selector, estimate, model)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(120000L, 147776L, 24835)]
    [Arguments(121000L, 148776L, 23785)]
    public async Task Actual_usage_reserves_five_percent_inside_context_window(
        long currentEstimate, long expectedInput, int expectedOutput)
    {
        var model = new LLMModel("model", "provider") { ContextWindow = 180000 };
        var budget = new ProviderTokenBudget();
        budget.ObserveUsage(Selector, 120000, LLMEvent.Completed("stop", 147776, 0, 1000, "reply", []));
        var calibratedInput = budget.EstimateInputTokens(Selector, currentEstimate);
        var maximumOutput = budget.CalculateMaximumOutputTokens(Selector, currentEstimate, model);

        _ = await Assert.That(calibratedInput).IsEqualTo(expectedInput);
        _ = await Assert.That(maximumOutput).IsEqualTo(expectedOutput);
        _ = await Assert.That(calibratedInput + maximumOutput + (long)Math.Ceiling(calibratedInput * 0.05))
            .IsLessThanOrEqualTo(model.ContextWindow);
    }

    [Test]
    [Arguments(2000, 1400, 1500, "The conversation exceeds the selected model input limit.")]
    [Arguments(0, 1400, 1500, "The conversation exceeds the selected model input limit.")]
    [Arguments(1500, 0, 1500, "The conversation leaves no output capacity in the selected model context window.")]
    [Arguments(1575, 0, 1500, "The conversation leaves no output capacity in the selected model context window.")]
    [Arguments(1000, 0, 1500, "The conversation leaves no output capacity in the selected model context window.")]
    public async Task Exhausted_calibrated_input_or_output_capacity_throws(
        int contextWindow, int modelInputLimit, int actualInput, string expectedMessage)
    {
        var model = new LLMModel("model", "provider")
        {
            ContextWindow = contextWindow,
            MaxInputTokens = modelInputLimit,
        };
        var budget = new ProviderTokenBudget();
        budget.ObserveUsage(Selector, 1000, LLMEvent.Completed("stop", actualInput, 0, 0, "reply", []));

        _ = await Assert.That(() => budget.CalculateMaximumOutputTokens(Selector, 1000, model))
            .ThrowsExactly<InvalidOperationException>().WithMessage(expectedMessage);
    }
}
