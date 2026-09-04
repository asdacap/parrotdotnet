using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Queues;
using Parrot.Store;

namespace Parrot.Agent;

internal interface IAgentSession : IAsyncDisposable
{
    string SessionId { get; }

    string Name { get; }

    string ParentSessionId { get; }

    string ParentSessionName { get; }

    int Depth { get; }

    AgentIdentity Identity { get; }

    AgentSessionActivity Activity { get; }

    AgentSelection Selection();

    void UpdateSelection(ModelSelector selectedModel, IMode mode);

    bool Wake(IncomingActivity? activity);

    Task Interrupt(CancellationToken cancellationToken);

    Task Compact(CancellationToken cancellationToken);

    Task Abort(CancellationToken cancellationToken);

    AgentSelection ResolvePolicySelection();

    AgentPolicyLineage ResolvePolicyLineage();

    bool IsIdle();

    bool IsActive();

    bool IsWaitingForIncomingInput();

    Task<IncomingActivity?> WaitForIncomingInput(
        TimeSpan duration,
        TimeProvider timeProvider,
        CancellationToken cancellationToken);

    Task<(Admission Admission, bool FollowUp)> Send(
        IReadOnlyList<ConversationPart> parts,
        string messageId,
        Delivery delivery,
        CancellationToken cancellationToken);

    Task SetGoal(string goal, CancellationToken cancellationToken);

    void ClearGoal();

    Task<bool> ReceiveQueueNotification(
        QueueNotification notification,
        CancellationToken cancellationToken);

    Task<AgentSendResult> Send(string message, CancellationToken cancellationToken);

    Task ReceiveChildQuestion(
        string message,
        string messageId,
        CancellationToken cancellationToken);

    Task ReceiveAgentCompletion(
        string name,
        string message,
        CancellationToken cancellationToken);

    Task ReceiveProcessCompletion(
        string name,
        string message,
        string messageId,
        CancellationToken cancellationToken);

    Task<WaitAgentResult> Wait(
        int yieldAfterMilliseconds,
        CancellationToken cancellationToken);
}
