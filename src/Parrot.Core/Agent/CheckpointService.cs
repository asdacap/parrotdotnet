using Parrot.Store;

namespace Parrot.Agent;

internal sealed class CheckpointService(IEventRepository eventRepository, string agentSessionId) : ICheckpointService
{
    public void SetCheckpoint(string title, long assistantSequence, string toolCallId)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("checkpoint title must not be blank", nameof(title));
        }

        if (assistantSequence <= 0)
        {
            throw new ArgumentException("checkpoint requires a durable assistant tool batch", nameof(assistantSequence));
        }

        _ = eventRepository.RecordCheckpoint(agentSessionId, title, assistantSequence, toolCallId);
    }
}
