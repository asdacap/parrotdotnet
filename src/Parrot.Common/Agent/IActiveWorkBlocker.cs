namespace Parrot.Agent;

/// <summary>Observes one category of active work for completion reminders.</summary>
internal interface IActiveWorkBlocker
{
    /// <summary>Captures current active work without changing its lifetime or consuming it.</summary>
    ActiveWorkBlockerResult? Observe();
}
