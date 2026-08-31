namespace Parrot.Agent;

internal interface IMode : IAgentProfile
{
    void Prepare();

    ModeCompletionOutcome Complete(string sessionId, string messageId);
}
