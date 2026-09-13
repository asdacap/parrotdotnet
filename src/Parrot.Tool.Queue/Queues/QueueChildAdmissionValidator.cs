using Parrot.Agent;

namespace Parrot.Queues;

internal static class QueueChildAdmissionValidator
{
    public static void Validate(IAgentSessionScope scope) => scope.GetService<IAgentQueues>().ValidateParent();
}
