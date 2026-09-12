using Parrot.Agent;

namespace Parrot.Core.Tests;

internal sealed class RetainedAgentBudgetTests
{
    [Test]
    public async Task Counts_pending_and_retained_reservations_with_exact_once_transitions()
    {
        var budget = new RetainedAgentBudget(2);
        var pending = budget.Reserve();
        var retained = budget.Reserve();
        retained.Commit();
        retained.Commit();

        _ = await Assert.That(budget.Reserve).Throws<AgentRegistryException>();

        pending.Rollback();
        pending.Rollback();
        var replacement = budget.Reserve();
        replacement.Commit();
        replacement.Rollback();

        _ = await Assert.That(budget.Reserve).Throws<AgentRegistryException>();

        retained.Release();
        retained.Release();
        var final = budget.Reserve();
        final.Rollback();
        replacement.Release();
    }
}
