using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class ToolCycleImageBudgetTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Restores_usage_and_overflow_without_sharing_state(bool restoreOverflow)
    {
        var budget = new ToolCycleImageBudget(10, TestModels.PromptTemplates);
        budget.RestoreAccepted(6);
        if (restoreOverflow)
        {
            budget.RestoreExceeded();
        }

        _ = await Assert.That(budget.TryAccept(4)).IsEqualTo(!restoreOverflow);
        _ = await Assert.That(budget.TryAccept(5)).IsFalse();
        _ = await Assert.That(budget.TryAccept(1)).IsFalse();
        _ = await Assert.That(budget.IsExceeded).IsTrue();

        var independentBudget = new ToolCycleImageBudget(10, TestModels.PromptTemplates);
        _ = await Assert.That(independentBudget.TryAccept(10)).IsTrue();
        _ = await Assert.That(independentBudget.IsExceeded).IsFalse();
    }
}
