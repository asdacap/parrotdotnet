using Parrot.Agent;

namespace Parrot.Queues;

internal sealed record QueueOwnerSnapshot(AgentIdentity Identity, IReadOnlyList<QueueInfo> Queues);
