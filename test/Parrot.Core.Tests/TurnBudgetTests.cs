using Parrot.Agent;

namespace Parrot.Core.Tests;

internal sealed class TurnBudgetTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Max_turn_switch_controls_final_phase_and_request_admission(bool disabled)
    {
        var budget = new TurnBudget(disabled);
        budget.BeginTurn();
        var first = budget.Admit(1, false);
        _ = await Assert.That(first).IsEqualTo(new TurnRequestPhase(!disabled, !disabled, false));
        _ = budget.CountRequest();

        for (var request = 0; request < 10; request++)
        {
            var next = budget.Admit(1, false);
            _ = await Assert.That(next).IsEqualTo(disabled ? new TurnRequestPhase(false, false, false) : null);
            _ = budget.CountRequest();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Explicit_interruption_still_enters_final_phase_and_bounds_settlement(bool disabled)
    {
        var budget = new TurnBudget(disabled);
        budget.BeginTurn();
        _ = await Assert.That(budget.Admit(10, true)).IsEqualTo(new TurnRequestPhase(true, true, true));
        _ = budget.CountRequest();
        _ = await Assert.That(budget.Admit(10, false)).IsEqualTo(new TurnRequestPhase(true, false, true));
        for (var extension = 0; extension < 4; extension++)
        {
            _ = await Assert.That(budget.TryExtendSettlement()).IsTrue();
        }

        _ = await Assert.That(budget.TryExtendSettlement()).IsFalse();
    }
}
