namespace Parrot.Agent;

/// <summary>Persists durable checkpoints for an agent session.</summary>
internal interface ICheckpointService
{
    /// <summary>Persists a checkpoint associated with a durable assistant tool batch.</summary>
    void SetCheckpoint(string title, long assistantSequence, string toolCallId);
}
