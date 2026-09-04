using Parrot.Agent;
using Parrot.Questions;
using Parrot.Queues;

namespace Parrot.Cli;

internal sealed class AgentSessionScope(
    IAgentSession session,
    ChildRegistry children,
    ChildQuestionCoordinator childQuestions,
    AgentQueues queues) : IAgentSessionScope
{
    private readonly Lock _gate = new();
    private Task? _shutdown;

    public IAgentSession Session { get; } = session;

    public ChildRegistry ChildRegistry { get; } = children;

    public ChildQuestionCoordinator ChildQuestions { get; } = childQuestions;

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _shutdown ??= ShutDown();
            return new ValueTask(_shutdown);
        }
    }

    private async Task ShutDown()
    {
        try
        {
            await ChildRegistry.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            ChildQuestions.Dispose();
            queues.Dispose();
        }
    }
}
