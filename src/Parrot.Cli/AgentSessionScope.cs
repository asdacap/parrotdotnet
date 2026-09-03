using Parrot.Agent;
using Parrot.Questions;
using Parrot.Queues;

namespace Parrot.Cli;

internal sealed class AgentSessionScope(
    AgentSession session,
    ChildRegistry children,
    ChildQuestionCoordinator childQuestions,
    AgentQueues queues) : IAgentSessionScope
{
    private bool _disposed;

    public AgentSession Session { get; } = session;

    public ChildRegistry ChildRegistry { get; } = children;

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
