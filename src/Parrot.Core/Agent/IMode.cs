namespace Parrot.Agent;

/// <summary>Coordinates a session mode's profile and turn lifecycle.</summary>
internal interface IMode
{
    /// <summary>Gets the mode's live profile projection, including its prompt and security policy.</summary>
    IAgentProfile Profile { get; }

    /// <summary>Prepares mode state before the turn reads its profile prompt.</summary>
    void Prepare();

    /// <summary>Completes the captured mode for the specified turn, possibly requesting repair.</summary>
    ModeCompletionOutcome Complete(string sessionId, string messageId);
}
