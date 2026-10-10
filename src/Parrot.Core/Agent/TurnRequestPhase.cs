namespace Parrot.Agent;

/// <summary>Describes the provider request a turn budget admitted.</summary>
internal sealed record TurnRequestPhase(bool FinalPhase, bool EntersFinalPhase, bool Interrupted);
