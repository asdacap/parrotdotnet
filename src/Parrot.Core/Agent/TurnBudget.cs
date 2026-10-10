namespace Parrot.Agent;

// How many provider requests a turn has made and whether it is in its final
// phase. Final phase: the budget ran out or the model asked to stop via
// agent_interrupt. Settlement tools stay; everything else goes. Each
// settlement batch grants one more final request, bounded by
// MaxSettlementExtensions, then the turn fails as a runaway.
internal sealed class TurnBudget
{
    private const int MaxSettlementExtensions = 4;

    private int _providerRequests;
    private bool _interrupted;
    private bool _finalPhaseActive;
    private int _settlementExtensions;

    public void BeginTurn() => _providerRequests = 0;

    // A completion callback asked for another attempt, which starts the budget over.
    public void Restart()
    {
        _providerRequests = 0;
        _interrupted = false;
        _finalPhaseActive = false;
        _settlementExtensions = 0;
    }

    // Returns null when the turn has run away.
    public TurnRequestPhase? Admit(int maxTurns, bool interruptionRequested)
    {
        _interrupted |= interruptionRequested;
        if (_providerRequests >= maxTurns + _settlementExtensions && !_interrupted)
        {
            return null;
        }

        var finalPhase = _interrupted || _providerRequests + 1 >= maxTurns;
        var entersFinalPhase = finalPhase && !_finalPhaseActive;
        _finalPhaseActive |= finalPhase;
        return new TurnRequestPhase(finalPhase, entersFinalPhase, _interrupted);
    }

    public int CountRequest() => ++_providerRequests;

    // Returns false once the final phase has used up its settlement extensions.
    public bool TryExtendSettlement() => ++_settlementExtensions <= MaxSettlementExtensions;
}
