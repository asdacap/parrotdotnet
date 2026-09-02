using Parrot.Agent;
using Parrot.Questions;
using Parrot.Queues;

namespace Parrot.Cli;

internal sealed class AgentSessionScope(
    AgentSession session,
    ChildQuestionCoordinator childQuestions,
    AgentQueues queues) : IAgentSessionScope
{
    private bool _disposed;

    public AgentSession Session { get; } = session;

    public ChildQuestionCoordinator ChildQuestions { get; } = childQuestions;

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            ChildQuestions.Dispose();
            queues.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
