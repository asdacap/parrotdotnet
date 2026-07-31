using Parrot.Protocol;

namespace Parrot.Agent;

internal interface IMode : IAgentProfile
{
    void Prepare();

    PlanCompleted? Complete(string sessionId, string messageId);
}
