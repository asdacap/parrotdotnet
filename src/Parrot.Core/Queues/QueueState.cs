namespace Parrot.Queues;

internal sealed record QueueState(
    string OwnerAgentSessionId,
    string OwnerAgentName,
    string ParentAgentSessionId,
    string ParentAgentName,
    string Name,
    string Description,
    int ItemCount);
