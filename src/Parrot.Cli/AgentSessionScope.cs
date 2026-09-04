using System.Runtime.ExceptionServices;
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
        Exception? failure = null;
        try
        {
            await ChildRegistry.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await Session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            ChildQuestions.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            queues.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
