namespace Parrot.Agent;

/// <summary>Supplies one mode's prompt and turn lifecycle for a single user session.</summary>
internal interface IModeWorkflow
{
    /// <summary>Renders the mode prompt around the configured profile.</summary>
    string RenderPrompt(IAgentProfile profile);

    /// <summary>Prepares mode state before the turn reads its profile prompt.</summary>
    void Prepare();

    /// <summary>Completes the mode for the ending turn, possibly requesting repair.</summary>
    ModeCompletionOutcome Complete();
}
